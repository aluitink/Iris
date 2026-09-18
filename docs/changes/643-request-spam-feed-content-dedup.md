# 64.3 — home-feed content-object coalescing (request spam, topic #3)

Phase 64 continues cutting the *number* of calls the WASM client makes. 64.3 is
the next slice: eliminate the **home-timeline duplicate** where a single post
renders as **two** content cards — once as the author's `Create` and once as a
follower's `Announce` (boost) — and the consequent **double** per-card
engagement walk that duplicate causes.

## Problem (620 tracker topic #3)

On `/home`, the RayvenMX status `117238846685341469` rendered as **two**
content cards:

- a `Create` — the remote author's own outbox item (the note **embedded**, with
  its content + the server-rendered `iris:likedCount` / `sharedCount`
  extensions), merged from the followed actor's outbox;
- an `Announce` — andrew's own boost of that same status (the note as a bare
  **link**, not embedded), merged from andrew's own outbox (which is always
  prepended to the followed feed).

They are **two distinct activities with two distinct IRIs**, so the feed's
existing by-IRI de-dup (`TruncateDedup`) did not remove either. The home
timeline's `IsContentItem` filter returns `true` for **both** a `Create` of a
`Note`/`Article` **and** any `Announce`, so both rendered as content cards.

The visible symptom (the same status shown twice) and the request-spam cost
(two engagement bars → the per-card `/likes` + `/shares` walks fired twice for
that item) both stem from the same root cause: **one piece of content surfaced
under two activity types**.

## Root cause

`FeedService.BuildFeedAsync` merges the actor's **own outbox** (always first —
so a boost's `Announce` is merged before the followed author's `Create`) with
the followed actors' outboxes, then de-duplicates **by activity IRI** only. A
`Create` and an `Announce` of the same object have different IRIs, so both
survive. The client has no cross-item state to coalesce them (its
`IsContentItem` is a per-item predicate), so the fix must be server-side.

## Fix

`src/Iris.Server/Services/FeedService.cs` — added a **by-content-object**
coalescing pass to the feed de-dup, applied after the by-IRI de-dup and before
the `MaxItems` cap:

- A new helper, `ContentObjectIri(item)`, resolves the IRI of the object a
  `Create`/`Announce` references, and whether that object is **embedded**
  (carried as a full object) or **link-only** (a bare reference). Non-content
  activities (`Like`, `Follow`, plain objects, etc.) and activities with no
  resolvable object IRI return "no object" and are never coalesced.
- `TruncateDedup` now groups surviving content items by referenced-object IRI.
  Per object it keeps a single **representative**: an item that carries the
  object **embedded** (the author's content-bearing `Create`, which renders the
  note + engagement counters in place without an extra fetch) is preferred over
  a link-only reference (a booster's bare `Announce`). The representative keeps
  its **first-seen position**, so feed ordering is stable. When a later embedded
  item beats an earlier link-only one, the link-only item is dropped and the
  embedded item is promoted to the representative's slot.
- The `MaxItems` cap is applied last, so a duplicate consuming a slot never
  displaces a legitimate item.

**Why the embedded `Create` wins (not the boost):** the `Create` carries the
full note (content + the `iris:` counter extensions the EngagementBar fast path
seeds from), so the home timeline renders it in place. A link-only `Announce`
would force the client to fetch the referenced note just to render it — an
extra request, the very thing Phase 64 is cutting. Preferring the embedded item
therefore fixes both the visible duplicate **and** the double engagement walk in
one change.

## Verification

**Unit tests** (`tests/Iris.Server.Tests/Services/FeedServiceTests.cs`, 5 new):

- `Create` (embedded) + `Announce` (link-only) of the same object → **one** card,
  and it is the embedded `Create` (not the boost).
- Two `Announce` (both link-only) of the same object → one card (first wins).
- Link-only `Announce` merged before an embedded `Create` (the own-outbox-first
  ordering) → the embedded `Create` is promoted to the representative; the boost
  is dropped.
- A `Like` + the note's `Create` → **both** kept (non-content items are never
  coalesced by object).
- Two distinct notes, one of which is also boosted → the boosted note renders
  once (as its `Create`), the other renders once, no over-dedup across objects.

All 27 `FeedServiceTests` pass. Full fast suite green (Iris.Server.Tests 974/975,
one known-flaky delivery test passes in isolation; Iris.Web 62/62; all other
projects green).

**Live (fresh origin `:8095`, `GET /ap/v1/u/andrew/feed` — the exact endpoint
the WASM home timeline fetches):**

- Before: the RayvenMX status `117238846685341469` appeared as **two** content
  items (an embedded `Create` from the author's outbox + andrew's link-only
  `Announce` boost from his own outbox).
- After: it appears as **exactly one** content item — the embedded `Create`
  (`mastodon.world/.../117238846685341469/activity`). andrew's boost `Announce`
  (`.../announces/06G8BTJTADVTB2Y6MYE0D5R2QR`) is **gone**. **0** duplicated
  content-object IRIs across the whole feed. The `Like` activities that
  reference the status are correctly preserved (they are not content).

The WASM client is **unchanged** (no `.razor` / client `.cs` touched), so no
client republish was required; the home timeline renders the surviving content
items through the existing `PagedCollection` + `IsContentItem` filter. (A full
signed-in UI re-pass was not run this turn: the persistent dev DB's login
passwords had drifted from the documented `Password1` — an environmental issue,
not a code defect. The change is purely server-side feed assembly, verified
directly at the feed endpoint the client consumes.)

## Remaining Phase 64 topics (separate slices)

- **Optional minted-id extension:** the server could render the minted
  Like/Announce activity IRI on the object so the client never needs the
  per-engaged-card id-recovery walk (the residual 5+5 from 64.2). A bounded
  server addition + a new client extension read. Deferred.
- **#6 (public-feed double-fetch) + #7 (deleted-account avatar 410s):** unchanged,
  separate slices.
