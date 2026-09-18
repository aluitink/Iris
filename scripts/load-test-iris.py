#!/usr/bin/env python3
"""
Iris — production load test script.

Tests the app's key endpoints under concurrent load:
  - GET /ap/v1/health (liveness probe)
  - GET /ap/v1/ready (readiness probe)
  - GET /local/v1/metrics (Prometheus scrape)
  - GET / (home page / WASM bootstrap)

Usage:
  ./scripts/load-test-iris.py                          # 60s, 10 concurrent
  ./scripts/load-test-iris.py --duration 30 --concurrency 20
  ./scripts/load-test-iris.py --base-url http://localhost:8088
  ./scripts/load-test-iris.py --endpoints health,ready,metrics,home

Outputs a summary table with p50/p95/p99 latencies, error rates, and throughput.
"""

import argparse
import asyncio
import json
import ssl
import time
import urllib.request
import urllib.error
from dataclasses import dataclass, field
from typing import List, Dict


@dataclass
class RequestResult:
    endpoint: str
    status: int
    latency_ms: float
    error: str = ""


@dataclass
class EndpointStats:
    endpoint: str
    results: List[RequestResult] = field(default_factory=list)

    @property
    def total(self) -> int:
        return len(self.results)

    @property
    def errors(self) -> int:
        return sum(1 for r in self.results if r.status >= 400 or r.status == 0)

    def _latencies(self) -> List[float]:
        return sorted(r.latency_ms for r in self.results if 0 < r.status < 400)

    def percentile(self, p: float) -> float:
        lats = self._latencies()
        if not lats:
            return 0.0
        idx = min(int(len(lats) * p / 100), len(lats) - 1)
        return lats[idx]

    @property
    def p50(self) -> float:
        return self.percentile(50)

    @property
    def p95(self) -> float:
        return self.percentile(95)

    @property
    def p99(self) -> float:
        return self.percentile(99)

    @property
    def avg(self) -> float:
        lats = self._latencies()
        return sum(lats) / len(lats) if lats else 0.0

    @property
    def error_rate(self) -> float:
        return self.errors / self.total * 100 if self.total else 0.0


def make_request(url: str, endpoint_name: str, timeout: float = 15.0) -> RequestResult:
    start = time.monotonic()
    try:
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        req = urllib.request.Request(url, headers={"User-Agent": "iris-load-test/1.0"})
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as resp:
            resp.read()
            latency = (time.monotonic() - start) * 1000
            return RequestResult(endpoint=endpoint_name, status=resp.status, latency_ms=latency)
    except urllib.error.HTTPError as e:
        latency = (time.monotonic() - start) * 1000
        return RequestResult(endpoint=endpoint_name, status=e.code, latency_ms=latency, error=str(e))
    except Exception as e:
        latency = (time.monotonic() - start) * 1000
        return RequestResult(endpoint=endpoint_name, status=0, latency_ms=latency, error=str(e))


