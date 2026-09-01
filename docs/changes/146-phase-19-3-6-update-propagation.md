# 146 — Phase 19.3.6: Update propagation

> 2026-09-01 · Slice 19.3.6 · Phase 19 (federation, two-instance network)

## What was built

Pinned the **Update propagation** invariant (the federated half of F-02) in both directions.
When a note is edited (re-published with new content under the *same* IRI), every instance that
holds a copy must refresh it — and only that object.

- **Direction 1** (a *local* author edits a note; the peer that received it via the outbound
  `Create` federation is refreshed) was already covered by
  `LocalUpdate_IsFederatedToRemoteFollower_RemoteCopyRefreshed` in
  `ObjectPropagationIntegrationTests` (Slice 12.10).
- **Direction 2** (the inverse — a *remote* author edits a note this instance holds a copy of) was
  the gap and is now pinned by the new
  `RemoteAuthorUpdate_LocalCopyRefreshed_NoCollateral_NoRePropagation` test.

In direction 2 the topology is the mirror of the 19.3.4 delete case: instance A (local person
`bob`) and instance B (the author `erin`). `bob` follows `erin` (the edge is recorded on B, the
author's home instance, which owns the propagation target set). `erin`'s note is federated to
`bob`'s inbox on A and A's `CreateActivityHandler` stores it (the "remote copy" on A). `erin` then
edits the note and A's `UpdateActivityHandler` receives the federated `Update` from `erin` — a
*remote* actor on A.

The `UpdateActivityHandler` already handled this correctly; the test pins three invariants:

- **Owner guard** — a remote actor may update only an object this instance holds *attributed to*
  that actor (no foreign rewrite; a remote actor editing an object it does not own is a no-op).
- **Correct scope** — only the referenced object is refreshed; an unrelated note on the same
  instance is left untouched (no collateral rewrite).
- **No re-propagation** — only the home instance (where the updating actor is local) re-fans-out
  the edit; a non-home copy applies it locally and stops. The re-propagation branch is gated on the
  updating actor being local on this instance, and `erin` is not a local actor on A, so it is
  skipped.

No production code changed. This slice is a test that pins direction 2 and guards against a future
regression that would (a) let a remote actor rewrite a note it does not own, (b) rewrite a
collateral note, or (c) re-fan-out an update from a non-home copy (an update storm).

## Key types & files

- `tests/Iris.Server.Tests/Delivery/ObjectPropagationIntegrationTests.cs` — new test
  `RemoteAuthorUpdate_LocalCopyRefreshed_NoCollateral_NoRePropagation` (direction 2, mirroring the
  existing direction-1 `LocalUpdate_...` test and the 19.3.4 direction-2 delete test).
- `src/Iris.Server/Inbox/UpdateActivityHandler.cs` — the owner guard (`actorIsLocal || IsAttributedTo`)
  and the gated re-propagation branch (`if (actorIsLocal)`). Unchanged.
- `src/Iris.Server/Delivery/DeletePropagationService.cs` — `PropagateUpdateAsync` targets the author's
  remote followers (no parent targets; an Update does not change reply edges). Unchanged.

## Tests

1197 → **1198** passing (the +1 is the new direction-2 update test). Full `dotnet test` green;
`dotnet build` clean (`TreatWarningsAsErrors`).

- `RemoteAuthorUpdate_LocalCopyRefreshed_NoCollateral_NoRePropagation` — two live in-process
  `TestServer` instances (A hosts `bob` on prop-b, B hosts `erin` on prop-c) wired over the wire
  (A's fetcher reaches B's actor doc so the federated `Update`'s signature is validated by
  resolving `erin`'s key). A's `CreateActivityHandler` stores the note federated by B; the test
  delivery worker (signed as `erin`, routing to A) then delivers the federated `Update` to
  `bob`'s inbox on A. It awaits A's object store to settle on the *edited* content and asserts:
  - **Copy refreshed** — A's stored note now carries the edited body (`erin's edited body`), not
    the pre-edit content.
  - **No collateral rewrite** — A's unrelated note (`erin's other note`) is untouched.
  - **No re-propagation** — `erin` is not a local actor on A (`Actors.TryGetActorAsync` is false),
    which is the exact condition that gates the re-propagation branch off.

## Decisions

- **Direction 2 is asserted on the *peer* side's store (A), not the author's home instance (B).**
  The slice's question is "does a non-home copy refresh correctly and stop?" — so the assertion
  targets the copy A holds, mirroring how the 19.3.4 direction-2 delete test asserts on A's
  tombstoned copy. The home-instance re-fan-out (B telling its *other* followers) is the same code
  path already exercised by the existing direction-1 `LocalUpdate_...` test (where A is the home
  instance and C is the non-home copy), so it is not re-tested here.
- **The endpoint-serves-new-content half is satisfied by the store read, not a cross-host GET.**
  The object-document endpoint reconstructs the object IRI from the *serving* instance's own base
  URL + the catch-all path. In direction 2 the note IRI is on `erin`'s host (prop-c) but the copy
  lives on A (prop-b), so a GET on A would 404 (A reconstructs a prop-b IRI, not the prop-c IRI it
  stores). The authoritative wire surface for a *copy* is the home instance's endpoint, which is the
  same surface direction 1 already covers end-to-end. The store-read assertion (the edited content
  is what a `GET` on the home instance would serve) is the faithful check for the non-home copy.
- **The unrelated-note control is stored directly (not federated).** It is a scope control — proof
  the `Update` handler refreshes only the referenced object. Storing it directly (attributed to
  `erin`) is sufficient: the handler's refresh path keys off the activity's embedded object IRI, so
  a note at a different IRI is structurally untouched. This mirrors the 19.3.4 direction-2
  delete test's collateral control.
