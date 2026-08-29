# 055 — Federated `Update` / `Delete` propagation (F-02/F-03 federated half)

> 2026-08-29 · Slice 12.10 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes the **federated half** of gaps **F-02** (`Update`) and **F-03** (`Delete`), plus the
**F-12** reply-edge cleanup on delete. Slice 12.3 landed the *local* write path (an inbound
`Update` refreshes the stored object; an inbound `Delete` tombstones it). But a local refresh is
not enough once the object has been federated out: every remote instance that holds a copy (via
the outbound `Create` federation, Slice 11.7) must be told, or it keeps serving the pre-edit
content — or the pre-delete content in place of a `Tombstone`.

- **Propagation service.** A new `IDeletePropagationService` / `DeletePropagationService` is the
  single owner of the remote fan-out. For an `Update` the targets are the author's **remote
  followers**; for a `Delete` they are the author's remote followers **plus** the **remote
  parent's owner** when the deleted object is a reply (the parent's instance holds the replies
  collection, F-12). Local targets are skipped (their copy is refreshed / tombstoned locally by
  the handler). Each target is delivered via `IDeliveryService.DeliverToActorAsync` (signed as the
  author; per-actor inbox / `sharedInbox` resolution). The propagation activity's `Id` is derived
  from the object IRI so a re-delivered propagation is deduplicated by the receiving instance's
  inbox pipeline (C-07).
- **Relaxed owner guard (federated inbound).** `UpdateActivityHandler` / `DeleteActivityHandler`
  now accept a federated activity from a **remote** owner when this instance holds a copy of the
  object (stored via the outbound `Create` federation) and the stored object is **attributed to**
  that actor. Previously the guard required the actor to be *local*, so a remote instance that
  received an author's post silently no-op'd the author's later `Update` / `Delete`. A remote actor
  updating / deleting an object it does not own is still rejected.
- **Home-instance-only re-propagation.** Only the author's **home** instance (where the actor is
  local) re-propagates: a remote instance that already received the activity has been told by the
  home instance and does not own the author's follower set, so re-propagating there would fan the
  activity out again.
- **F-12 reply-edge cleanup + pre-tombstone capture.** The `DeleteActivityHandler` captures the
  deleted object's **parent object before tombstoning** (a `Tombstone` carries no `inReplyTo`) so
  the propagation can resolve the remote parent's owner (its `attributedTo`); it also removes the
  local parent → child reply edge for a deleted reply so the parent's replies collection no longer
  lists it.

*Scope note:* this is the **server-side** federated write path. The client already posts `Update` /
`Delete` via the signed wire (Slice 12.3); no client change was required. Profile-object
`Update` is interpreted the same way (the fan-out targets the author's followers, matching the
post path).

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/IDeletePropagationService.cs` | Propagation interface (`PropagateUpdateAsync` / `PropagateDeleteAsync`). |
| `src/Iris.Server/DeletePropagationService.cs` | Default impl: remote-follower enumeration + remote-parent-owner for deleted replies, delivery via `IDeliveryService`. |
| `src/Iris.Server/UpdateActivityHandler.cs` | Relaxed owner guard (accepts a remote owner of a stored copy); re-propagates a local author's `Update` to remote followers. |
| `src/Iris.Server/DeleteActivityHandler.cs` | Relaxed owner guard; captures the parent object pre-tombstone; removes the local reply edge; re-propagates a local author's `Delete` to remote followers + the remote parent's owner. |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | DI registration of `IDeletePropagationService → DeletePropagationService`. |
| `tests/Iris.Server.Tests/ObjectPropagationIntegrationTests.cs` | Two-instance E2E (author A ↔ remote C): create → federate, edit → remote refresh, delete → remote tombstone. |
| `tests/Iris.Server.Tests/UpdateActivityHandlerTests.cs` / `DeleteActivityHandlerTests.cs` | Unit coverage for the relaxed guard, propagation targeting (remote follower, local-only followers, remote parent's owner), and reply-edge removal. |

## Tests

689 → **708** (+19):

- `tests/Iris.Server.Tests/ObjectPropagationIntegrationTests.cs` — 3 new (two-instance E2E: `Create` → federation to C; `Update` → C's copy refreshed; `Delete` → C's copy tombstoned).
- `tests/Iris.Server.Tests/UpdateActivityHandlerTests.cs` — +8 (federated inbound from a remote owner of a stored copy; local-owner re-propagation to a remote follower; local-only followers → no delivery; not-stored / no-embedded-object no-ops).
- `tests/Iris.Server.Tests/DeleteActivityHandlerTests.cs` — +7 (federated inbound; propagation to a remote follower; local-only followers → no delivery; **deleted reply → remote parent's owner told + local reply edge removed**; local parent → local edge removal only; not-stored no-op).

## Decisions

- **The parent's *owner* is the propagation target for a deleted reply (not the parent IRI).** A
  deleted reply's parent may live on a remote instance; the *actor* that owns the parent object is
  the one whose instance holds the replies collection (F-12). The handler reads the parent object
  from the store (its `attributedTo`) **before** tombstoning the reply — a `Tombstone` carries no
  `inReplyTo`, so the parent must be captured up front. A local parent's owner is skipped (the edge
  is removed locally). Recorded inline: it is a targeting detail with no cross-cutting trade-off.

- **Only the home instance re-propagates.** Re-propagation is gated on `actorIsLocal`. A remote
  instance that received the `Update` / `Delete` has already been told by the home instance (which
  owns the author's follower set); letting it re-propagate would fan the activity out a second
  time. This keeps the fan-out to exactly one hop beyond the author's home instance.
