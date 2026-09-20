# 14819 — Home feed surfaces inbox-delivered remote content (S25)

**Status:** done (pending QA live re-verify)
**Slice:** Dev Queue — S25: a remote post is not in the follower's home feed (S2-sev interop)
**Owner:** dev

## Problem

After a cross-instance follow (actor on instance B follows actor on instance A) and A's public post
federates to B's inbox, B's **home feed** for the follower (`GET B /u/{follower}/feed`) did **not**
include A's post — the follower's timeline showed only the `Follow` activity (which arrived via the
inbox), not the delivered remote `Note`. B had accepted the delivery: B's inbox processed the
`Create` (`CreateActivityHandler … ok`) and B's proxy served the note by deep link (200) — but the
home feed left the delivered post out.

## Root cause

The home feed (`FeedService`, `src/Iris.Server/Services/FeedService.cs`) is composed per followed
actor:

- a **local** follow → read its outbox from the local activity store;
- a **remote** follow → **walk the remote actor's outbox over the wire** (`FetchRemoteOutboxAsync`).

When a remote `Create` is delivered to a local recipient, the `CreateActivityHandler` stores the
embedded object in the **object store** (`IObjectStore.PutObjectAsync`, keyed by the object's
`attributedTo`) and adds the `Create` to the **recipient's** outbox — but that object store is the
*object-document* read path (served by IRI / proxy), **not** the feed's source. The feed for a
remote follow only reads the **wire-walked remote outbox**. So when that wire walk yields nothing —
an unreachable or broken remote outbox, or a fresh delivery not yet reflected in the walked page —
the remote follow contributes nothing and the follower's home feed shows no remote post at all
(exactly the S25 symptom: `totalItems` = the `Follow` activities only).

## Fix

`src/Iris.Server/Services/FeedService.cs` → `BuildFeedUncachedAsync`: for each **remote** follow the
feed now **unions** the wire-walked remote outbox with the content this instance has **already
received in its inbox** from that author (the object store, `IObjectStore.ListByActorAsync`).

- New `GetDeliveredContentAsync(actorIri, ct)`: lists the object-store objects attributed to the
  remote author (the inbox-delivered notes), skips `Tombstone`s (a deleted object has no feedable
  content), and wraps each surviving object in a **synthetic `Create`** (activity IRI = the object
  IRI, `Actor` = the author, the object **embedded**). Wrapping in a `Create` lets the item flow
  through the feed's existing content-object de-dup/coalesce, reply filter, and
  audience/visibility filter exactly like a wire-walked outbox `Create`.
- The union is de-duplicated by `TruncateDedup` (by item IRI, then by content object), so a note
  present in **both** the wire walk and the delivered object store renders **once** (the embedded
  representative wins).
- A store failure contributes nothing (a single broken follow must not fail the whole feed —
  147.2); the existing `try/catch` around each follow is unchanged.

This makes the delivered remote post surface in the follower's home feed even when the live outbox
walk contributes nothing — the content is read from the **same store/path the inbox write lands in**
(the object store), not from a second network hop. Local follows are unchanged (they read the local
activity store, which already contains delivered + own content).

## Tests

`tests/Iris.Server.Tests/Services/FeedServiceTests.cs` (in-memory `FeedService` unit harness):

- `Feed_RemoteFollow_DeliveredContentInObjectStore_SurfacesInFeed` — a remote follow whose outbox
  walk yields **nothing** (no collection document mapped) but whose author has a note in the object
  store (the production inbox path) → the note **surfaces** in the follower's home feed (was
  absent).
- `Feed_RemoteFollow_DeliveredAndWireContent_Deduplicated` — the same note both in the wire-walked
  outbox and in the object store → rendered **exactly once** (content-object coalesce).
- `Feed_RemoteFollow_DeliveredTombstone_NotInFeed` — a tombstoned object in the object store does
  **not** surface in the feed.

`dotnet test tests/Iris.Server.Tests` (fast) → **1408 pass, 0 fail** (includes the three new
tests). `dotnet test tests/Iris.Web.Tests` → **108 pass, 0 fail** (no regression). Full solution
`dotnet build` → **0 warning, 0 error**.

## Live verification

**Deferred to QA** (per the loop's policy that dev does not run the two-instance QA federation
stack for verification). The dev instance (`iris.luit.ink`) was rebuilt (`--no-cache`) + redeployed
with this fix and is healthy; the S25 actors (`ii-a1`/`ii-b1`) live on the QA federation stack, not
dev, so a live wire repro requires the two-instance stack. QA re-verify: a fresh cross-instance
follow where A's public post federates to B → B's follower home feed (`GET /u/{follower}/feed`)
includes A's note (not just the `Follow` activity).

## Files

- `src/Iris.Server/Services/FeedService.cs` — remote-follow feed now unions the wire-walked outbox
  with the inbox-delivered object-store content (`GetDeliveredContentAsync`).
- `tests/Iris.Server.Tests/Services/FeedServiceTests.cs` — three regression tests.
