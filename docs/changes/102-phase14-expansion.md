# 102 — Expand Phase 14+ into concrete phases (14–17)

> 2026-08-30 · ROADMAP expansion · docs-only

## What was built

Expanded the abstract "Phase 14+ — Future work" section in ROADMAP.md into four concrete phases
(14–17), grounded in the Phase 9 reference docs (the gap register, compatibility matrix, and
deployment prep) and the existing code seams. This makes the post-Phase-13 work actionable: each
phase has a clear scope and the concrete slices it contains.

## The concrete phases

- **Phase 14 — Live-Interop Execution & Gap Remediation**: run the live-interop suite (13.5–13.10),
  record findings, fix the confirmed gaps, re-run until all matrix scenarios are PASS or Accepted.
  The `Iris.LiveInterop.Tests` scenario stubs (from change 101) are the payload.
- **Phase 15 — Authentication Hardening**: replace Basic auth with OAuth2/Bearer tokens (the
  `IActorCredentialValidator` seam is the swap point), implement the authorization code + PKCE flow
  for the Blazor WASM client, add token refresh/revocation.
- **Phase 16 — Production Persistence & Scaling**: database-backed persistence (PostgreSQL/SQLite,
  the `IPersistenceProvider` seam), distributed cache (Redis, the `ICache<T>` seam), shared-inbox /
  relay for larger-scale delivery, multi-instance scaling.
- **Phase 17 — Observability & Transport Hardening**: structured logging + OpenTelemetry metrics,
  circuit breaker + retry hardening, inbound rate limiting, health-check endpoints + graceful
  shutdown.

## Decision: concrete phases, not a single abstract "14+"

The original "Phase 14+ — Future work" was four abstract bullets. Expanding into four concrete
phases (14–17) makes the post-Phase-13 work actionable and gives the autonomous loop clear work
items to select from. Each phase is scoped to a single concern (live-interop execution, auth,
persistence, observability) and has concrete slices that can be selected and completed independently.
The phases are ordered by dependency: Phase 14 (live-interop) comes first because it produces the
gap findings that inform the other phases; Phase 15 (auth) is next because it's a prerequisite for
production deployment; Phases 16–17 (persistence, observability) are the final production-readiness
phases.

## Files changed

- `docs/ROADMAP.md` — expanded "Phase 14+ — Future work" into "Phase 14–17" with concrete slices;
  updated the Status-at-a-glance table row from "📋 abstract" to "📋 planned (concrete)".

## Test count

No code change (docs-only). 992 tests, 0 failures, 0 warnings (unchanged).
