# Iris — Testing Strategy

> Part of the [Iris plan](../PLAN.md). See also [Architecture](ARCHITECTURE.md), [Projects](PROJECTS.md), [Roadmap](ROADMAP.md), [Coding Style](CODING_STYLE.md).

**Philosophy: integration-first, end-to-end.** We maximize coverage with a small number of high-fidelity integration tests that exercise real HTTP, real signing, real persistence, and real federation — instead of a large sprawl of isolated unit tests. Unit tests exist only where they add value that integration tests can't reach (pure crypto edge cases, IRI parsing, cache TTL/eviction logic).

## In-Process Multi-Instance Test Harness

- **`TestServer` harness** (in a shared `tests/Iris.Testing` project): spins up **multiple fully in-process `WebApplication` instances**, each with:
  - Its own `Iris.Server` pipeline + `Iris.Server.InMemory` persistence.
  - A **distinct hostname** from the `*.domain.local` range — **start with `a.domain.local` and `b.domain.local`** for basic federation; the harness scales to **N instances** (`a.domain.local`, `b.domain.local`, `c.domain.local`, …) for relay/fan-out scenarios. Each instance has its own system identity + key.
  - A real `HttpClient` wired to the instance's `TestServer`/Kestrel endpoint so requests go through the full HTTP stack (headers, content negotiation, signature validation, caching).
- **Instance-to-instance federation**: tests create actors on instance A, follow actors/communities on instance B, and assert that activities are delivered, signature-validated, stored, and visible in feeds/outboxes on the receiving instance. This proves **instance-to-instance compatibility** — the core property of a federated protocol.
- **N-instance relay/fan-out**: the harness is designed from the start to spin up **N servers** so we can test relay and fan-out topologies (one actor followed by many, a relay re-broadcasting, etc.) — not just pairwise federation.
- **Client against server**: the `Iris.Client` (including proxy fallback) is exercised against these live instances, including the Basic-auth → private-key → signed-request flow.
- **Distinct hostnames matter**: signature validation, WebFinger, IRI resolution, and cache keys are all hostname-sensitive. The harness guarantees each instance has a unique, resolvable hostname so these paths are genuinely exercised.

## Test Project Layout

```
tests/
├── Iris.Testing/                 shared harness: TestServer factory, multi-instance topology,
│                                 actor/credential fixtures, assertion helpers
├── Iris.Core.Tests/              focused unit tests ONLY for pure logic:
│                                 sign/verify round-trip (both profiles), tamper detection,
│                                 key generation, IRI helpers, cache TTL/eviction/stale-revalidate
├── Iris.Client.Tests/            integration: client ↔ live TestServer (auth flow, discovery,
│                                 paged enumeration, cache hit/bypass, proxy fallback)
└── Iris.Server.Tests/            integration: multi-instance federation (follow/accept/create/announce,
                                  community feed propagation, signature validation across instances,
                                  WebFinger/NodeInfo, cache refresh)
```

## Live Mastodon Compatibility Test (deferred — far later)

- **Deferred until instance-to-instance viability is first confirmed** with our own in-process servers. This is a downstream goal, not part of the near-term phases. See [Phase 8](ROADMAP.md#phase-8--live-mastodon-compatibility-test-deferred--after-instance-to-instance-viability).
- A **separate, opt-in** integration suite (not part of the default `dotnet test` run) that:
  - Runs in a **fully isolated, routable Docker Compose environment**: our server instance + a **Dockerized Mastodon** (+ optional relay) on an internal network with routable hostnames.
  - Orchestrates Mastodon via its **admin/REST API** to create test accounts, posts, and follows.
  - Runs our Iris server instance against it: our instance follows a Mastodon account, receives its posts, and (where possible) posts to Mastodon and confirms delivery.
  - Asserts **server-to-external-server compatibility** — the ultimate interop proof.
- Gated behind an environment flag (e.g. `IRIS_MASTODON_TEST=1`) and the Docker Compose environment, so CI can run it as a dedicated job while local/dev runs skip it.

## Coverage Principle

- Every **phase** ships with the integration tests that prove its end-to-end behavior before it's marked done.
- Prefer one test that federates two instances over five tests that mock each layer.
- The harness is a first-class, maintained artifact — its ergonomics determine how much integration coverage we can afford to write.
