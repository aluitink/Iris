# 138.27 S2 follow-up — Vote-bar gate generalization + UI/UX polish (Phase 148)

**Date:** 2026-09-19
**Scope:** Client-only (WASM) + one small client-library extension read. No server-side change.

## What was built

Completes the 138.27 S2 finding (the vote-bar gate was Lemmy-IRI-shaped) plus a set of
UI/UX polish items discovered during the recurring UI/UX review.

### 1. Vote-bar gate generalization (138.27 S2)

The vote bar (upvote/downvote/score) was previously shown only for posts whose IRI matched the
Lemmy `/post/{id}` shape (`LemmyPostScore.TryParsePostIri`). This leaked the Lemmy-specific gate
to any downvote-capable peer.

- `ObjectView.IsLemmyPost` renamed to `HasVoteData` and generalized: the vote bar is now shown
  when the content object carries vote data — either the `iris:dislikedCount` extension (a non-zero
  dislike count, meaning the source instance supports downvotes) or a `LemmyPostScore` fetched from
  the Lemmy REST API (a Lemmy-IRI-shaped post). This allows **any** downvote-capable peer (not just
  Lemmy) to get the downvote affordance.
- `LemmyVoteBar.razor` renamed to `VoteBar.razor`; CSS class `lemmy-vote-bar` → `vote-bar`
  (and `lemmy-vote-btn`/`lemmy-vote-count`/`lemmy-vote-score` → `vote-btn`/`vote-count`/`vote-score`).
- New read-path extension helpers in `Iris.Client.IrisDocumentExtensions`:
  - `GetDislikedCount(IObject, string namespaceIri)` → `int?`
  - `GetIsDisliked(IObject, string namespaceIri)` → `bool?` (per-requester net dislike state)
- `LemmyCommunityRelayIntegrationTests` doc-comment updated to reference `VoteBar`.

### 2. Boost card parity + stretched link

The `Announce` (boost) card in `ObjectView` was a reduced card (no moderation buttons, no title,
no vote bar, not whole-card-clickable). It now matches the `Create`/direct-object card:

- Whole-card stretched-link overlay (`object-item--clickable` + `.object-card-link`) — the entire
  boosted-post card navigates to the post detail.
- `--card-hue` banner tint derived from the boosted post's author (same deterministic FNV-1a hue
  as every other content card, so the boosted post reads like any other post by that author).
- `CardModerationButtons` (block/mute/report) on the boosted post's author.
- `VoteBar` (when `HasVoteData`) or `EngagementBar` on the boosted post.
- `.object-boost-by` raised to `z-index:1` so the "Boosted by" line stays clickable above the
  stretched-link overlay.
- New `ObjectView` properties: `BoostCardLinkIri`, `BoostCardHueStyle`, `BoostActivityPublished`.

### 3. Notification Like/Announce full-card render

`NotificationRow` previously rendered a Like/Announce's target as a compact text preview
(`.notification-card__note`). It now renders the liked/boosted post as a **full object card**
(author, content, media, engagement) via `ObjectView`, so the notification shows the actual post
that was liked/boosted. The activity is passed (not just its object) so the card keeps the
"Liked" / "Boosted by" indicator.

