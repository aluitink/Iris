# 041 — Live interop suite design

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The project needed a way to run real-world federation checks against external instances without polluting the default local test suite or requiring every developer to have a specific third-party host configured.

Two shapes were considered: a trait inside the server test project, or a separate project with a runtime guard. The first option would still compile and attempt these tests in the default run; the second makes the opt-in execution path explicit.

## Decision

Iris uses a separate project for live interop:

- `tests/Iris.LiveInterop.Tests`
- tests are gated by a `LiveGuard.Requires()` runtime check
- the suite only runs when the opt-in environment is set and the required target configuration is provided
- the default `dotnet test Iris.slnx` does not include the live suite as a normal execution target

This keeps the default CI/test loop deterministic and prevents accidental third-party calls from a local `dotnet test` run.

## Alternatives considered

### 1. Put live tests inside `Iris.Server.Tests` with `[Trait("live")]`

This still compiles into the default run and has no built-in pattern for "skip unless env var set." It would create a false sense of safety and force per-test guards.

### 2. Run the live suite in the default CI job

This would make a missing FQDN and misconfigured target environmental failure a normal build problem. That violates the intended separation between in-process self-tests and live interop checks.

### 3. Create one giant integration test project without a guard

This is functionally the same as putting the suite in the default run, with worse ergonomics and greater operational risk.

## Consequences

- Default builds remain fast and local-safe.
- Live interop is opt-in, explicit, and easy to run when the host target exists.
- The project design matches the pre-existing runtime-gating pattern used elsewhere in the repo.
- The live suite can be executed in a dedicated job without polluting the default gate.

## Code alignment

The current design follows the decision:

- the live suite is a separate project rather than a trait-based test folder
- the guard runs at runtime and skips when prerequisites are absent
- the default test run remains a self-contained local suite that proves the in-process harness without depending on external hosts

This is the correct structural boundary for a true live interop suite.
