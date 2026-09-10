# 64.2 — per-item likes/shares fan-out (request spam)

Phase 64 continues cutting the *number* of calls the WASM client makes. 64.2
is the next slice: eliminate the **per-item `/likes` + `/shares` count-walk**
that fired on every content card in a feed — the dominant remaining request
spam after 64.1's actor coalescing.

## Problem (620 tracker topics #4 + #5)

On `/home` initial load (andrew's followed feed), the EngagementBar fired a
**full collection walk for every card**: one `/likes` and one `/shares` per
content item. With 10 content cards that was **10 + 10 = 20** AP calls just to
count likes/shares and derive the viewer's own like/boost state.

The EngagementBar already has a **fast path** (54.8): when the content object
carries the server-rendered `iris:likedCount` / `iris:sharedCount` /
`iris:isLiked` / `iris:isShared` extensions, it seeds the counts and the
viewer's state from them and skips the count-walk (walking only to recover the
minted activity id an unlike/un-boost Undo references, and only if the viewer
engaged). The server **does** write those counters onto every feed item
(`EnrichCollectionItemsAsync`, batched per feed). So the fast path *should*
fire.

## Root cause — namespace mismatch (WASM read ≠ server write)

The fast path reads the counters via
`IActorSessionAccessor.IrisNamespaceBase`, which built the `iris:` namespace
as `{base}/ns#` from **`_advertiseBase ?? _browserBase`**.

In the **multi-instance** dev topology (the browser dials `http://localhost:PORT`
while the instance advertises `https://iris.luit.ink`), `Program.cs` rewrites
`advertiseBase` to the **browser origin** for dialing (so the WASM's same-origin
rewrite + signing match the host the server sees on the wire). But the **server**
writes the counter extensions under the namespace of its own `BaseUri` — the
**advertised FQDN** — `https://iris.luit.ink/ns#` (confirmed in the feed JSON:
each embedded Note carried `https://iris.luit.ink/ns#likedCount` etc.).

So:

- WASM read the counters under: `http://localhost:PORT/ns#likedCount`
- Server wrote them under:       `https://iris.luit.ink/ns#likedCount`

`GetLikedCount` / `GetSharedCount` found **nothing** → the fast-path guard
failed → the **full** `/likes` + `/shares` count-walk fired for every card.
This is exactly why the spam was proportional to feed size (N+1): the
server-rendered counters were being ignored, so every card re-walked both
collections.

## Fix

`apps/Iris.Web.Client/Accounts/IActorSessionAccessor.cs` — `IrisNamespaceBase`
now derives the namespace from **`_rewriteBase ?? _browserBase`** instead of
`_advertiseBase ?? _browserBase`.

`_rewriteBase` holds the **canonical** FQDN (the pre-rewrite original the
server advertises from), because `Program.cs` passes
`canonicalAdvertiseBase` as the `rewriteBase` constructor argument. That is the
same base the server uses to namespace its `iris:` extensions, so the WASM's
read now matches the server's write. In the single-instance case (no rewrite)
`_rewriteBase == _advertiseBase`, so behavior is unchanged; in the
multi-instance case it is the canonical FQDN, which is what the counters are
actually namespaced to.

This is a **one-line** behavioral change (plus doc-comment) in a single
property. It is the *only* consumer-side read of the `iris:` namespace, and both
call sites (the EngagementBar's fast-path guard and ObjectDetail's) need the
canonical namespace, so the fix is complete for the read side.

## Verification (live, fresh origin `:8093`, `/home` as andrew)

The fix was verified by re-measuring the `/home` request profile on a **fresh
port** (new origin → no cached WASM in the persistent browser profile) after
republishing the client:

| Pattern (per `/home` load) | Before (64.1 baseline) | After (64.2) |
|---|---|---|
| `/likes` (GET local + POST proxy remote) | 10 | **5** |
| `/shares` (GET local + POST proxy remote) | 10 | **5** |
| actor fetches (`/ap/v1/u/{actor}` + remote proxy) | 1 (coalesced in 64.1) | 7 (1 local + 6 remote, coalesced) |
| feed | 1 | 1 |

The **10 + 10 → 5 + 5** drop is the fast path now engaging: the un-engaged
cards seed their counts from the embedded `likedCount` / `sharedCount` and
issue **no** collection walk. The residual 5 + 5 is **only** for the ~4 cards
andrew actually liked/boosted (alice's 3 notes + the RayvenMX status) — the
fast path still walks *one* collection per engaged card to recover the minted
Like/Announce activity id an unlike/un-boost (Undo) references. That walk is
proportional to the **viewer's own engagement**, not the feed size, so it is
small and functionally necessary (the server renders the count + the viewer's
net state, but not the minted activity IRI).

Stability: two consecutive clean reloads both measured 5 + 5 (no flakiness).
No regression: the home timeline renders (10 content cards, engagement bars
showing `0,0` for un-engaged and `1,1` for engaged items), **0 console errors**.

## Remaining Phase 64 topics (separate slices)

- **#3 (RayvenMX Create+Announce feed dup):** the same remote status renders as
  two content cards (a `Create` and an `Announce`) in the followed feed → 2×
  the per-card engagement walks. A feed **dedup** issue, not a fan-out issue —
  the per-card walk is correct; the feed serves the same object under two
  activity types. Separate slice.
- **Minted-id extension (optional refinement):** the server could render the
  minted Like/Announce activity IRI on the object (alongside `isLiked` /
  `isShared`) so the client never needs the per-engaged-card id-recovery walk.
  A bounded server addition (a per-(requester, object) activity lookup in
  `EnrichCollectionItemsAsync`) + a new client extension read. Deferred — the
  current residual is engagement-proportional and small.
- **#6 (public-feed double-fetch) + #7 (deleted-account avatar 410s):** unchanged,
  separate slices.
