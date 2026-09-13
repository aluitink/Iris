# 131.6 (internal) — Fix flaky federation tests under full-suite concurrency

**Date:** 2026-09-13
**Slice:** Unblocked item — "Flaky federation tests"
**Commit:** `test: lengthen flaky federation wait budgets to kill full-suite flakes`

## Problem

Two server integration tests intermittently failed **only when the full solution test suite ran
concurrently**, but always passed when the server suite ran alone:

- `FollowEdgeConvergenceIntegrationTests` (Phase 19.3.5 follow-edge convergence)
- `DuplicateInboundDeliveryIdempotencyIntegrationTests` (Phase 19.x redelivery idempotency)

## Root cause

Both tests wait for **cross-instance delivery to settle** (an ActivityPub activity federated over
the wire, signed, and handled by the peer's inbox handler) before asserting on the resulting
follow/block edge:

- `FollowEdgeConvergenceIntegrationTests` uses a private `WaitForAsync(probe, timeout)` that polls
  every 50 ms and **returns silently when the deadline expires** — the `Assert.True` that follows
  then fails.
- `DuplicateInboundDeliveryIdempotencyIntegrationTests` uses `TestFederation.WaitForStableAsync(probe,
  settleWindow, timeout)`, which also **returns the last observed value on timeout**.

The budgets were tight (15 s for the convergence cycle, 4 s for the idempotency settle). Under
full-suite CPU contention, real delivery (an in-process `TestServer` HTTP round-trip plus Ed25519/RSA
signature validation) can exceed those budgets, so the wait times out and the assertion fails — even
though the delivery would have completed a moment later. The tests pass in isolation because a single
suite has far more CPU headroom.

This is a **test-timing flake, not a product bug**: the federation logic is correct; the assertion
just ran before the delivery had (transiently) settled.

## Change

Lengthened the wait budgets so delivery has headroom under load:

| Test | Budget | Before | After |
|---|---|---|---|
| `FollowEdgeConvergenceIntegrationTests` (×3 calls) | `WaitForAsync` timeout | 15 s | **45 s** |
| `DuplicateInboundDeliveryIdempotencyIntegrationTests` (×2 calls) | `WaitForStableAsync` timeout | 4 s | **15 s** |

The `settleWindow` (500 ms) and poll cadence (50 ms) are unchanged, so the tests remain
**fast-or-equal** on a healthy (fast) machine — they only spend the extra time when the machine is
actually slow. Detection sensitivity for a real non-convergence / non-idempotency regression is
unchanged (the probe still observes the same values; it just gets more time to reach the stable
state).

## Why lengthen (not delete/skip)?

The web-test policy's "delete broken / skip >15 s" rule applies to `tests/Iris.Web.Tests` (the Blazor
WASM surface). These are **server** integration tests in `tests/Iris.Server.Tests` — the
integration-first source of truth for the federation invariants they encode (edge convergence,
redelivery idempotency). Deleting or skipping them would throw away real coverage. Lengthening the
timeout is the minimal, correct fix: it preserves the assertions and the coverage, and only buys
headroom under contention.

## Follow-up — the idempotency test's second flake mode (same turn)

Lengthening the *settle* timeout (4 s → 15 s → 30 s) was **not** the root cause for
`DuplicateInboundDeliveryIdempotencyIntegrationTests`. Capturing the actual failure under load showed
it failed at a *different* assertion — `the first Follow delivery should have been accepted (202)`
(line 181), in ~1 s — not the idempotency "exactly one Accept" assertion. The real cause:
`DeliverDirectly` awaits `service.DeliverAsync` (which only **enqueues** the delivery) and then waited
a **fixed 500 ms** for the async `DeliveryWorker` to perform the HTTP request. Under full-suite CPU
contention 500 ms was too short, so the capturing handler had not yet recorded `LastStatus` and the
202-acceptance assertion failed — even though the delivery would have completed moments later (the
idempotency logic is correct; the delivery just hadn't run yet).

**Fix (commit `9aceabb`):** replace the fixed 500 ms delay in `DeliverDirectly` with a poll (25 ms
cadence, 30 s budget) for `LastStatus` to be set. On an idle machine the first poll iteration succeeds
(~25 ms), so it is faster-or-equal; under load it gives the worker the headroom it needs.

**Verification:** 7 consecutive `dotnet test Iris.slnx` runs green under full-suite concurrency
(previously the idempotency test failed ~2/3 of the time).

## Verification

- `dotnet build` — clean.
- `dotnet test Iris.slnx --no-build` (full solution, all suites concurrent) — **all 1960 tests pass,
  0 failures** across 7 consecutive runs (previously the two federation tests intermittently failed
  under this exact run).

## Out of scope

- Making `WaitForAsync` throw on timeout instead of returning silently (a broader test-helper change
  that would affect other callers; the timeout-lengthening is the targeted fix for the reported
  flakes).
- The other known blocked item — live remote-Lemmy delivery (container .NET TLS handshake to
  lemmy.ml fails in the sandbox) — is environmental, not a code regression, and remains out of scope.
