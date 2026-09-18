# 148.12 — Unified actor card + actor-page polish

> 2026-09-16 · Slice 148.12 · Phase 148 (UI follow-up)

## What was built

The three actor-listing surfaces (the directory, and the actor page's Followers / Following tabs)
rendered actor rows with **two different controls**: the directory used a bespoke expandable
`DirectoryCard` (avatar + identity + summary, a click-to-expand "recent posts" section, and a
Mastodon-style footer holding the cached counters + a follow button), while the followers/following
tabs used the plainer `ActorCard` (no follow button). Meanwhile the actor detail page presented its
subject as an `ActorProfile` header with a separate, awkwardly-placed action column and its tab counts
were only partially sourced from the extension properties.

This change unifies everything on the **single `ActorCard` control**, trims each surface to what it
needs, and polishes the actor page:

- **Directory (`DirectoryCard`)**: reduced to a thin wrapper around the unified `ActorCard`
  (`ShowStats="true"` + a `FollowButton`; `ShowModeration="false"`). The click-to-expand "recent posts"
  section — and all of its supporting state and fetch logic in `Directory.razor` (`_expandedPosts`,
  `_expandedLoading`, `ExpandAsync`) — is removed. The card shows the follow button and the cached
  counters; moderation is left off (it belongs on the full profile view).
- **Followers/Following tabs (`ActorListPanel`)**: each `ActorCard` now takes a `FollowButton` as
  child content, so these tabs render the same unified card (follow + stats) the directory uses.
- **Actor detail page (`ActorDetail.razor`)**: the subject is now presented as the unified `ActorCard`
  (banner + large avatar + identity + summary + **follow + moderation**) instead of an `ActorProfile`
  header + a separate action column. The follow button and the block/mute/report actions live together
  in the card's actions column — this is the one place moderation is shown (the "full profile view").
  The redundant **summary of counters in the header is removed** (`ShowStats="false"`): the three tabs
  (`Posts (N)` / `Followers (N)` / `Following (N)`) already carry those counts, all sourced from the
  `iris:` extension properties the server renders on the actor document. The actor's icon is enlarged
  (5.5rem) and the banner is taller (8rem) so the page header reads as a profile.
- **Clickable cards, equal size/shape**: the whole `ActorCard` body is now a link to the actor's
  profile page (`OpenActorAsync` → `NavigationManager`), so clicking a card in the directory, the
  search results, or the followers/following tabs opens the actor page where moderation is managed.
  The actions column (follow / moderate) stop-propagates so a button click does not also navigate.
  A `min-height`/`max-height` on non-bannered cards keeps actors without an image or summary rendering
  at the same size and orientation as the others, and the bannered card body is given a centered
  `min-height` so a follow button without a summary does not hug the card's bottom. The follow /
  moderate buttons get a little right-edge breathing room.

## Key types & files

- `apps/Iris.Web.Client/Components/DirectoryCard.razor` — collapsed to the unified `ActorCard`
  wrapper (`ShowModeration="false" ShowStats="true"` + `FollowButton`); the expand button, chevron,
  footer, and posts section are gone.
- `apps/Iris.Web.Client/Components/Pages/Directory.razor` — dropped the expansion state + `ExpandAsync`
  and now renders `<DirectoryCard Actor="…" />`.
- `apps/Iris.Web.Client/Components/ActorListPanel.razor` — each `ActorCard` gains a `<FollowButton>`
  child + `ShowModeration="false"` (the followers/following tabs now match the directory: follow only).
- `apps/Iris.Web.Client/Components/ActorCard.razor` — the whole card body is now a link to the actor's
  profile page (`OpenActorAsync` → `NavigationManager`; the actions column stop-propagates); the handle
  is a plain `<span>` (the card, not the handle, is the link).
- `apps/Iris.Web.Client/Components/Pages/ActorDetail.razor` — header is now one `ActorCard`
  (`ShowModeration="true" ShowStats="false"` + `FollowButton`); the standalone stats/action blocks are
  removed; the now-dead collection-count fallbacks (`LoadCountsAsync`, `GetPostCountAsync`,
  `GetPostCountAnonymousAsync`, `GetFirstPageTotal(Anonymous)Async`) are deleted — the tab counts are
  read straight from the `iris:` extension props; the signed-out hint reads "Sign in to follow or
  moderate".
- `apps/Iris.Web.Client/wwwroot/css/app.css` (and the identical copy in
  `apps/Iris.Web/wwwroot/css/app.css`) — the base `.actor-card` now carries the card framing (border /
  radius / padding) + a `cursor:pointer` hover for the clickable state; a `min-height`/`max-height` on
  non-bannered cards keeps no-image/no-summary cards the same size; bannered card bodies get a centered
  `min-height` so a follow button without a summary does not hug the bottom; the follow/moderate buttons
  get a little right-edge breathing room; the obsolete directory expansion/footer/chevron/post rules,
  the old `actor-detail-*` rules, and the now-dead `.actor-card-handle:hover` link rule are removed;
  enlarged `.actor-profile-avatar` (4rem → 5rem).

## Tests

No new coded tests (pure UI-presentation change; the Blazor component suite for this phase is
Playwright-driven, and the only bUnit component tests in the repo target the *sample* app's own
components, which are untouched). Build verified: `Iris.Web.Client` and `Iris.Web` compile clean
(0 warnings, 0 errors). **Visually verified in the running app** (signed in as a local actor):
(1) the directory cards show follow + the cached counters, no block/mute/report, no expansion, and are
clickable; (2) the actor page header is one card with **follow + block/mute/report** together (no stat
summary), a larger avatar, and the Posts/Followers/Following tabs each show their count; (3) the
Followers/Following tabs render the same unified card with only a follow button; (4) clicking a card
navigates to that actor's profile page; (5) actors without an image/summary render at the same size and
orientation as the others, and the follow button has breathing room (does not hug the card's bottom).

## Decisions

- **One `ActorCard` everywhere, not per-surface variants.** The directory's expandable card and the
  list tabs' card were the same concept rendered two ways. The follow button is a first-class part of
  the card (its `ChildContent` slot), so every surface that lists actors gets follow + the cached
  counters for free. The "recent posts" expansion is dropped — an actor's posts are reachable from the
  card's handle link (the actor page's Posts tab), so the inline preview was redundant.
- **Moderation is reserved for the full profile view.** Block / mute / report are context-heavy actions;
  showing them on every card in a dense list (directory, followers/following) cluttered the UI. The
  card keeps the `ShowModeration` parameter (default `true`, so a standalone profile card still offers
  them), but the directory and the actor-page header opt out (`ShowModeration="false"`).
- **No stat summary on the actor page — the tabs are the summary.** The header's "N posts / N
  following / N followers" line duplicated the counts already shown on the Posts / Followers /
  Following tabs, so the header drops them (`ShowStats="false"`) and the redundant collection-counting
  fallback code is removed. The tab counts come only from the `iris:` extension properties the server
  renders on every local actor document; a remote actor from a non-Iris instance has none, so its tabs
  show no count.
- **The actor page uses `ActorCard`, not a new control.** It already carries the banner, large avatar,
  identity, summary, and an actions slot — exactly the profile header the page needs, plus the follow
  button. Framing it as a card (via `.actor-detail-header .actor-card` CSS) gives the page-header
  treatment without a second component.
