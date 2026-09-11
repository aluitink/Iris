# 83.3 — Outbound delivery retry + dead-letter observability

**Commit:** `78c941d`
**Status:** COMPLETE (parts 1 + 2 + 3).

## What was built

This slice closes out the outbound-delivery hardening: it **verifies (with coded tests)** the
non-permissive-peer dead-letter contract and **exposes the dead-letter queue** on the operator
observability surface.

### 1. Permanent-4xx dead-letter contract — verified (parts 1 + 3)

The `DeliveryWorker` already treats a non-429 4xx response (401/403/404) as **permanent** — a delivery
the peer will reject on every retry (a bad signature, a blocked account, a missing inbox) is parked in
the dead-letter store **on the first attempt**, not retried up to the budget. This slice locks that
contract with tests that drive a real `DeliveryWorker` against a failable transport:

- **401 (signature-rejecting peer)** → exactly **1** send, dead-lettered with `Attempts == 1`,
  `FailureKind == NonSuccessStatus`, `FailureDetail == "401"`. Not retried.
- **403 (forbidden peer)** → same: 1 send, `Attempts == 1`, detail `"403"`.
- **Contrast — 500 (transient)** → the **full** retry budget (5 sends with `MaxAttempts == 5`), dead-lettered
  only after exhausting it, `Attempts == 5`, detail `"500"`.

This is the "a delivery to a signature-rejecting peer is dead-lettered after the retry budget (not
retried indefinitely)" integration guarantee: a permanent peer rejection consumes **one** attempt, while a
transient one consumes the whole budget. The existing `DeliveryRetryTests` covered 500 + transport-error
dead-lettering; this slice adds the **4xx-permanent** dimension it was missing.

### 2. Dead-letter observability endpoint (part 2)

The dead-letter queue (deliveries that exhausted their retry budget) was previously only inspectable
in-memory. It is now exposed on the operator surface, alongside the health endpoint:

**`GET /ap/v1/dead-letters`** — returns the deliveries that could not be delivered, so an operator can
inspect **what** is failing to federate (which inbox, which activity, why, after how many attempts):

```json
{
  "count": 3,
  "limit": 25,
  "deadLetters": [
    {
      "inbox": "https://peer3.local/inbox",
      "activityId": "https://a.domain.local/.../act-3",
      "actor": null,
      "failureKind": "nonsuccessstatus",
      "failureDetail": "430",
      "attempts": 3,
      "deadLetteredAt": "2026-09-11T00:00:00Z"
    }
  ]
}
```

- `count` — the store's current size (`IDeliveryDeadLetterStore.Count`).
- `deadLetters` — a **bounded peek**, newest-first, of the most recent entries.
- `limit` query parameter — bounds the peek. Default `25`
  (`DefaultDeadLetterPeekLimit`), clamped to `[1, 500]` (`MaxDeadLetterPeekLimit`) so a monitoring scrape
  cannot load the store's entire (already-bounded) backlog into the response.
- **Read-only:** it does not re-drive deliveries (re-driving is an explicit operator action via
  `DeadLetterEntry.ToJob()` → enqueue). **No authentication** (an operator's monitoring scrape reaches it
  without an ActivityPub signature), like the health/ready endpoints.

**Key types:**
- `ActivityPubServerConstants.DeadLetterRouteSegment` (`"dead-letters"`) — the route segment constant.
- `DeadLetterHandler` + `ResolveDeadLetterPeekLimit` (private, in `ActivityPubServerExtensions.cs`) — the
  minimal-API handler that serializes `IDeliveryDeadLetterStore.ListAsync` into the payload above. It
  resolves the `limit` query parameter from the request's `HttpContext` (injected, not via
  `HttpContext.Current`, to stay idiomatic for minimal APIs).
- `DefaultDeadLetterPeekLimit` (25) / `MaxDeadLetterPeekLimit` (500) — the public peek-bound constants.
- Mapped in `MapActivityPubEndpoints` as `GET {prefix}/dead-letters`, with a `.WithName("dead-letters-endpoint")`.

**Design decision (recorded):** a dedicated endpoint (not a `result.Data` extension on the existing
`/ap/v1/health` handler). The health endpoint's `HealthHandler` serializes only each check's `status` +
`description`, **not** `result.Data` — so surfacing the dead-letter entries (a list of inbox/activity/
failure fields) there would have required changing the health endpoint's response shape for *all* checks,
a blast radius that does not fit. A purpose-built `/ap/v1/dead-letters` is additive, read-only, and keeps
the health endpoint's contract stable (the `dead_letters` **count** still lives on `/ap/v1/health` via the
`InstanceObservabilityHealthCheck` added in 83.2).

## Carried-forward remaining (from 83.2 part 3 — graceful degradation)

Still open on the Active Slice (unchanged this turn): when the durable store is unavailable at startup,
the server should start in a degraded/read-only mode (not crash) + surface it. The existing
`PersistenceHealthCheck` already reports Unhealthy (→ `/ap/v1/health` 503), so an orchestrator can evict a
degraded instance; remaining is a startup probe + a gate the write paths consult. See the
[832 change doc](832-phase83-deployment-hardening-config-validation.md).

## Tests

`tests/Iris.Server.Tests/Delivery/DeadLetterObservabilityTests.cs` — **6 new tests**:
- **Endpoint (count + bounded peek):** 3-entry store → `count == 3`, peek newest-first (peer3, peer2,
  peer1), each entry carries `failureKind`/`failureDetail`/`attempts`.
- **Endpoint (limit bounds the peek):** `?limit=2` → `count` still 3 (the store holds 3), peek bounded to 2,
  `limit` field reports 2.
- **Endpoint (count == peek length when limit >= count):** `?limit=100` → peek length equals count.
- **Worker (401 permanent):** signature-rejecting peer → 1 send, dead-lettered, `Attempts == 1`,
  `NonSuccessStatus`, detail `"401"`, queue drained.
- **Worker (403 permanent):** forbidden peer → 1 send, `Attempts == 1`, detail `"403"`.
- **Worker (500 transient contrast):** 500 → full 5-attempt budget, dead-lettered with `Attempts == 5`.

## Suite impact

- `dotnet build -c Release` — 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test -c Release --no-build --filter "Category!=Slow"` — **1614 passed, 0 failed, 1 skipped**
  (Iris.Server.Tests 988 → 994). The new 6 tests pass in isolation + on a clean full-suite run; the
  occasional `Iris.Server.Tests` failure under heavy parallel full-suite load is the known timing/contention
  flake (passes 994/994 in isolation).