async def run_load_test(base_url: str, duration: int, concurrency: int, endpoints: List[str]):
    endpoint_urls = {
        "health": f"{base_url}/ap/v1/health",
        "ready": f"{base_url}/ap/v1/ready",
        "metrics": f"{base_url}/local/v1/metrics",
        "home": f"{base_url}/",
        "framework": f"{base_url}/_framework/blazor.webassembly.js",
    }

    active = [(name, endpoint_urls[name]) for name in endpoints if name in endpoint_urls]
    if not active:
        print("No valid endpoints selected.")
        return

    print(f"Load test: {base_url}")
    print(f"  Duration: {duration}s")
    print(f"  Concurrency: {concurrency}")
    print(f"  Endpoints: {', '.join(endpoints)}")
    print(flush=True)

    stats: Dict[str, EndpointStats] = {name: EndpointStats(endpoint=name) for name, _ in active}
    stop_time = time.monotonic() + duration
    total = 0
    total_errors = 0

    async def fire_batch():
        """Fire a batch of `concurrency` requests (one per endpoint, round-robin)."""
        nonlocal total, total_errors
        tasks = []
        for i in range(concurrency):
            name, url = active[i % len(active)]
            loop = asyncio.get_event_loop()
            tasks.append(loop.run_in_executor(None, make_request, url, name, 15.0))
        results = await asyncio.gather(*tasks, return_exceptions=True)
        for r in results:
            if isinstance(r, RequestResult):
                stats[r.endpoint].results.append(r)
                total += 1
                if r.status >= 400 or r.status == 0:
                    total_errors += 1

    # Fire batches until the duration expires.
    batch_start = time.monotonic()
    last_report = batch_start
    while time.monotonic() < stop_time:
        await fire_batch()
        now = time.monotonic()
        if now - last_report >= 10:
            elapsed = now - batch_start
            rps = total / elapsed if elapsed > 0 else 0
            print(f"  [{elapsed:.0f}s] {total} requests, {rps:.0f} rps", flush=True)
            last_report = now

    elapsed = time.monotonic() - batch_start

    # Print results.
    print(flush=True)
    print("=" * 72)
    print(f"{'Endpoint':<12} {'Total':>6} {'Errors':>6} {'Err%':>6} {'Avg':>7} {'p50':>7} {'p95':>7} {'p99':>7}")
    print("-" * 72)
    for name, _ in active:
        s = stats[name]
        print(
            f"{name:<12} {s.total:>6} {s.errors:>6} {s.error_rate:>5.1f}% "
            f"{s.avg:>6.0f} {s.p50:>6.0f} {s.p95:>6.0f} {s.p99:>6.0f}"
        )
    print("-" * 72)
    rps = total / elapsed if elapsed > 0 else 0
    err_pct = total_errors / total * 100 if total > 0 else 0
    print(f"{'TOTAL':<12} {total:>6} {total_errors:>6} {err_pct:>5.1f}%   ({rps:.0f} rps over {elapsed:.0f}s)")
    print("=" * 72)

    # Bottleneck analysis.
    print(flush=True)
    print("Bottleneck analysis:")
    found_issue = False
    for name, _ in active:
        s = stats[name]
        if s.total == 0:
            continue
        if s.error_rate > 5:
            print(f"  WARNING: {name} has {s.error_rate:.1f}% error rate — investigate.")
            found_issue = True
        if s.p99 > 1000:
            print(f"  WARNING: {name} p99 = {s.p99:.0f}ms (>1s) — potential bottleneck.")
            found_issue = True
        elif s.p95 > 500:
            print(f"  NOTE: {name} p95 = {s.p95:.0f}ms (>500ms) — monitor.")
            found_issue = True
    if not found_issue:
        print("  No issues detected. All endpoints within acceptable thresholds.")

    # JSON output.
    output = {
        "base_url": base_url,
        "duration_s": round(elapsed, 1),
        "concurrency": concurrency,
        "total_requests": total,
        "total_errors": total_errors,
        "rps": round(rps, 1),
        "endpoints": {
            name: {
                "total": stats[name].total,
                "errors": stats[name].errors,
                "error_rate_pct": round(stats[name].error_rate, 2),
                "avg_ms": round(stats[name].avg, 1),
                "p50_ms": round(stats[name].p50, 1),
                "p95_ms": round(stats[name].p95, 1),
                "p99_ms": round(stats[name].p99, 1),
            }
            for name, _ in active
        },
    }
    print(flush=True)
    print("JSON results:")
    print(json.dumps(output, indent=2))


def main():
    parser = argparse.ArgumentParser(description="Iris load test")
    parser.add_argument("--base-url", default="http://localhost:8088", help="Base URL")
    parser.add_argument("--duration", type=int, default=60, help="Duration in seconds")
    parser.add_argument("--concurrency", type=int, default=10, help="Concurrent requests per batch")
    parser.add_argument("--endpoints", default="health,ready,metrics,home",
                        help="Comma-separated: health,ready,metrics,home,framework")
    args = parser.parse_args()
    endpoints = [e.strip() for e in args.endpoints.split(",")]
    asyncio.run(run_load_test(args.base_url, args.duration, args.concurrency, endpoints))


if __name__ == "__main__":
    main()
