# 136.1 — Lemmy interop foundation (env + observability): federation trace collector

**Date:** 2026-09-14
**Slice:** 136.1 (Lemmy interop foundation — observability half)
**Status:** **COMPLETE.** A process-local federation trace now captures every inbound and outbound
federation request as a single shared, time-ordered artifact, exposed at a read-only operator
endpoint. This is the diagnostic tool for the 135.1b(4) signature blocker (Lemmy rejecting Iris
outbound signatures).

## What was built

### New observability seam (`src/Iris.Server/Observability/`)

- **`FederationDirection`** — `Inbound` / `Outbound` enum.
- **`FederationTraceEntry`** — an immutable record: `Timestamp`, `Direction`, `Method`, `Url`,
  `Status`, `PeerIri`, `ActorIri`, `ActivityType`. One entry per captured request.
- **`IFederationTraceCollector`** — `Record(entry)`, `Snapshot()`, `Clear()`.
- **`InMemoryFederationTraceCollector`** — bounded ring-buffer implementation. Default capacity
  1000 (oldest dropped when full); capacity 0 disables capture (the worker/inbox behave exactly as
  before). Thread-safe via an internal lock.

### Capture points

- **Outbound** — `DeliveryWorker` (new optional `IFederationTraceCollector` dependency on both
  constructors). A successful delivery records an `Outbound` entry with the recipient inbox URL, the
  acting actor (the `X-Iris-Actor` override when present, else the instance actor), the activity
  type, and the HTTP status. A transport failure (network error / timeout) records status `0`.
- **Inbound** — the inbox handler (`HandleInboxPostAsync` in `ActivityPubServerExtensions.cs`)
  records an `Inbound` entry at **every** return point: 401 (unsigned / bad signature), 404 (unknown
  actor), 429 (rate-limited), 400 (empty body), 202 (tombstone), 400 (unrecognizable), 500 (exception),
  202 (accepted). When signature verification succeeded, the entry is attributed to the **verified
  signer IRI**; otherwise `ActorIri` is null.

### Operator read path

- **`GET /local/v1/federation-trace`** (`WebAppFactory.MapFederationTraceEndpoint`, wired after
  `MapMetricsEndpoint`). Returns the snapshot as JSON (`{ count, entries[] }`). Like
  `/local/v1/metrics` it is an operator-internal endpoint (no auth, not reverse-proxied publicly).
  The snapshot is **process-local** (a multi-replica deployment reports per-replica) — an accepted
  trade-off for Phase 136.1; a cross-replica store is out of scope here.

## Live verification (against the running `iris.luit.ink` instance)

After a full Release rebuild + container redeploy, the endpoint is live and captures real traffic:

```
$ curl -s http://localhost:8088/local/v1/federation-trace
{"count":1,"entries":[{"timestamp":"2026-09-14T01:29:33.0823318+00:00","direction":"Inbound",
  "method":"POST","url":"/ap/v1/u/bob/inbox","status":401,
  "peerIri":"https://iris.luit.ink/ap/v1/u/bob","actorIri":null,"activityType":null}]}
```

This is the result of a deliberately **unsigned** `POST` to bob's inbox — the trace correctly
records `direction=Inbound`, `method=POST`, the inbox path, `status=401` (signature required), and
the target actor as the peer, with `actorIri=null` (no verified signer). That is exactly the
diagnostic signal the 135.1b(4) blocker needs: an operator can now see, on one line, the request
line + status + (when present) the verified signer for a failed exchange.

The **outbound** capture path is not exercised by a live post this turn (publishing to the outbox
requires a signature from the acting local actor, and the app does not expose private keys for
scripting). It is instead covered end-to-end by the integration tests below, which drive a real
`DeliveryWorker` + real signing pipeline against a failable transport.

## Tests

- **`FederationTraceIntegrationTests`** (8 cases, `tests/Iris.Server.Tests/Observability/`): outbound
  success (asserts recipient inbox, instance actor, activity type, status 200); acting-actor override
  (asserts the `X-Iris-Actor` actor is attributed, not the instance actor); permanent failure (status
  400 captured); per-attempt retry capture (a 500→200 sequence yields two entries, 500 then 200);
  collector unit behavior (bounded buffer drops oldest, zero-capacity disables, `Clear`, stable
  snapshot copy).
- **`FederationTraceEndpointTests`** (2 cases, `tests/Iris.Web.Tests/`): the `/local/v1/federation-trace`
  endpoint returns the collector snapshot as JSON (non-empty + empty).
- Fixed `GracefulShutdownDrainTests` for the new `DeliveryWorker` constructor signature.

Full suite: **1371 passed, 17 skipped, 0 failed.**

## What is NOT in this slice

- No cross-replica / persistent trace store (process-local only).
- No trace-driven alerting or retention policy (the bounded buffer is the retention).
- No fix to the 135.1b(4) signature interop itself — this slice is the **observability** half of
  136.1; the fix lands in 136.3 (HTTP signatures + canonical verification matrix) using the trace as
  the diagnostic input.
