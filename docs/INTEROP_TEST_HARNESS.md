# Live-Interop Test Harness Design (Phase 9)

> Phase 9 is **ideation + preparation only** — no live interop is run here. This document is the
> **test-harness extension** (ROADMAP bullet 5): the structure of the opt-in live-interop suite that
> executes the [compatibility matrix](COMPATIBILITY_MATRIX.md) scenarios against real third-party
> instances, so **Phase 13 is a matter of filling in targets, not building the harness**. It is
> grounded in the **real** test infrastructure that exists today, so the design *extends* it (hoisting
> the copy-pasted two-instance wiring into a shared harness and swapping the transport seam) rather
> than inventing a parallel framework.
>
> Companion docs: [COMPATIBILITY_MATRIX.md](COMPATIBILITY_MATRIX.md) (the scenarios the suite runs),
> [ENUMERATION_DESIGN.md](ENUMERATION_DESIGN.md) (how targets are discovered), [DEPLOYMENT_PREP.md](DEPLOYMENT_PREP.md)
> (the FQDN/TLS + bootstrap the suite depends on), [TESTING.md](TESTING.md) (the coverage principle and
> the deferred live-test note this slice operationalizes).

## 1. Goal

A **separate, opt-in** integration suite (not part of the default `dotnet test` run) that:

1. Boots our Iris server against a **real FQDN** (the operator-provided host from DEPLOYMENT_PREP.md).
2. Runs the matrix scenarios (COMPATIBILITY_MATRIX.md §4) against one or more **real third-party
   instances** (Mastodon, Lemmy, Pleroma, Threads).
3. Asserts the **expected** behavior per scenario (PASS-expected) and **confirms the predicted gaps**
   (the six [GAP] items in COMPATIBILITY_MATRIX.md §5) surface as predicted.
4. Is **gated** by an environment flag + FQDN config so local/dev runs skip it entirely and CI runs it
   as a dedicated job.

The deliverable of Phase 9 is the **design + the in-process skeleton** that makes the live suite a
fill-in-the-targets exercise. No live instance is contacted in this phase.

## 2. What exists today (ground truth — what the design builds on)

Verified against the source. The live suite is a thin layer over these real seams — none of them need
to change.

**The in-process two-instance federation tests** (the model the live suite mirrors):
- Each `Iris.Server.Tests` integration test (e.g. `DeliveryIntegrationTests.cs:32`,
  `CommunityFollowsCommunityIntegrationTests.cs:52`) is `sealed ... : IDisposable` and **copy-pastes its
  own** `StartServer`, `BuildFetcherFor`, `Seed`, `BuildDeliveryWorker`, `WaitForAsync` helpers
  (duplicated across ~9 files). There is **no shared base fixture** for real server wiring.
- `StartServer` (`DeliveryIntegrationTests.cs:282-318`): `WebHostBuilder().ConfigureServices(s => { s.AddActivityPubServer(opts => { opts.BaseUri = …; opts.InstanceName = …; opts.InstanceActorId = …; }); s.AddInMemoryPersistence(); s.AddSingleton<IPersistenceProvider>(persistence); s.AddSingleton<IActorDocumentFetcher>(fetcher); })` then `.Configure(app => { app.UseRouting(); app.UseSignatureValidation(); app.UseEndpoints(e => e.MapActivityPubEndpoints()); })` → `new TestServer(builder)`.
- **The cross-instance wiring is a per-instance `IActorDocumentFetcher` whose client routes over the
  *other* instance's `TestServer.CreateHandler()`** (`DeliveryIntegrationTests.cs:63-64`), so each side
  validates the other's signatures by fetching the other's actor doc.
- **The transport seam**: outbound delivery is driven by a hosted `DeliveryWorker` with
  `transportFactory = () => targetServer.CreateHandler()` (`DeliveryIntegrationTests.cs:200`); a client
  is built by `ActivityPubClientFactory.Create(options, httpHandler)` (`src/Iris.Client/ActivityPubClientFactory.cs:47`),
  where `httpHandler` is the transport. **Swapping `TestServer.CreateHandler()` for a real
  `HttpClientHandler()` pointed at a FQDN is the entire delta between in-process and live.**
- `LazyHandler` (`CommunityFollowsCommunityIntegrationTests.cs:466-496`) defers `targetServer.CreateHandler()`
  until first request to break the chicken-and-egg of bidirectional wiring.
- Assertions poll the receiver's in-memory persistence via `WaitForAsync` (`DeliveryIntegrationTests.cs:90-93`,
  helper `:337-349`) plus xUnit asserts on the stored activity.

