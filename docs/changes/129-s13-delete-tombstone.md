# 129 — Sample Explorer: delete (`Delete`) closes the §3.3 "delete / tombstone" surface decision

**Status:** DONE — the client gained `DeleteAsync`, the server routes an outbox-published `Delete` to the
existing (previously inbox-only) `DeleteActivityHandler` so a local author can delete their own content,
the handler now also removes the deleted object's `Create` from the author's outbox, and the Object page
gained an author-only Delete button. This closes the §3.3 "No delete / tombstone client method" item — the
last deferred library-surface decision after unlike (change [128](128-s12-unlike-undo-like.md)).

## Background — §3.3 "delete"

The sample plan's §3.3 noted: *"No delete / tombstone client method — `BuildTombstone` exists in
`IriExtensions` but there is no `DeleteAsync` on the client. Also a library surface decision, out of
scope."*

Investigation revealed the server was **further along than the plan assumed**: a full
`DeleteActivityHandler` already existed (registered as an `IActivityHandler` for the `Delete` activity) —
it tombstones the object, removes the reply edge, and propagates the tombstone to the author's remote
followers (the federated half of F-03). The only gaps were:

1. **No client method** — `DeleteAsync` did not exist.
2. **Outbox-published `Delete` was a no-op** — the outbox handler (`OutboxPublishHandler`) recorded the
   `Delete` in the outbox + activity store but its switch fell through to `_ => null` (no
   tombstone/cleanup/propagation). Only an *inbound* `Delete` (delivered to an actor's inbox) reached the
   `DeleteActivityHandler`.
3. **The deleted note's `Create` stayed in the outbox** — the outbox collection kept listing the deleted
   content.

## Change

- **`src/Iris.Client/IActivityPubClient.cs` + `ActivityPubClient.cs`** — `DeleteAsync(actorId, objectId)`:
  builds a `Delete` (type `Delete`, id `{actorId}/deletes/{suffix}`, `object` = the object being deleted
  by IRI as a bare link) and publishes it to the actor's own outbox (the author deletes their own content;
  a content object has no inbox of its own). The `Delete` gets a deterministic unique-per-(actor,object)
  IRI so a retried delete dedupes.
- **`src/Iris.Server/ActivityPubServerExtensions.cs`** — `OutboxPublishHandler` gained a `Delete` branch:
  a local actor's outbox-published `Delete` is routed to the **existing** `DeleteActivityHandler` (the
  same handler that handles an inbound `Delete`), so the tombstone, reply-edge cleanup, and federated
  propagation all go through the one code path. The handler resolves the stored object and applies the
  owner guard (a non-author is a no-op), so the 202 (recorded) is still correct.
- **`src/Iris.Server/Inbox/DeleteActivityHandler.cs`** — after tombstoning + reply-edge cleanup, the
  handler now removes the deleted object's `Create` from the author's outbox (the deterministic
  `{actor}/creates/{suffix}` sibling of the object IRI), so the outbox collection no longer lists the
  deleted content.
- **`src/Iris.Server/Stores/IActivityStore.cs` + `InMemoryActivityStore.cs` + `FileBackedActivityStore.cs`**
  — `RemoveFromOutboxAsync(actorIri, itemIri)`: removes an outbox item by its IRI (the inverse of
  `AddToOutboxAsync`), used by the delete to drop the deleted note's `Create`.
- **`samples/SampleBlazorClient/Pages/ObjectPage.razor`** — an author-only **Delete** button (shown when
  the logged-on actor is the object's `attributedTo`): calls `DeleteAsync`, then re-loads the object,
  which now surfaces the server's `Tombstone` ("deleted" marker) in place of the original content. A
  `danger` style was added.

## Tests

- **`tests/Iris.Client.Tests/DeleteDeliveryTests.cs`** (new, 2 tests): `DeleteAsync` POSTs a `Delete`
  (type `Delete`, id `{actor}/deletes/{suffix}`, `object` = the object IRI) to the actor's own outbox; and
  its `object` reference matches the exact note IRI `PostNoteAsync` created.
- **`tests/SampleBlazorClient.Tests/S7ScreenTests.cs`** (new, 1 in-process e2e test): post a note → it is
  stored and in the author's outbox; delete (202) → the object's IRI now resolves to a `Tombstone` and the
  note's `Create` is gone from the outbox (cache-bypassed re-read).
- **3 `Iris.Server.Tests` stubs** (`IrisActorDocumentFetcherTests`, `FeedServiceTests`,
  `IrisRemoteCollectionFetcherTests`) updated to implement the new `IActivityPubClient.DeleteAsync`.

## Verification

- `dotnet build` 0 warnings; **883/883** green (Iris.Client.Tests 114 → 116; SampleBlazorClient.Tests
  82 → 83).
- The in-process e2e test confirms the full cycle: post → stored + in outbox; delete (202) → the object
  resolves to a `Tombstone` and the `Create` is removed from the outbox — exercising the outbox-handler
  routing to the `DeleteActivityHandler` (the local path). The federated propagation (remote followers) is
  the handler's existing, separately-tested behavior (F-03).

## Notes

- **Reused the existing `DeleteActivityHandler` rather than adding an `Undo(Create)` path.** The handler
  already tombstones, cleans up the reply edge, and propagates to remote followers (F-03). An `Undo` of a
  `Create` has no ActivityStreams precedent (unfollow/`Undo(Follow)` is the inverse pattern), and a
  dedicated `Delete` branch in the inbox handler would have duplicated the propagation logic. Emitting a
  `Delete` and routing it to the existing handler is the minimal, consistent change.
- **§3.3 is now fully closed.** Both deferred library-surface decisions — unlike (change 128) and delete
  (this change) — are implemented. The sample plan's §3.1 (client methods) and §3.2 (IriExtensions) audits
  are also closed. The remaining roadmap work is Phase 17.2 (structured logging + OTel metrics) and
  Phase 18 (perf / load baseline).
