# 132.1 — Interaction tracking (like / boost / reply counts, local + remote)

**Date:** 2026-09-13
**Slice:** 132.1 (user request — "Objects have Likes, Shares, and Replies; we need to track the counts
of these for local and remote objects. We should be able to display the number of Likes/Shares/Replies
on any object we can see. Ideally when we fetch an object via proxy or upon seeing an announce or reply,
we attempt to sync up the like/share/reply counts by walking the object collections if available; if the
collections cannot be walked or return nothing, we would serve the counts of any known Likes/Shares/Replies.")
**Commits:** `feat(132.1): render iris:repliedCount on the object-document endpoint`,
`feat(132.1): sync a proxied remote object's interaction edges into the local stores`

## Problem

Two gaps made a proxied remote object's interaction counts read as **0** even when the remote had real
counts, and left the object-document endpoint missing the reply count entirely:

1. **The object-document endpoint was missing `iris:repliedCount`.** `GET /ap/v1/{object}` (the read the
   object-detail page and the `EngagementBar` fast path use) rendered `iris:likedCount` +
   `iris:sharedCount` from the local like / announce reverse indexes, but **not**
   `iris:repliedCount` (it was only rendered on collection-page items). A client reading the counts off
   the object document it already fetched could not read the reply count and fell back to a slow
   `/replies` collection walk (showing 0).

2. **The proxy stored a remote object but never its interactions.** When the proxy
   (`POST /ap/v1/proxy/{target}`) relayed a successful GET of a remote ActivityPub JSON object, it
   stored the object in the local `IObjectStore` (75.3) but never recorded the object's like / boost /
   reply edges. So a proxied remote object's object-document read showed "0 likes · 0 boosts · 0
   replies" even though the remote had real counts.

## Change

### (A) `iris:repliedCount` on the object-document endpoint

`ObjectDocumentHandler` now reads the per-object reply count live from the reply reverse index
(`persistence.Replies.GetRepliesAsync`) alongside `likedCount` / `sharedCount`, and
`ServeObjectDocument` renders `iris:repliedCount` onto the object document. The counters are computed on
every read and are cacheable (not per-requester), so the object-detail page and `EngagementBar` read all
three counts off the document they already fetched instead of re-walking the `/likes`, `/shares`, and
`/replies` collections. An object with no interactions renders the counters as `0` (not absent).

### (B) Proxy interaction-edge sync

After the proxy stores a remote (non-locally-authored) object, `SyncProxiedObjectInteractionsAsync`
walks the object's `/likes`, `/shares`, and `/replies` collections on the remote (via the same signed
`IActivityPubClient` the proxy used to fetch the object, bounded to 100 items each) and records the
discovered likers / announcers / replies as edges in the local like / announce / reply reverse indexes
(idempotent, add-if-absent). The object-document endpoint then derives the object's
`iris:likedCount` / `iris:sharedCount` / `iris:repliedCount` from those indexes, so a proxied read
reflects the object's real interaction counts.

Design points:

- **Best-effort and bounded.** Each collection walk is capped (100 items — a like/boost/reply set is
  small) and any failure (a network error, a 404, a timeout) is swallowed: the proxy relay and the
  object store are never broken by a failed interaction sync.
- **Known-edges fallback.** When a collection cannot be walked (the remote does not serve it — a
  non-Iris / strict ActivityStreams instance that does not expose the bare `likes` / `shares` extension
  collections — or is slow / unreachable), the walk yields nothing and the object is simply served with
  the counts of the interactions this instance has **already** recorded (from inbound Like / Announce /
  Reply activities). There is no additional signal to mine; the fallback is "serve what we know."
- **Locally-authored objects are skipped.** A locally-authored object's interactions are already
  recorded by this instance's own handlers (the `LikeActivityHandler` / `AnnounceActivityHandler` /
  `CreateActivityHandler`), so the proxy does not re-walk its remote collections (the object is not
  really remote — it is a local object being read back through the proxy).

## New / changed API

- `ServeObjectDocument` now takes a `repliedCountValue` parameter and renders `iris:repliedCount`.
- `ObjectDocumentHandler` computes `repliedCountValue` from `persistence.Replies`.
- `SyncProxiedObjectInteractionsAsync(client, persistence, objectIri, ct)` (`private static`) — walks the
  remote object's `/likes`, `/shares`, `/replies` and records the discovered edges locally.
- `ForEachCollectionItemAsync(client, collectionIri, limit, onItem, ct)` (`private static`) — bounded,
  best-effort collection walk (any failure yields nothing).
- `IsLocallyAuthoredObject(obj, instanceBase)` (`private static`) — whether a fetched object's
  `attributedTo` is a local actor on this instance (the sync is skipped for it).

## Verification

- `ObjectDocumentRepliedCountIntegrationTests` (2) — a note with 2 likers + 1 announcer + 2 replies
  renders `2/1/2` on its object document; a note with no interactions renders `0/0/0` (the counters are
  always present, not absent, because the endpoint computes them on every read).
- `Proxy_GetOfRemoteNote_SyncsInteractionEdgesIntoLocalStores` (1) — a Note in B's store with recorded
  like / announce / reply edges is proxy-GET'd from A, and A's local reverse indexes now hold the same
  edges (a subsequent local read would render `2/1/1`).
- `dotnet test Iris.slnx` — all tests pass (0 failures) under full-suite concurrency.

## Out of scope

- Walking a remote object's collections **on an inbound Announce / Reply** (the request mentions it, but
  the proxy path is the one that actually stores the remote object locally and serves it; an inbound
  Announce / Reply records only its own edge, which the handlers already do). A future slice could
  extend the sync to fire on an inbound announce/reply of a not-yet-stored remote object.
- Persisting the synced edges to the durable store (they are in the in-memory store in tests; the durable
  `InMemoryPersistenceProvider` is the same shape — no change needed, the edges are stored the same way
  the handlers already store them).
