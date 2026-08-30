# 076 — Phase 8 S7: Explorer write screens + Like/UndoFollow client

> 2026-08-30 · Phase 8 (Sample) · Slice S7 (core)

## What was built

The Blazor WASM explorer's **write screens** (the core of SAMPLE_PLAN slice S7) are complete: the
composer, the follow/unfollow action, and the like action. Each write the screens perform is driven
through `ExplorerSession.GetClient()` and pinned by an in-process test against a live
`SampleServer`. This is the "act" half of the boot → explore → **act** story: after logging on (S3–S5)
and reading (S6), a user can now post a note, reply under a parent, follow / un-follow an actor (locally
and across instances), and like an object.

Two new one-call client methods were added to complete the write surface: `LikeAsync` and
`UndoFollowAsync`.

## Key types & files

- **`src/Iris.Client/IActivityPubClient.cs` / `ActivityPubClient.cs`** (new methods):
  - `UndoFollowAsync(actorId, targetId, ct)` — builds an `Undo` whose `object` references the original
    `Follow` by IRI (the same deterministic `{actorId}/follows/{targetId}` IRI `FollowAsync` mints) and
    delivers it to **the follower's own inbox** (`actorId.InboxOf()`) — the ActivityPub un-follow
    convention (the party that made the follow undoes it). The `Undo` gets a deterministic,
    unique-per-(actor,target) IRI so a retried un-follow dedupes.
  - `LikeAsync(actorId, objectId, ct)` — builds a `Like` (actor = the liker, object = the liked object)
    and delivers it to **the liker's own inbox** (`actorId.InboxOf()`, the local-write path). A content
    object (the liked note) has no inbox of its own — only actors do — so the like is a local write the
    instance records in the liker's `liked` collection.
- **`samples/SampleBlazorClient/Pages/Compose.razor`** (new, `/compose`) — post a note (`PostNoteAsync`)
  or a reply under a parent IRI (`PostReplyAsync`); nav link added in `Layouts/MainLayout.razor`.
- **`samples/SampleBlazorClient/Pages/ActorDetail.razor`** — Follow / Unfollow card
  (`FollowAsync` / `UndoFollowAsync`), shown once an actor is loaded.
- **`samples/SampleBlazorClient/Pages/ObjectPage.razor`** — Like button (`LikeAsync`) on the object view.
- **`tests/SampleBlazorClient.Tests/S7ScreenTests.cs`** (new, 6 facts) — the write screens driven
  in-process. The single-instance host is built by hand with `BaseUri` = the dial base so the Basic-auth
  logon, the signed writes, and the activity body actor all agree on one IRI (the inbound key resolver
  verifies the signature against the actor document's `publicKey`). The federated test spins up two
  instances (`a.example`/`b.example`) and wires cross-instance key resolution with a deferred
  actor-document fetcher (`LazyHandler`), then does a genuine A→B follow and an A-local un-follow.
- **`tests/Iris.Server.Tests`** — the three test stub clients
  (`IrisRemoteCollectionFetcherTests.StubCollectionClient`,
  `IrisActorDocumentFetcherTests.StubActivityPubClient`, `FeedServiceTests.StubClient`) now implement the
  two new `IActivityPubClient` members (the only other implementers in the repo).

## Tests

`SampleBlazorClient.Tests` 36 → 42 (6 new S7 facts). Full solution green (878 tests) after the stub
updates; build clean (0 warnings).

## Decisions

- **Un-follow and like are local writes (own inbox).** Both `UndoFollowAsync` and `LikeAsync` deliver to
  the acting actor's own inbox, matching the `PostNoteAsync`/`PostReplyAsync` local-write convention and
  the server's `UndoActivityHandler` (which resolves the stored `Follow` and keys off the recipient as the
  follower). This keeps the client's write path uniform: a user-initiated activity is a signed POST to the
  author's own home.
- **The `Like` fix.** An earlier `LikeAsync` delivered to `objectId.InboxOf()` — but a content object has
  no inbox (only actors do), so that path 405'd against the actor-inbox route. Delivering to the liker's
  own inbox is what `LikeActivityHandler` expects (it records the like in the liker's `liked` collection
  independent of the recipient).

## Forward-looking: the delivery-model invariant (next slice, not yet resolved)

A pre-fix audit against the project's delivery invariant — **all user-initiated activities flow through
the acting actor's own outbox; only server-to-server federation delivers to an inbox** — found a tension
that is **not** resolved here (and must be decided before any fix):

- Posts/replies and the un-follow are outbox-compliant (the server records them in the actor's outbox and
  federates).
- `FollowAsync` / `BlockAsync` / `FlagAsync` deliver **directly to the target's inbox** (client →
  recipient), bypassing the follower's own outbox; `LikeAsync` records locally but does not federate to
  the object's owner.

Two consistent models exist (model 1: the client addresses the recipient's inbox for relationship edges;
model 2: everything flows through the actor's own outbox and the server owns the recipient hop). The
follow lifecycle (`Accept` back to the follower, `UndoActivityHandler` resolving the stored `Follow`) is
built on model-1 assumptions. **Decision required before the fix** — see
[SAMPLE_PLAN §4.3a](../SAMPLE_PLAN.md#43a-delivery-model-the-invariant-to-hold-across-every-write).
