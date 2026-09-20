# Inbox ① — Card Header Polish (Moderation, Strip, Boost Hint)

**Date:** 2026-09-20
**Status:** complete (build + test green; live verification pending)
**Commit:** `6819c0e`

## What was built

Three small, self-contained UI fixes from Inbox item ①, plus a deferral note on the
fourth (feed load feel).

### ①(1) — Remove moderation icons from object-card actor headers

Moderation (Block / Mute / Report) is now reserved for the actor detail page, not on
every feed card.

- `ObjectView.ShowModeration` default changed `true` → `false`
  (`apps/Iris.Web.Client/Components/ObjectView.razor.cs:37`).
- `ActorCard.ShowModeration` default changed `true` → `false`
  (`apps/Iris.Web.Client/Components/ActorCard.razor:114`).
- Explicit `ShowModeration="true"` added where moderation is the primary surface:
  - `ObjectDetail.razor:107` — the object detail page (primary moderation surface).
  - `Profile.razor:296` — the profile's own-posts tab (no actor-level controls).
  - `Search.razor:73` — search results (no actor-level controls).
- `ActorDetail.razor:31` already sets `ShowModeration="true"` on the actor-level
  `ActorCard`; its outbox template already sets `ObjectView.ShowModeration=false`
  (no change needed).

Feed cards (home timeline, community feed, notifications, reply threads, directory
lists) no longer show the per-card moderation buttons. The actor detail page retains
actor-level Block / Mute / Report.

### ①(2) — Extend the card header color strip to full width

The `--card-hue` gradient banner that previously wrapped only the avatar + handle
(`.object-header-actor`) now spans the entire header row, including under the
timestamp (`.object-time`).

- Moved the `background` / `border-radius` / `padding` from `.object-header-actor`
  to a new rule `.object-item:has([style*="--card-hue"]) > .object-header`
  (`apps/Iris.Web/wwwroot/css/app.css:844`).
- The `:has([style*="--card-hue"])` gate ensures only content cards (which set
  `--card-hue` inline) get the strip; Like / Follow / Actor cards (which have
  `.object-item` but no hue) and non-card headers (actor detail, notification rows)
  are unaffected.
- `.object-header-actor` keeps its flex layout but loses the background/padding.

### ①(3) — De-clutter the boost header

The "replying to <author>" hint on the boost card's "Boosted by" line was dropped
(`apps/Iris.Web.Client/Components/ObjectView.razor:234-241` removed). The boosted
post's own header already shows the author, making the hint redundant and busy.

### ①(4) — Speed up initial feed load feel (investigation only)

The server-side follow-feed builder (`IFollowFeedService.GetFeedAsync`) merges
remote follows' outboxes over the wire on every request — it is not served through
the local collection-page response cache. This is the root cause of the initial-load
latency. A full fix (caching, streaming, local-first serve) is a significant
server-side change and is **deferred to a dedicated slice**. No code change in this
commit.

## Files changed

| File | Change |
|---|---|
| `apps/Iris.Web.Client/Components/ObjectView.razor.cs` | `ShowModeration` default `true` → `false` |
| `apps/Iris.Web.Client/Components/ActorCard.razor` | `ShowModeration` default `true` → `false` |
| `apps/Iris.Web.Client/Components/ObjectView.razor` | Removed boost "replying to" hint |
| `apps/Iris.Web.Client/Components/Pages/ObjectDetail.razor` | Added `ShowModeration="true"` |
| `apps/Iris.Web.Client/Components/Pages/Profile.razor` | Added `ShowModeration=true` to post template |
| `apps/Iris.Web.Client/Components/Pages/Search.razor` | Added `ShowModeration="true"` |
| `apps/Iris.Web/wwwroot/css/app.css` | Moved hue gradient to full-width header rule |

## Tests

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test --filter "Category!=Slow"` — all green (1370 server + 106 web + others).
- 0 new coded tests (WASM manual-test policy, Phase 45+).

## Verification (pending)

Live Playwright verification against the Docker app is the next step per the loop
protocol: confirm feed cards have no moderation buttons, the header strip spans full
width, boost cards have no "replying to" hint, and the actor detail page still shows
moderation controls.

## Decision

The moderation default flip (true→false) is a deliberate UX call: per-card
moderation on every feed card was noisy; the actor detail page is the natural
home for Block / Mute / Report. Sites without an actor-level surface (object
detail, profile's own posts, search) opt back in with `ShowModeration="true"`.
