# Iris — Testing Strategy

> Part of the [Iris plan](../../PLAN.md). See also [Architecture](ARCHITECTURE.md), [Projects](PROJECTS.md), [Phase Ledger](../ROADMAP.md), [Coding Style](CODING_STYLE.md).

**Philosophy: integration-first, end-to-end.** We maximize coverage with a small number of high-fidelity integration tests that exercise real HTTP, real signing, real persistence, and real federation — instead of a large sprawl of isolated unit tests. Unit tests exist only where they add value that integration tests can't reach (pure crypto edge cases, IRI parsing, cache TTL/eviction logic).

## In-Process Multi-Instance Test Harness

 - **`TestServer` harness** (in a shared `tests/Iris.Testing` project): spins up **multiple fully in-process `WebApplication` instances** through a single shared bootstrap, **`ActivityPubHostFactory.Create(ActivityPubHostOptions)`**. Each instance has:
   - Its own `Iris.Server` pipeline (`AddActivityPubServer` + `AddInMemoryPersistence` + `UseSignatureValidation` + `MapActivityPubEndpoints`) + `Iris.Server.InMemory` persistence.
   - A **distinct hostname** from the `*.domain.local` range — **start with `a.domain.local` and `b.domain.local`** for basic federation; the harness scales to **N instances** (`a.domain.local`, `b.domain.local`, `c.domain.local`, …) for relay/fan-out scenarios. Each instance has its own system identity + key.
   - A real `HttpClient` wired to the instance's `TestServer`/Kestrel endpoint so requests go through the full HTTP stack (headers, content negotiation, signature validation, caching).
   - The options object captures the union of the per-test seams — `Fetcher`, `DeliveryTransport`, `CredentialValidator`, `ProxySettings`, `ExtraLocalActors`, `CommunityKey`, `ExtraServices`, `RegisterLocalKey`, and `IdentityKeys` (a custom signer triple) — so the integration tests no longer each carry a private `StartServer` copy.
   - **Seeding** via the shared **`TestSeeder`** (`SeedPerson`/`SeedPersonWithKey`/`SeedCommunity`/`SeedCommunityWithKey`/`AddMember`/`AddCreateActivity`), **`Jwk.ExtractComponent`**, and **`JsonDoc`** (`GetItems`/`ItemId`) — the per-test `Seed`/`ExtractJwkComponent`/`GetItems`/`ItemId` copies were removed in Phase 10.
- **Instance-to-instance federation**: tests create actors on instance A, follow actors/communities on instance B, and assert that activities are delivered, signature-validated, stored, and visible in feeds/outboxes on the receiving instance. This proves **instance-to-instance compatibility** — the core property of a federated protocol.
- **N-instance relay/fan-out**: the harness is designed from the start to spin up **N servers** so we can test relay and fan-out topologies (one actor followed by many, a relay re-broadcasting, etc.) — not just pairwise federation.
- **Client against server**: the `Iris.Client` (including proxy fallback) is exercised against these live instances, including the Basic-auth → private-key → signed-request flow.
- **Distinct hostnames matter**: signature validation, WebFinger, IRI resolution, and cache keys are all hostname-sensitive. The harness guarantees each instance has a unique, resolvable hostname so these paths are genuinely exercised.

## Test Project Layout

```
tests/
├── Iris.Testing/                 shared harness: ActivityPubHostFactory (the single real-pipeline
│                                 TestServer bootstrap), TestSeeder, Jwk, JsonDoc, assertion helpers
├── Iris.Core.Tests/              focused unit tests ONLY for pure logic:
│                                 sign/verify round-trip (both profiles), tamper detection,
│                                 key generation, IRI helpers, cache TTL/eviction/stale-revalidate
├── Iris.Client.Tests/            integration: client ↔ live TestServer (auth flow, discovery,
│                                 paged enumeration, cache hit/bypass, proxy fallback)
└── Iris.Server.Tests/            integration: multi-instance federation (follow/accept/create/announce,
                                  community feed propagation, signature validation across instances,
                                  WebFinger/NodeInfo, cache refresh)
```

## Running the suite: fast vs. full

The full `dotnet test` run is the **source of truth** (all tests, including the slow ones), but it is slow — `Iris.Server.Tests` alone is ~900 tests / ~5.5 min, because every test method builds fresh in-process `TestServer` hosts and drives real multi-hop federation deliveries. For the everyday autonomous-loop "is it green?" check, use the **fast** run, which excludes the slow tests (those that wait out a real delivery backoff budget).

| Run | Command | What it does |
|---|---|---|
| **Fast** (default for the loop) | `dotnet test --filter "Category!=Slow"` | Excludes tests tagged `Category=Slow` (the ones that wait out real backoff). Everything else runs. |
| **Full** (source of truth) | `dotnet test` | Runs every test, including the slow ones. Use this for the final green check before a phase closes. |

**How a test is marked slow.** Apply `[Trait(TestCategories.Category, TestCategories.Slow)]` (constants in `Iris.Testing.TestCategories`) to the test method or class. Only mark tests that actually wait on wall-clock time (a non-zero `DeliveryRetryOptions.BaseDelay`, a real multi-second backoff) — a short polling `Task.Delay(50)` used to await an async hop is cheap and stays in the fast run. Currently tagged: `DeliveryDeadLetterIntegrationTests` (waits the full default retry budget) and `DeliveryRetryTests.TransientFailure_WaitsConfiguredBackoff_BetweenRetries` (a real 150ms backoff).

**Honest note on the payoff.** The `Slow` exclusion is a *correct partition* but a *small* time saving (~1s of ~5.5 min): the backoff waits are a tiny fraction of total wall-clock. The real cost is the aggregate of ~900 test methods each building hosts and driving multi-hop deliveries (xunit creates a fresh test-class instance per method). A larger speedup would come from reusing hosts across a class's methods or cutting delivery round-trips — a structural follow-up, not a per-test tag. Until then, treat the fast run as the loop's quick green check and the full run as the authoritative one.

**Pending reclassification (requested 2026-09-06).** On the **next full `dotnet test` run**, reclassify any test method that takes longer than **5 seconds** as `Slow` (add `[Trait(TestCategories.Category, TestCategories.Slow)]`). This broadens the current wall-clock-backoff-only rule to a measured threshold: it will catch slow tests whose cost is host startup / multi-hop delivery rather than an explicit backoff wait. Record the newly-tagged tests + the resulting fast-vs-full split in the change doc for that run.

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