- New `ObjectView` parameters: `SuppressTime` (suppress the card's own timestamp when the embedded
  note's `published` equals the notification activity's `published` — Mastodon sets both to the
  status creation time, so they'd otherwise render as an exact duplicate of the header time) and
  `SuppressBoostTime` (suppress the "Boosted by" line's own time; the notification header already
  carries the boost time, so the boosted post below shows only its own timestamp).
- New `NotificationRow` helpers: `TimeMatchesHeader(IObject)`, `IsLikeOrAnnounce(Activity)`.
- Dead `.notification-card__note*` CSS removed (no longer referenced).

### 4. Signed-out auth bar

`MainLayout` now renders a sticky bottom **auth bar** (`Log in` / `Sign up` buttons) for
signed-out visitors, replacing the per-page landing hero. The root page (`Home.razor`) no longer
renders its own `.landing-hero` block — it shows the public feed directly, and the auth bar is
available on every signed-out page.

- `.landing-hero` / `.landing-title` / `.landing-tagline` / `.landing-actions` CSS removed (dead).
- `.auth-bar` CSS: sticky bottom, opaque background, centered button pair.

### 5. Mobile / long-handle overflow fixes

- `.object-header` gets `min-width:0`; `.object-header-actor` gets `flex-shrink:1`; the actor-bar
  handle gets `min-width:0; max-width:100%` and an ellipsis clamp scoped to the card header
  (`.object-header .actor-bar-handle`) so a long remote handle can't push the card header past the
  card's edge on narrow screens.
- Card header on mobile: `.object-header` wraps; the timestamp (`margin-left:auto`) wraps to its own
  line below the handle, pinned right.
- 6-tab bars (notifications) scroll horizontally on one row at narrow widths
  (`.page-header-row .tab-bar` `overflow-x:auto`); the 3-tab settings bar keeps wrapping.
- Button-styled links keep their own text color on hover (`a.button:hover` etc. `color:inherit`) —
  the primary button's background IS `--accent`, so the generic `a:hover` color would paint the
  label the same color as its background.

## Key types / files

- `apps/Iris.Web.Client/Components/VoteBar.razor` (renamed from `LemmyVoteBar.razor`)
- `apps/Iris.Web.Client/Components/ObjectView.razor` / `.razor.cs`
- `apps/Iris.Web.Client/Components/NotificationRow.razor`
- `apps/Iris.Web.Client/Components/Layout/MainLayout.razor`
- `apps/Iris.Web.Client/Components/Pages/Home.razor`
- `src/Iris.Client/IrisDocumentExtensions.cs` (+2 read helpers)
- `apps/Iris.Web.Client/wwwroot/css/app.css` (+ `apps/Iris.Web/wwwroot/css/app.css`, kept identical)

## Verification

- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test --filter "Category!=Slow"`: green across all projects —
  Iris.Core.Tests 467, Iris.Client.Tests 188, Iris.Server.Tests 1363, Iris.Web.Tests 106,
  SampleBlazorClient.Tests 17, SampleServer.Tests 38, Iris.Client.Extensions.Tests 29,
  Iris.Server.Data.Tests 16, Iris.WebCrypto.Tests 3, Iris.Testing 12.
- `Iris.LiveInterop.Tests`: 5 failures, all `Lemmy container not reachable: 403` — known external
  Lemmy-instance availability (documented in UI/UX Pass 7/8 as remote-availability, not an Iris
  bug). Unrelated to this change.
- **Live verification via MCP Playwright (2026-09-19) — COMPLETE.** Clean entry (cookies +
  localStorage cleared, fresh WASM load) on `https://iris.luit.ink`:
  - **Signed-out** (`/`): public feed renders; boost cards show "Boosted by" + "Content unavailable
    — view original post" + EngagementBar; the new **auth bar** (Log in / Sign up) is present at the
    bottom; the landing hero is gone. 14 console errors, all the known remote-availability class
    (proxy 401s for remote actor avatars + one direct CORS fetch) — not Iris bugs.
  - **Signed-in as andrew** (technology community, Lemmy content): **16 vote bars** (Upvote/Downvote/
    score, e.g. `↑16 ↓1 15` + "5 comments"), boost-card **tinted banner strips** + moderation
    (Block/Mute) buttons on boosted authors, "Boosted by" headers. 3 console errors, all the known
    external `lemmy.ml` 500 (remote instance down) — not Iris bugs.
  - **Notifications**: full-card render confirmed — 20 rows, 4 Announce cards all with a full object
    card (`.object-card__object`); an Announce shows "Boosted by" + the boosted post's own timestamp
    **only** (boost time suppressed — `object-boost-time` count 0, no duplicate); Create/reply cards
    render the full reply/post card with tinted banner. **0 console errors.**
  - **Home (signed-in)**: stable across hard-refresh (28 object items before and after); all show
    EngagementBar (0 vote bars — andrew's feed is Mastodon content, so no vote-bar leak). **0 console
    errors.**
  - No new defects. The only console errors on any page were the known external remote-availability
    class (lemmy.ml 500, proxy 401/CORS) — consistent with Pass 7/8, not Iris regressions.

## Decision (recorded)

- **Vote-bar gate generalization (138.27 S2):** the gate is now data-driven (`iris:dislikedCount` /
  `iris:isDisliked` extension presence OR a Lemmy `LemmyPostScore`), not IRI-shaped. A peer that
  supports downvotes will emit the `iris:dislikedCount` extension, so any such peer gets the
  downvote affordance without Iris knowing its platform. The Lemmy `LemmyPostScore` path is kept
  for the Lemmy REST API (which is not in the AP document). This is the correct generalization —
  the Lemmy-IRI shape was always a proxy for "this peer supports downvotes."
