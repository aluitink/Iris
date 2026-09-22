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
│                                 TestServer bootstrap), TestSeeder, Jwk, JsonDoc, TestCategories,
│                                 LiveGuard/LiveInteropOptions (live-interop gate)
├── Iris.Core.Tests/              focused unit tests ONLY for pure logic:
│                                 sign/verify round-trip (both profiles), tamper detection,
│                                 key generation, IRI helpers, cache TTL/eviction/stale-revalidate
├── Iris.Client.Tests/            integration: client ↔ live TestServer (auth flow, discovery,
│                                 paged enumeration, cache hit/bypass, proxy fallback)
├── Iris.Client.Extensions.Tests/ integration: DI/runtime integration of the client extensions
├── Iris.Server.Tests/            integration: multi-instance federation (follow/accept/create/announce,
│                                 community feed propagation, signature validation across instances,
│                                 WebFinger/NodeInfo, cache refresh, delivery + dead-letter)
├── Iris.Server.Data.Tests/       EF Core (PostgreSQL) persistence provider behavior
├── Iris.WebCrypto.Tests/         browser/WebCrypto signing (JS-interop boundary)
├── Iris.Web.Tests/               production host + WASM client pipeline (expendable during UI
│                                 stabilization — see the web test policy in DEV_LOOP.md)
├── Iris.LiveInterop.Tests/       live peer interop: gated by IRIS_LIVE_INTEROP (see below)
├── SampleServer.Tests/           the SampleServer host end-to-end
└── SampleBlazorClient.Tests/     the SampleBlazorClient host end-to-end
```

The layout is authoritative in `Iris.slnx` — when a project is added, update that list.

## Running the suite: fast vs. full

The full `dotnet test` run is the **source of truth** (all tests, including the slow ones), but it is slow — `Iris.Server.Tests` alone is ~900 tests / ~5.5 min, because every test method builds fresh in-process `TestServer` hosts and drives real multi-hop federation deliveries. For the everyday autonomous-loop "is it green?" check, use the **fast** run, which excludes the slow tests (those that wait out a real delivery backoff budget).

| Run | Command | What it does |
|---|---|---|
| **Fast** (default for the loop) | `dotnet test --filter "Category!=Slow"` | Excludes tests tagged `Category=Slow` (the ones that wait out real backoff). Everything else runs. |
| **Full** (source of truth) | `dotnet test` | Runs every test, including the slow ones. Use this for the final green check before a phase closes. |

**How a test is marked slow.** Apply `[Trait(TestCategories.Category, TestCategories.Slow)]` (constants in `Iris.Testing.TestCategories`) to the test method or class. Only mark tests that actually wait on wall-clock time (a non-zero `DeliveryRetryOptions.BaseDelay`, a real multi-second backoff, or a measured single-test duration >15 s per the blame procedure below) — a short polling `Task.Delay(50)` used to await an async hop is cheap and stays in the fast run. As of 2026-09-21 the tagged surface is the delivery/reliability family in `Iris.Server.Tests` (`DeliveryDeadLetterIntegrationTests`, `DeliveryRetryTests`, `DeliveryReliabilityIntegrationTests`, `RecreationStabilityIntegrationTests`, the outbox fan-out/audience/relay propagation suites) — verify with `grep -rl "TestCategories.Slow" tests --include="*.cs"`.

**Isolating slow / hanging tests (blame).** When the suite stalls or a test runs long, let the runner name the offender instead of guessing:

1. **Per-test timings (fastest signal):** `dotnet test tests/<proj> -v n --logger "console;verbosity=detailed"` — prints a line per test with its duration; anything >15 s is an offender.
2. **Blame mode:** `dotnet test --blame-hang --blame-hang-timeout 15s --blame-hang-dump-type none` — terminates and names the hung test (no dump). Combine with a project filter to keep it quick.
3. **Action on an offender:** if it legitimately waits on wall-clock time, tag it `Slow` (it drops out of the fast run); otherwise **skip it with a date** (`[Fact(Skip = "slow >15s — <date>")]`). **Log every tag/skip** (test name, action, reason, restore-by) in the change doc — no silent deletions; the phase closeout reviews the ledger.

**Honest note on the payoff.** The `Slow` exclusion is a *correct partition* but a *modest* time saving: the backoff waits are a small fraction of total wall-clock, and the real cost is the aggregate of hundreds of test methods each building fresh in-process hosts and driving multi-hop deliveries (xunit creates a fresh test-class instance per method). A larger speedup would come from reusing hosts across a class's methods or cutting delivery round-trips — a structural follow-up, not a per-test tag. Until then, treat the fast run as the loop's quick green check and the full run as the authoritative one.

## Live interop suite (opt-in, gated)

`tests/Iris.LiveInterop.Tests` is the live peer-interop surface — the "server-to-external-server compatibility" goal from the original plan, now **built and in use** (not deferred):

- **Gate:** `LiveGuard.TryRequires()` at the top of each live test. It loads `LiveInteropOptions` from the environment and the suite runs only when **`IRIS_LIVE_INTEROP=1`** *and* the target FQDN is configured (`IRIS_LIVE_INTEROP_BASE_URI` + actor credentials; see `LiveInteropOptions` for the full env-var set). When disabled — the default — live tests **return early as no-op passes**, so the everyday `dotnet test` (fast or full) stays green without contacting any live instance.
- **What it covers today:** peering trust/identity against a **real Lemmy key** (fetch the key live, sign over it, verify Iris-signed requests a Lemmy peer would verify), peering **failure-mode drills** (real Lemmy shared-inbox delivery happy path + unreachable-inbox dead-letter path), and the `IRIS_LIVE_INTEROP` scenario seam (`LiveScenarioTests`) where per-platform targets + admin-API adapters are filled in as peers come up.
- **Running it:** set `IRIS_LIVE_INTEROP=1` + the base-URI/credential vars against a provisioned peer (the dev stacks provide Lemmy/Mastodon peers per [DUAL_DEV_PROTOCOL.md](DUAL_DEV_PROTOCOL.md)), then `dotnet test tests/Iris.LiveInterop.Tests`. Note the local-port drill constants (e.g. `localhost:8091` for Lemmy) assume the peer's host port — align them with the active environment's port block before running.

## Coverage Principle

- Every **phase** ships with the integration tests that prove its end-to-end behavior before it's marked done.
- Prefer one test that federates two instances over five tests that mock each layer.
- The harness is a first-class, maintained artifact — its ergonomics determine how much integration coverage we can afford to write.
