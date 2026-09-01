# 144 — Phase 19.3.4: Delete propagation, both directions

> 2026-09-01 · Slice 19.3.4 · Phase 19 (federation, two-instance network)

## What was built

Closed the **direction-2 gap** in Delete propagation. Direction 1 (delete a *local* note → the peer
tombstones its copy) was already covered by
`ObjectPropagationIntegrationTests.LocalDelete_IsFederatedToRemoteFollower_RemoteCopyTombstoned`.
Direction 2 — *a remote actor deletes a note, and an instance holding a copy tombstones it* — had no
two-instance test. This slice adds it.

The receiving instance's `DeleteActivityHandler` is the whole of direction 2, and it was already correct:

- **Owner guard** (`DeleteActivityHandler.HandleAsync`) accepts a *remote* author when the instance holds
  a copy of that author's object (the stored object's `attributedTo` resolves to the deleting actor). A
  remote actor deleting an object it does not own is a no-op — no foreign tombstoning.
- **Correct scope.** Only the referenced object IRI is tombstoned (replaced by an AS2.0 `Tombstone`
  under the same IRI, F-10); the instance's other stored objects are untouched.
- **No re-propagation.** The F-03 federated half (`IDeletePropagationService.PropagateDeleteAsync`) runs
  only when the deleting actor is *local* on this instance — the home instance re-fans-out the tombstone.
  A non-home copy (the actor is remote here) applies the delete locally and stops, so the tombstone is not
  fanned out again.

No production code changed. The slice is a test that proves the existing handler does the right thing in
the cross-instance, remote-author case, guarding against a future regression that would (a) let a remote
actor tombstone content it does not own, (b) collateral-delete unrelated objects, or (c) re-propagate a
remote delete (an echo storm).

## Key types & files

- `tests/Iris.Server.Tests/Delivery/ObjectPropagationIntegrationTests.cs` — new test
  `RemoteAuthorDelete_LocalCopyTombstoned_NoCollateral_NoRePropagation` (+ the `_cSeedKey` field for
  signing as erin).
- `src/Iris.Server/Inbox/DeleteActivityHandler.cs` — the handler under test (owner guard, `formerType`
  tombstone, reply-edge cleanup, local-only re-propagation). Unchanged.

## Tests

1195 → **1196** passing (the +1 is the new direction-2 test). Full `dotnet test` green; `dotnet build`
clean (`TreatWarningsAsErrors`).

- `RemoteAuthorDelete_LocalCopyTombstoned_NoCollateral_NoRePropagation` — erin (local on B) posts a note;
  it is federated to bob's inbox on A (signed as erin), and A's `CreateActivityHandler` stores the copy
  (the "remote copy" on A). erin then deletes the note; the federated `Delete` reaches bob's inbox on A.
  A's `DeleteActivityHandler` receives a Delete from a *remote* actor (erin, not local on A) for an object
  A holds attributed to erin → the owner guard passes → A tombstones its copy. Asserts: A's copy is a
  `Tombstone` (correct `id`); A's unrelated note is **untouched** (no collateral deletion); and erin is
  **not** a local actor on A (so the re-propagation branch is skipped — only B, the home instance,
  re-fans-out).

## Decisions

- **Direction 2 is the receiving instance's handler, not a propagation service.** The home instance (B)
  re-fans-out via `IDeletePropagationService` (already tested in direction 1); the non-home copy (A) only
  applies the delete locally. The test therefore drives the federated `Delete` straight to A's inbox with
  a test delivery worker signed as erin, and asserts A's handler behavior — no new production path.
- **The "remote copy" is seeded by a real federated `Create`, not a direct store.** A's `CreateActivityHandler`
  stores the embedded object only when the *recipient* (bob) is local on A; driving the Create through A's
  full inbound pipeline (signature validation + handler) is the faithful model of how A came to hold the
  copy in the first place, and it exercises A's key resolution of erin (fetching B's actor doc).
- **No re-propagation is asserted by the local-actor guard, not by a delivery counter.** A's re-propagation
  branch is gated on `IsLocalActorAsync(erin)`; since erin is not in A's local actor store, the branch never
  runs. Asserting `!A.Actors.TryGetActorAsync(erin)` is the direct, stable witness of that guard (A's own
  host delivery worker is not live in the two-instance transport, so a queue-depth count would be a weaker
  and harness-dependent signal).
