# 101 — Phase 13.8: CI gating/opt-in model for the live interop suite

> 2026-08-30 · Phase 13 (Live Federation Compatibility) · sub-slice 13.8

## What was built

The runtime gate for the live-interop suite, per the design in `INTEROP_TEST_HARNESS.md` §3.3. A
separate, opt-in test project (`Iris.LiveInterop.Tests`) that is runtime-gated so the default
`dotnet test` run stays green (no contact with a live instance, no failure on a missing FQDN). When
the operator has provisioned the FQDN and set `IRIS_LIVE_INTEROP=1`, the live scenario tests run.

## New types (in `Iris.Testing`)

- **`InteropPlatform`** (enum) — `Mastodon`, `Lemmy`, `Pleroma`, `Threads`.
- **`InteropTarget`** (record) — a third-party platform under test: `Platform`, `Name`, `BaseUri`,
  `SeedAccounts`, `AdminApiBase`, `AdminToken`. The `AdminApiBase`/`AdminToken` are the seam for
  driving the platform's admin API (creating test accounts/posts/follows).
- **`LiveInteropOptions`** (record) — the suite config: `IsEnabled` (the `IRIS_LIVE_INTEROP=1` master
  switch), `OurBaseUri`, `OurActorIri`, `OurUsername`/`OurPassword`, `Targets`, `RequestBudget`,
  `RateLimitPerSecond`. `TryLoadFromEnvironment` reads the `IRIS_LIVE_INTEROP*` env vars; `CanRun`
  requires both `IsEnabled` and a configured `OurBaseUri`.
- **`LiveGuard`** — `TryRequires(out options)` gates a live test: returns `false` when the suite is
  disabled or the FQDN is not configured (the test returns early — a no-op, reported as passed, not
  failed).

## New project (`Iris.LiveInterop.Tests`)

Registered in `Iris.slnx`. References `Iris.Testing`, `Iris.Core`, `Iris.Client`, `Iris.Server`,
`Iris.Server.InMemory` (all existing solution projects — no new NuGet package).

- **`LiveGatingTests`** (7 unit tests) — prove the gate works: master switch off → `false`; master
  switch on + no FQDN → `false` (cannot run); master switch on + FQDN set → `true` (can run);
  custom/default/invalid budget and rate limit handling.
- **`InteropTargetTests`** (4 unit tests) — `DisplayName`, field-by-field equality (`IReadOnlyList`
  is compared by reference, not value), all four platforms.
- **`LiveScenarioTests`** (4 stubs, runtime-gated) — F1 (follow), C1 (Create delivery), SIG1
  (signature compat), P1 (pagination). Each calls `LiveGuard.TryRequires` at the top and returns
  early when the suite is disabled; the `Assert.Fail` placeholder is the Phase 13 payload seam
  (the "fill in targets" work).

## Decision: runtime-gate (return early), not xUnit skip

xUnit 2.9.3 has no public `SkipException` or `Assert.Skip` API for runtime skipping. The
`LiveGuard.TryRequires` pattern (return `false` → test returns early) is the portable alternative:
the test is reported as **passed** (not skipped) when the suite is disabled, which is acceptable
because the important guarantee is that the default `dotnet test` run stays **green** (no failure,
no contact with a live instance). The live scenarios run only when the operator has provisioned the
FQDN and enabled the suite. This is the C# analogue of `docker-smoke-test.sh`'s `exit 0` when
Docker is unavailable.

## Files changed

- `tests/Iris.Testing/InteropPlatform.cs` — new enum.
- `tests/Iris.Testing/InteropTarget.cs` — new record.
- `tests/Iris.Testing/LiveInteropOptions.cs` — new record + env-var loading.
- `tests/Iris.Testing/LiveGuard.cs` — new static gate.
- `tests/Iris.Testing/GlobalUsings.cs` — added `Iris.Core` global using.
- `tests/Iris.LiveInterop.Tests/` — new project (csproj, GlobalUsings, 3 test files).
- `Iris.slnx` — registered `Iris.LiveInterop.Tests`.

## Test count

974 → 992 (+18), 0 failures.
