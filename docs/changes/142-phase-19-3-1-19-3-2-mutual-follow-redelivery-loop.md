# 142 — Phase 19.3.1 / 19.3.2: mutual-follow re-delivery loop (fix)

> 2026-09-01 · Slice 19.3.1/19.3.2 · Phase 19 (federation, two-instance network) · commit `262616e`

## What was built

Closed the two-instance **re-delivery loop** found during the Phase 19.1.3 live verification. With both
instances hosting a local copy of the same actor (`alice-a` ↔ `alice-b`) and each following the other, a
`Create` posted on A was federated to B; B's `CreateActivityHandler` re-fan-out the *same* activity back
to A (the mutual follow edge was recorded on B), and each echo was re-fan-out again — an **unbounded
delivery storm** (observed live: 140,488 duplicate deliveries to a single inbox, a 138 MB queue).

The fix makes the inbox pipeline idempotent by activity IRI: the `InboxProcessor` now stores each inbound
activity **add-if-absent** and, when the activity was already stored (a re-delivery), does **not**
re-dispatch it to a handler. Only the first delivery of an activity is interpreted; the post is federated
to the author's followers exactly once and never re-fan-out.

## Key types & files

- `src/Iris.Server/Stores/IActivityStore.cs` — new `TryAddActivityAsync(IObject, CancellationToken)`
  (atomic add-if-absent by IRI; returns `true` iff the activity was added).
- `src/Iris.Server.InMemory/Stores/InMemoryActivityStore.cs` — `TryAddActivityAsync` via
  `ConcurrentDictionary.TryAdd` (atomic).
- `src/Iris.Server/Persistance/Stores/FileBackedActivityStore.cs` — `TryAddActivityAsync` via an atomic
  check-then-store under the file-state lock.
- `src/Iris.Server/Inbox/InboxProcessor.cs` — `ProcessAsync` gates handler dispatch on the
  first-delivery result: a re-delivery (IRI already stored) is a no-op and is not re-dispatched.
- `src/Iris.Server/Inbox/AnnounceActivityHandler.cs` — re-stores the propagated boost form
  (`to=follower`) under the shared deterministic IRI via `PutActivityAsync` (the pre-existing overwrite
  path), because the add-if-absent guard would otherwise leave the original `cc=Public` form in place.
- `src/Iris.Server/Inbox/CreateActivityHandler.cs` — doc updated to reflect pipeline-level idempotency.
- `tests/Iris.Server.Tests/MutualFollowDeliveryLoopIntegrationTests.cs` — new two-instance mutual-follow
  integration test (2 tests).

## Tests

1191 → **1193** passing (the +2 are the new loop-safety tests). Full `dotnet test` green; `dotnet build`
clean (`TreatWarningsAsErrors`).

- `MutualFollow_Post_FederatesToPeer_BoundedNotUnbounded` — posts a signed `Create` on A in a mutual-follow
  network; asserts it reaches B's inbox a **bounded** number of times (≤ 4) and B's outbox lists it
  exactly once.
- `RedeliveredCreate_IsRecordedOnce_NotReFannedOut` — re-delivers the same `Create` to B; asserts B stores
  it once and the outbound re-fan-out stays bounded.

**Loop detection verified:** with the `InboxProcessor` guard temporarily disabled, the same tests produced
**3,950** and **6,474** deliveries (the unbounded storm, matching the live 140k+); with the guard, a
constant. This proves the tests actually exercise the loop and that the guard is what bounds it.

## Decisions

- **Loop-safety lives in the pipeline, not per handler.** The `InboxProcessor` is the single owner of
  "receive an activity" (C-07), so the add-if-absent gate belongs there and uniformly covers every
  activity type (Create, Announce, Follow, …). A per-handler guard would miss types and would not stop a
  re-dispatch by a different handler.
- **`PutActivityAsync` (overwrite) is retained for the Announce propagation form.** An `Announce` boost
  reuses the deterministic IRI `{announcer}/announces/{objectIri}` but is delivered in two forms: the
  original inbound form (`cc=Public`) and the propagated form (`to=follower`). The outbox/follower view
  must carry the propagated form, so the handler explicitly re-stores it via the overwrite path. This is
  the one place where "same IRI, different form, later form wins" is the correct semantics — distinct
  from the re-delivery case (same IRI, same activity), where "first wins" is correct.
- **Bounded, not zero, deliveries to the peer.** The intended A→B delivery is 1; a bounded echo is
  tolerated (the peer's local handling may deliver once more). The assertion is `≤ 4`, which passes with
  the fix and fails (thousands) without it — robust against minor timing variation while still catching
  an unbounded loop.