**The shared `Iris.Testing` project** (the home the hoisted wiring belongs in):
- `TestServerFactory` (`tests/Iris.Testing/TestServerFactory.cs:14`), `TestServerInstance`
  (`:12`), `FederationTopology` (`:8`) — a multi-instance harness, but **scaffold-stage**: it builds a
  pass-through pipeline (any request → 404) and a placeholder `IHarnessStore`
  (`TestServerFactory.cs:49` "Phase 3 swaps this for the real InMemoryPersistenceProvider", `:69` "No
  endpoints are mapped yet"). The real `StartServer` pipeline has **not** been moved here yet.
- `FederationTopology.Create(int count)` builds N instances with hostnames `a.domain.local`,
  `b.domain.local`, … (`FederationTopology.cs:37,69`).

**Client construction in tests** (the recipe the live client reuses):
- `ActivityPubClientFactory.Create(options, httpHandler)` with `ActivityPubClientOptions { ActorId = … }`
  (`src/Iris.Client/IActivityPubClientOptions.cs:14`); the factory composes `RetryHandler → JsonLdHandler →
  SigningHandler` over the caller-supplied transport (`ActivityPubClientFactory.cs:47-90`). A live client
  is `factory.Create(options, new HttpClientHandler())` against a real FQDN — **no pipeline change**.
- Basic-auth session flow (for logging into our own server): `BasicAuthClientAuthenticator` +
  `IrisClientBuilder`/`IrisClientFactory` (`src/Iris.Client.Extensions/IrisClientFactory.cs:64-87`).

**Config**:
- Server: `ActivityPubServerOptions` (`src/Iris.Server/ActivityPubServerOptions.cs:14`) — `BaseUri`,
  `InstanceName`, `InstanceActorId`, `CachePolicies`, `ProxySettings`. Configured inline in every test's
  private `StartServer` (no shared "test options" builder today).
- Client: `ActivityPubClientOptions` (`src/Iris.Client/IActivityPubClientOptions.cs:9`) — `ActorId`
  (required), `HttpClientTimeout`, `Caches`, `EnableRetry`/`MaxRetryAttempts`, `ProxyBaseUrl`/`ProxyCredentials`.

**The existing opt-in gate convention** (the pattern the live suite mirrors):
- The **only** opt-in gate in the repo is `scripts/docker-smoke-test.sh` — it **skips (exit 0) when
  Docker/daemon/compose is unavailable** and honors `IRIS_COMPOSE_KEEP=1`. There is **no env-flag/skip
  pattern in any C# test project** today (all plain `[Fact]`).
- The **planned** convention is already documented in `docs/TESTING.md:34-42` ("Live Mastodon
  Compatibility Test (deferred)"): a **separate, opt-in** suite, "Gated behind an environment flag (e.g.
  `IRIS_MASTODON_TEST=1`) and the Docker Compose environment, so CI can run it as a dedicated job while
  local/dev runs skip it."

## 3. The design

### 3.1 A new opt-in test project: `tests/Iris.LiveInterop.Tests`

A **separate project** registered in `Iris.slnx` (not a folder/trait in `Iris.Server.Tests` — a
trait-based folder would still run under the default `dotnet test` and would violate the "not part of
the default run" requirement). It references `Iris.Testing`, `Iris.Client`, `Iris.Client.Extensions`,
`Iris.Server`, `Iris.Core`, and `Iris.Server.InMemory`. Its `csproj` is the only place the suite is
wired; `dotnet test Iris.slnx` (the default command) does **not** include it in a normal local run
because the project's tests are runtime-gated (§3.3) and CI excludes the project by default (a
dedicated CI job includes it, §3.5).

> This project does **not** exist yet in Phase 9. Phase 9 produces this design + the **in-process
> skeleton** (§3.6) that proves the harness structure compiles and the in-process path runs; Phase 13
> fills in the live targets and the env-flag wiring. No new NuGet package is introduced (the project
> references only existing solution projects + `xunit` + `Microsoft.AspNetCore.TestHost`, both already
> in the dependency set).

### 3.2 Hoist the two-instance wiring into `Iris.Testing` (the shared harness)

The single most valuable refactor the live suite enables: **move the copy-pasted per-test wiring into
`Iris.Testing`** so the live suite (and future in-process tests) reuse one implementation instead of
~9 divergent copies. Concretely, `Iris.Testing` gains:

- **`ActivityPubHost`** — a typed wrapper that replaces the private `StartServer` + `TestServerInstance`:
  it builds the **real** server pipeline (`AddActivityPubServer` + `AddInMemoryPersistence` +
  `IPersistenceProvider` + `IActorDocumentFetcher` + `UseRouting`/`UseSignatureValidation`/
  `MapActivityPubEndpoints`) over a `TestServer`, and exposes `BaseUri`, `ActorIri`, an `HttpClient`
  (`CreateClient()`), the `IActorDocumentFetcher`, the `IPersistenceProvider` (for assertion polling),
  and a `Func<HttpMessageHandler> Transport` (the seam). This is the real-pipeline version of
  `TestServerInstance` (which today serves 404s).
- **`FederationTopology` (real)** — `Create(int count)` builds N `ActivityPubHost`s wired cross-instance
  via per-instance `IActorDocumentFetcher`s (the `LazyHandler` chicken-and-egg fix generalized), replacing
  the scaffold that maps no endpoints.
- **`LiveInteropOptions`** — the config record the live suite reads (see §3.4): `OurFqdn`,
  `OurBaseUri`, `OurActorIri`, `OurUsername`/`OurPassword`, `IrisFqdn` (the "other Iris" sanity-check
  instance), `Targets` (a list of `InteropTarget`), `RequestBudget`, `RateLimitPerSecond`.
- **`InteropTarget`** — a record describing one third-party platform under test: `Platform` (enum:
  `Mastodon`/`Lemmy`/`Pleroma`/`Threads`), `BaseUri`, `SeedAccounts` (the handles to resolve, from
  ENUMERATION_DESIGN.md), `AdminApiBase`/`AdminToken` (for the platform's REST API to create test
  accounts/posts/follows — see TESTING.md:39), and per-platform capability notes.
- **`ScenarioRunner`** — the driver that, given an `ActivityPubHost` (ours) + an `InteropTarget` + a
  matrix scenario id (F1, C1, G2, SIG1, …), executes the scenario and returns a `ScenarioResult`
  (`Passed` / `GapConfirmed` / `Mismatch`, with the evidence). This is the single seam between the
  compatibility matrix (scenario definitions) and the live suite (scenario execution).
- **`WaitForAsync`** (hoisted) — the polling helper for asserting against a receiver's state (in-process:
  the `IPersistenceProvider`; live: a read-back via the client's `GetCollectionItemsAsync`/`GetObjectAsync`).

> Hoisting is the **design + in-process skeleton** in Phase 9. The actual move of the ~9 copy-pasted
> helpers into `Iris.Testing` and the deletion of the per-test copies is a **Phase 10 (code
> consolidation)** task — Phase 9 establishes the shape so Phase 10 has a target. Phase 9's skeleton
> proves the shape compiles and the in-process path runs; it does not yet delete the duplicates.

### 3.3 Gating: the env-flag + runtime-skip pattern

Because no skip pattern exists in the C# tests today, the live suite introduces one, mirroring the
Docker smoke script's "skip when the external environment is unavailable" convention:

- **Gate env var**: `IRIS_LIVE_INTEROP=1` (the suite's master switch; the `IRIS_MASTODON_TEST=1` name in
  TESTING.md:42 is generalized to all platforms). When unset, **every test in the project skips** (a
  shared `LiveGuard.Requires()` helper called at the top of each test — an xUnit `Assert.Skip`-style
  early return, or a constructor-time guard that marks the test skipped). This is the C# analogue of
  `docker-smoke-test.sh`'s `exit 0` when Docker is unavailable.
- **FQDN config**: the suite reads `LiveInteropOptions` from environment variables / a config file
  (`IRIS_LIVE_INTEROP_FQDN`, `IRIS_LIVE_INTEROP_BASE_URI`, `IRIS_LIVE_INTEROP_ACTOR`, target
  definitions). When the FQDN is not configured (i.e. the operator has not provisioned it yet — the
  Phase 13 blocker), the suite skips with a clear message, **not** a failure.
- **Per-target skip**: a target whose `BaseUri` is not reachable (connection probe fails) is skipped
  for that target's scenarios, so one unreachable platform does not fail the whole run.
- **Budget guard**: `LiveInteropOptions.RequestBudget` + `RateLimitPerSecond` are enforced by the
  `ScenarioRunner` (a simple token bucket), so a runaway scenario cannot hammer a third-party instance
  (the ENUMERATION_DESIGN.md §5 guardrails applied to the live suite).

The net effect: `dotnet test tests/Iris.LiveInterop.Tests` with no env config → all tests **skip**
(exit 0, no failure). With `IRIS_LIVE_INTEROP=1` + FQDN + targets → the matrix scenarios run live.

### 3.4 The scenario model (mapping the matrix → executable scenarios)

Each compatibility-matrix scenario (COMPATIBILITY_MATRIX.md §4) becomes a **`Scenario`** with:

- **`Id`** — the matrix id (F1, F2, C1, C3, G1, S1, P1, T1, SIG1, …).
- **`Direction`** — `Out` (we → platform) or `In` (platform → us).
- **`Expectation`** — `PassExpected` or `Gap` (the six predicted gaps). For a `Gap` scenario, the
  assertion is that the gap **surfaces as predicted** (e.g. C1: "remote follower's inbox did NOT
  receive a signed `Create`"), not that it fails.
- **`Act`** — the driver logic (uses `ScenarioRunner`): e.g. F1 = "drive the platform account to follow
  our actor via its admin API; wait; assert our `AcceptActivityHandler` recorded the edge and an
  `Accept` was delivered to the platform inbox."
- **`Assert`** — the check (in-process: poll `IPersistenceProvider`; live: read back via the client +
  the platform's API).

The `ScenarioRunner` is platform-agnostic about *our* side (it always uses `ActivityPubHost`) and
platform-specific only about *the other side* (the `InteropTarget`'s admin API). This is why the suite
is "fill in targets": adding Mastodon = one `InteropTarget` + its admin-API adapter; the scenario
definitions and our-side drivers are shared.

### 3.5 CI wiring (the dedicated job)

- **Default CI job**: `dotnet test Iris.slnx` **excludes** `Iris.LiveInterop.Tests` (the project is in
  the solution but the default test command filters it out, or the job passes `--filter`/a project
  list). This preserves the "not part of the default run" requirement.
- **Dedicated live job**: a separate CI job that sets `IRIS_LIVE_INTEROP=1` + the FQDN/target secrets
  and runs `dotnet test tests/Iris.LiveInterop.Tests`. It is **opt-in** (only runs when the operator
  has provisioned the FQDN and enabled the job) and is the Phase 13 execution surface. This mirrors the
  Phase 8 Docker smoke script's opt-in CI job (deferred there for the same reason — no baseline CI
  workflow yet).

### 3.6 The Phase 9 deliverable (design + in-process skeleton)

Phase 9 produces, **without contacting any live instance**:

1. **This design doc** (the structure above).
2. **The in-process skeleton** in `tests/Iris.LiveInterop.Tests` + the `Iris.Testing` hoisted types
   (§3.2) — **compiled but live-gated**: the `ActivityPubHost`/`FederationTopology`(real) types are
   implemented (they run the real server pipeline in-process, replacing the 404 scaffold), and a small
   set of **in-process self-tests** prove the harness structure works (e.g. "two `ActivityPubHost`s
   federate a Follow/Accept loop in-process through the hoisted harness" — reusing the real pipeline,
   no third-party instance). These in-process tests run in the default `dotnet test` (they need no
   FQDN) and prove the harness compiles and the in-process path is green.
3. **The live scenario tests** are **written but runtime-skipped** (gated by `IRIS_LIVE_INTEROP=1` +
   FQDN). They compile (so the suite is structurally complete) and skip in the default run (so no live
   instance is contacted). Filling them in for Phase 13 = populating `LiveInteropOptions` targets +
   the per-platform admin-API adapters, **not** building the harness.

> The in-process self-tests are the "its tests" for this slice: they prove the hoisted harness
> (`ActivityPubHost`, real `FederationTopology`) correctly runs the real server pipeline and federates
> two instances in-process. The live scenario tests are the Phase 13 payload; they ship skipped.

## 4. What this design deliberately does NOT do

- **No live interop in Phase 9.** No third-party instance is contacted; no FQDN is required to build
  or run the in-process self-tests. The live scenario tests are written but skip.
- **No deletion of the ~9 copy-pasted helpers in `Iris.Server.Tests`.** The hoist *establishes the
  shape* in `Iris.Testing`; actually migrating the existing tests to the shared harness and deleting the
  duplicates is **Phase 10 (code consolidation)**. Phase 9 adds the shared types; it does not yet
  rewire the existing tests onto them.
- **No new NuGet package.** The new project references only existing solution projects + `xunit` +
  `Microsoft.AspNetCore.TestHost` (both already in the dependency set). No package addition is noted in
  ROADMAP.md because there is none.
- **The remaining Phase 9 bullet** (risk & gap register) is a separate slice; it promotes
  COMPATIBILITY_MATRIX.md §5's six gaps + the Threads/Lemmy unknowns into tracked items, and the
  harness's `Gap`-scenario assertions are how those gaps get confirmed live in Phase 13.

## 5. Open decision recorded

**Decision — the live suite is a separate, runtime-gated project, not a trait in `Iris.Server.Tests`.**
A folder + `[Trait("live")]` inside `Iris.Server.Tests` would still be compiled and *attempted* by the
default `dotnet test` (xUnit has no built-in "skip unless env var set" without per-test guards), and a
misconfigured run could contact a live instance or fail the default build. A separate project
(`Iris.LiveInterop.Tests`) with a `LiveGuard.Requires()` runtime skip + a CI `--filter`/project-list
exclusion is the cleanest guarantee that the default run never touches a third-party instance and
never fails on a missing FQDN. Recorded in CHANGELOG.md (Resolved Decisions).
