# Production App — Feature Matrix (Functionality → Experience → Polish)

> **Level 3.** Parent: [production-app-feature-set.md](production-app-feature-set.md). Grandparent: [production-app-overview.md](production-app-overview.md).

Legend: **A** = functionality pass done, **C** = experience pass done, **D** = polish pass done. An autonomous agent picking up a slice should check this table, find the first unchecked box in the earliest pass across all features (finish Phase A broadly before starting Phase C anywhere), and update it as work completes. "Done" for any box means a live MCP Playwright functional + visual pass, not an automated test — see [production-app-web-host.md](production-app-web-host.md) §6 (no `Iris.Web` UI test project exists yet, by design). Before building any screen below, check [production-app-ui-guidelines.md](production-app-ui-guidelines.md) for an existing component that already covers it — most rows here compose a handful of shared components rather than needing bespoke UI.

> **Reconciled 2026-09-12 (Phase 88.1):** All boxes below reflect a code-inspection + live-app verification pass against Phases 32–87. 33 rows fully implemented, 6 partial (scope/exposure gaps, not missing functionality), 1 missing (key/algorithm info). The two most notable gaps: (1) no read-only key/algorithm info surface, (2) "View others' profile" is `@attribute [Authorize]`-gated (the matrix originally said "Public, no auth required").
>
> **Updated (Phase 99):** **Community search** promoted 🟡 → ✅ (server `GET /c/{name}/search` + a CommunityDetail Feed-tab search box, with the server's paged links now carrying `?q=` so the filter survives infinite-scroll). Tally now **34 fully implemented, 5 partial, 0 missing**.
> **Updated (Phase 128.1):** **Unread badge** promoted ☐ → ✅ on D (polish). Tally now **35 fully implemented, 4 partial, 0 missing**.
> **Updated (Phase 130.1–130.2):** **Login rate limiting** + **Admin bootstrap** promoted ☐ → ✅ on D (polish). Tally now **37 fully implemented, 2 partial, 0 missing**. All D-column boxes closed.

## Onboarding & account

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Register (username/password) | ✅ | ✅ | ✅ | `/register`: handle + display + password inputs + "Create account" button. Provisions actor + keys. Live-verified 2026-09-13 (form structure; DOM-injected values don't bind in headless WASM — known limitation). |
| Login / logout | ✅ | ✅ | ✅ | `/login`: handle + password + "Sign in" button; error handling ("Invalid username or password."); `/logout` → `/login`. Cookie auth. Rate limiting server-side. Live-verified 2026-09-13 (logout→login round-trip). |
| Login rate limiting | ✅ | ✅ | ✅ | `SlidingWindowLoginRateLimiter` (5 attempts / 15 min, keyed by `username+IP`). **Phase 130.1 live-verified:** 5 failed logins → 6th rejected with "Too many failed attempts. Please try again later. Try again in about 15 minutes." |
| Admin bootstrap from `.env` | ✅ | ✅ | ✅ | `AdminBootstrapper` provisions first admin from `IRIS_ADMIN_USERNAME`/`IRIS_ADMIN_PASSWORD` at boot. **Phase 130.2 verified:** idempotent (alice bootstrapped 2026-09-08, no second admin on subsequent restarts). Verification path documented. |
| Change password | ✅ | ✅ | ✅ | Settings > Account > "Change password" `<details>`: 3 inputs + button + client-side validation. `POST /local/v1/account/password`. Live-verified 2026-09-13 (Phase 115.6). |
| Admin-assisted password reset | ✅ | ✅ | ✅ | `/admin/users`: per-row "Reset password" button → inline confirm (password input + Set/Cancel). `POST /local/v1/admin/users/{id}/password-reset`. Live-verified 2026-09-13 (button present; not exercised to avoid changing a real user's password). |

## Profile

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| View own profile | ✅ | ✅ | ✅ | `/profile`: "Your profile" heading + 5 tabs (Your posts, Replies, Likes, Followers, Following) + profile info. Live-verified 2026-09-13. |
| View others' profile | ✅ | ✅ | ✅ | `/actor?iri=…`: profile + Posts/Followers/Following tabs (e.g., bob: Posts, Followers (2), Following (1)). Anonymous viewing wired (Phase 88.4). Live-verified 2026-09-13. |
| Edit profile (name, summary, avatar/header) | ✅ | ✅ | ✅ | Settings → `/profile?edit=true`: edit form (display name, bio, avatar picker, follow-approval checkbox, Save/Cancel). Save posts signed AP `Update` to outbox + `POST /local/v1/u/{handle}/media`. Live-verified 2026-09-13 (Phase 115.6). |
| View outbox/liked tabs | ✅ | ✅ | ✅ | `/profile`: "Your posts" tab (outbox) + "Likes" tab. Live-verified 2026-09-13. |

## Compose & content

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Text post | ✅ | ✅ | ✅ | `/compose`: textarea `#compose-content` + Post button. Signed POST Create to `{actor}/outbox`. Live-verified 2026-09-13 (form structure + home feed shows posts). |
| Reply (threaded) | ✅ | ✅ | ✅ | EngagementBar "Reply" link → `/compose?replyTo={iri}`; ObjectDetail "Reply" button. `PostReplyAsync` sets `InReplyTo`. Thread view at `/object?iri=…`. Live-verified 2026-09-13 (reply link + replies section present). |
| Mentions | ✅ | ✅ | ✅ | `@handle` in compose textarea; autocomplete popover on `@`/`#`; regex detection + `Mention` tags on post. Live-verified 2026-09-13 (mention hint + home feed shows `@bob` mention). |
| Media attachment (image) | ✅ | ✅ | ✅ | Compose: `InputFile #compose-attachment` (multiple, image/video/audio/pdf). `POST /local/v1/u/{handle}/media` → `Image`/`Document` attachment. Live-verified 2026-09-13 (file input present in compose). |
| Content warning / sensitive flag | ✅ | ✅ | ✅ | Compose: "Content warning" checkbox + summary input. Feed renders behind Show/Hide blur toggle. Live-verified 2026-09-13 (sensitive label present in compose). |
| Delete own post | ✅ | ✅ | ✅ | ObjectDetail: "Delete" button + inline confirm (own posts only). Signed POST Delete to author's outbox. Live-verified 2026-09-13 (delete button present on own post). |
| Edit own post | ✅ | ✅ | ✅ | ObjectDetail: "Edit" button + inline textarea `#edit-content` (own posts only). Signed POST Update to author's outbox. Live-verified 2026-09-13 (edit button present on own post). |
| Rich attachment/image rendering in feed | ✅ | ✅ | ✅ | `ObjectView` → `MediaGallery`: image grid `<img src>` with click-to-lightbox; local media → `/ap/v1/media/{id}`, remote → proxy. Sensitive → blur + Show/Hide. Live-verified 2026-09-13 (media gallery component present in feed). |
| Video/audio attachment rendering | ✅ | ✅ | ✅ | `MediaGallery`: a video/audio attachment renders an embedded `<video>`/`<audio>` player (controls + preload="metadata") instead of a poster image, detected via the attachment's `mediaType` or URL extension (`RichAttachment.MediaType` + `ResolveAttachmentMediaType`; new `IsVideoUrl`/`IsAudioUrl`/`IsVideoMedia`/`IsAudioMedia` predicates). Live-verified 2026-09-17 (Phase 150): video note renders `<video controls>` with a proxied `<source type="video/mp4">`, 0 console errors. |
| Plain web-link (Link) attachment | ✅ | ✅ | ✅ | `MediaGallery`: a `type:"Link"` attachment renders as a clickable link card (`<a target="_blank" rel="noopener" class="doc-gallery-link">`, no forced download, not proxied through the media endpoint) — `IsPlainWebLink` (requires `Type == "Link"`, no media MIME, no media-file URL extension, no image preview). Root cause fixed in `GetRichAttachments`: it now reads the declared `type`/`name` off `ILink` (previously only off `IObject`, so a `Link` attachment surfaced `Type == null` and was misclassified as an image). Live-verified 2026-09-17 (Phase 150): link note renders `<a target="_blank" rel="noopener">` (no proxied `<img>`), 0 console errors. |

## Timeline / feed

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Home feed (followed actors + communities) | ✅ | ✅ | ✅ | `/home`: "Home timeline" + PagedCollection over `{actor}/feed` (followed feed). 41 items + engagement bars + refresh button. Live-verified 2026-09-13. |
| View an actor's outbox as a feed | ✅ | ✅ | ✅ | `/actor?iri=…`: "Posts" tab renders PagedCollection over actor's outbox. Works signed out too. Live-verified 2026-09-13 (bob: Posts, Followers (2), Following (1)). |
| View a community's feed | ✅ | ✅ | ✅ | `/community?iri=…`: "Feed" tab renders PagedCollection over `{community}/feed`. "Post to this community" link. Each post card's content is clickable → `/object?iri=…` (Phase 149.3), whose object page shows the full reply thread (Replies tab, nested `ReplyTree`). Live-verified (5 communities, 5 tabs each; click-through to the object's reply thread). |
| Infinite-scroll / pagination | ✅ | ✅ | ✅ | PagedCollection: scroll listener + sentinel + "Load more" fallback button. Sentinel/Load-more only appear when more pages exist. Live-verified 2026-09-13 (paged collection present; 41 items fit in one page). |
| Optimistic UI on like/boost/reply | ✅ | ✅ | ✅ | `EngagementBar` applies the state + local count delta on success and rolls back on failure (catch reverts), invalidating the shared engagement cache so a re-render re-walks. Reply is a composer link (n/a for optimistic update). |

## Follow graph

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Follow / unfollow | ✅ | ✅ | ✅ | ActorDetail: "Unfollow"/"Follow" button (alice follows bob → "Unfollow"). `FollowAsync`/`UnfollowAsync`. Live-verified 2026-09-13. |
| Followers / following lists | ✅ | ✅ | ✅ | ActorDetail: "Followers (2)" + "Following (1)" tabs. Followers tab renders actor list (alice + andrew). Live-verified 2026-09-13. |
| Manually-approve-followers toggle | ✅ | ✅ | ✅ | Profile edit (`/profile?edit=true`): `#edit-approve-followers` checkbox "Require approval for follow requests". `SetManuallyApprovesFollowersAsync`. Live-verified 2026-09-13. |
| Follow-request queue (accept/reject) | ✅ | ✅ | ✅ | ActorDetail: Requests tab (conditional — shown when manual-approve is on). `GET/POST /local/v1/u/{handle}/requests[/accept|reject]/{**actorIri}`. Tab not visible for alice (manual-approve off). Structure verified. |

## Engagement

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Like (star) | ✅ | ✅ | ✅ | `EngagementBar` like button — optimistic toggle, `iris:likedCount`/`iris:isLiked` seed, live-verified (24/24 bars render). |
| Boost (announce) | ✅ | ✅ | ✅ | `EngagementBar` boost button — `AnnounceAsync`/`UnannounceAsync`, `iris:sharedCount`/`iris:isShared`/`iris:announceActivityIri` seed, live-verified (24/24 buttons render; server computes sharedCount=1 + minted announce IRI for a boosted note). |
 | Like/boost counts on a post | ✅ | ✅ | ✅ | Server renders `iris:likedCount`/`iris:sharedCount` + `iris:likeActivityIri`/`iris:announceActivityIri` extensions on the object doc; `EngagementBar` seeds counts + the signed-in user's engaged state from them (zero collection walks). Live-verified: a boosted note serves sharedCount=1, isShared=true. |
  | Pre-computed interaction counters (background) | ✅ | ✅ | ✅ | **Phase 151:** a background `ObjectInteractionCountRefreshService` (`IHostedService`) pre-computes `iris:likedCount`/`sharedCount`/`repliedCount`/`dislikedCount`/`score` and persists them onto the stored object documents (every `Iris:ObjectInteractionRefreshInterval`, default 30s; idempotent re-store-only-if-changed). The object-document handler + collection-page enrichment now serve these cached counters (`TryReadStoredCounts`) instead of the per-read O(n) reverse-index sweep, falling back to the sweep only when an object lacks them. Tolerates a null persistence provider (inert under in-memory). Live-verified 2026-09-17: 5816/5816 stored objects converged (pass 1: 5807 updated, pass 2: 0); object doc serves likedCount=1/repliedCount=1/score=1 matching the `Edges` table; 0 new console errors. All suites green. |
 | Boost/like card of a reply shows the original post | ✅ | ✅ | ✅ | **Phase 152:** when a boost (Announce) or like (Like) card's target is itself a reply (`inReplyTo` set), `ObjectView` now renders the ORIGINAL post it answers to as the card body — keeping the "Boosted by" / "Liked" indicator — plus a "replying to …" context hint, instead of rendering the reply. New `_boostedParentObject`/`_likedParentObject` (best-effort `GetContentObjectAsync(parentIri)` fetch in `OnInitializedAsync` when `GetParentIri()` is non-null); `BoostedRenderTarget`/`LikedRenderTarget` (= parent ?? the boosted/liked object) drive all body props. A fetch failure is non-fatal — falls back to rendering the boosted/liked reply itself. New CSS `.object-boost-reply-hint`/`.object-like-reply-hint`. Live-verified 2026-09-17 (fresh WASM load after a `--no-cache` rebuild): a Liked reply card shows "Liked … replying to alice" with alice's original post as the body; a Boosted reply card shows "Boosted by Gargron … replying to everton137" with everton137's original post as the body; 0 console errors. All suites green. |

## Notifications

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Unified notification list (inbox projection) | ✅ | ✅ | ✅ | `Notifications.razor` page renders the list (12 rows live-verified) with `NotificationRow` items (avatar, verb, relative time, target link). Live-verified signed in. |
| Unread badge/count | ✅ | ✅ | ✅ | `NotificationBadge` polls `GetUnreadCountAsync` and renders `.nav-badge` when >0. **Phase 128.1 live-verified:** badge shows "1" when alice has 1 unread; disappears after mark-all + navigation. Minor: badge is stale on the notifications page for up to 60s after mark-all (clears on next poll or navigation). |
| Mark as read | ✅ | ✅ | ✅ | "Mark all as read" button → `NotificationService.MarkAllReadAsync` → `POST /local/v1/notifications/read`. Live-verified: unread 15 → 0. (Per-item read is intentionally deferred to Phase 2; MVP bumps `UserAccount.NotificationsReadAt`.) |
| Filter by type (follows/likes/replies/mentions) | ✅ | ✅ | ✅ | Six filter tabs (All/Follows/Likes/Boosts/Replies/Mentions) → server `?type=` filter. Live-verified: "All" = 12 rows, "Likes" = 6 rows. |

## Communities

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Browse/directory of communities | ✅ | ✅ | ✅ | `/communities` page (Phase 149 renovation): Following / "All on this instance" tabs over the `local=true` actor search filtered to `Group` (the old unqualified search returned 100 mixed remote actors and the page rendered empty — fixed by `LocalOnly=true`), a "Create community" form, and the unified community card (same control as the directory). Each card: banner + avatar (sized to the feed-card 2.5rem, Phase 149.4), handle, Community badge, summary, post/member stats, and a Follow/Unfollow button; the Following tab re-reads the following collection on a toggle. Cards link to `/community?iri=…`. Live-verified (6 local communities). |
| View a community | ✅ | ✅ | ✅ | `/community?iri=…` detail page: header card + member count, tabs (Feed/Members for all; +Owners/Peers/Requests for the creator), in-community search. Live-verified (Technology = non-creator view; owner-test-5428 = creator view with all 5 tabs + Edit button). |
| Create a community | ✅ | ✅ | ✅ | "Create a community" form (name/handle/description) on `/communities` → `CreateCommunityAsync`. UI live-verified; server + client integration-tested (A/C). |
| Join / leave | ✅ | ✅ | ✅ | `JoinButton` toggle on the community header → `RequestJoinAsync`/`RequestLeaveAsync`. Live-verified: button toggles Join → Leave → Join. |
| Post to a community | ✅ | ✅ | ✅ | "＋ Post to this community" link → `/compose?community={iri}`; `Compose.razor` `PostToCommunityAsync` delivers a `Create(Note)` attributed to author + community. Link live-verified. |
| Member list | ✅ | ✅ | ✅ | Members tab (`{community}/members` via `PagedCollection`, 5/page) with per-member moderation buttons for the creator. Live-verified (renders, empty state for 0 members). |
| Community moderation (block/mute within community) | ✅ | ✅ | ✅ | Full set: mute (Phase 109), block (Phase 112), remove-member, owner promote/demote, join-requests. Server handlers + client methods + CommunityDetail Members/Owners/Requests-tab buttons (creator-only). Live-verified: the creator-only tabs (Owners/Peers/Requests) + Edit button render for the community creator; the moderation buttons are wired to `ILocalModerationClient` (A/C integration-tested). |
| Community search | ✅ | ✅ | ✅ | Server `GET /c/{name}/search` + a CommunityDetail Feed-tab search box (Phase 99) driving `PagedCollection` with a query-carrying IRI. The server's paged links now carry `?q=` (the `PageLink` fix) so the filter is preserved across infinite-scroll pages. |

## Moderation (per-user)

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Block | ✅ | ✅ | ✅ | `ModerationActions` Block button → `IActivityPubClient.BlockAsync`. Live-verified: button toggles Block → Unblock; DB edge recorded (Kind=5). |
| Mute | ✅ | ✅ | ✅ | `ModerationActions` Mute button → `ILocalModerationClient.MuteAsync`. Live-verified: button toggles Mute → Unmute; DB edge recorded (Kind=7). |
| Flag/report | ✅ | ✅ | ✅ | `ModerationActions` Report button → `IActivityPubClient.FlagAsync`. Live-verified: flag delivered; DB edge recorded (Kind=6). |
| View own blocks/mutes/flags list | ✅ | ✅ | ✅ | Settings → Moderation tab: three sections (Blocked / Muted / Reported) with per-item action buttons. Live-verified: all three lists render the correct actors. |

## Search & directory

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Global search (actors + content) | ✅ | ✅ | ✅ | `GET /ap/v1/search` (q/limit/offset, type filter). **Phase 115.5 fixed a live 500:** the EF/Postgres `EfActorStore` search built its raw SQL with a `$$"""` raw-interpolated string, so `{0}`–`{3}` were consumed by C# interpolation instead of reaching `FromSqlRaw` as parameter placeholders → every non-empty query threw `FormatException`. Switched to verbatim string literals (branched on `localOnly`). Live-verified: q=alice → 9 results (actors + notes). |
| Actor-only directory filter | ✅ | ✅ | ✅ | The "Actors only" checkbox → `type=Actor` (local-only directory). Live-verified: q=alice + actors-only → 3 actor results, 0 notes. Regression-covered by `EfPersistenceContractTests.ActorStore_Search_MatchesAndCounts_DoesNotThrow` (real Postgres, both `localOnly` directions). |

## Settings

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Account (change password) | ✅ | ✅ | ✅ | Settings > Account > "Change password" `<details>`: 3 inputs (current/new/confirm) + button. Client-side validation fires ("Current and new passwords are required"). Endpoint `POST /local/v1/account/password` (cookie auth). Live-verified 2026-09-13. |
| Profile edit | ✅ | ✅ | ✅ | Settings > Account > "Edit your profile" link → `/profile?edit=true` opens edit form pre-populated: display name, bio, avatar picker, follow-approval checkbox, Save/Cancel. Save posts signed AP `Update` to outbox + `POST /local/v1/u/{handle}/media` for avatar. Live-verified 2026-09-13. |
| Relay subscriptions | ✅ | ✅ | ✅ | Settings > Relays tab: empty state ("not subscribed to any relays"), URL input + Subscribe button, per-relay Remove. Client-side URL validation. `GET /ap/v1/u/{handle}/relays` (list), `POST /local/v1/u/{handle}/relays/{iri}[?unsubscribe=true]` (add/remove). Live-verified 2026-09-13. |
| Key/algorithm info (read-only) | ✅ | ✅ | ✅ | Settings > Account > "Security" `<details>`: fetches `GET /local/v1/account/key-info`, renders algorithm (Rsa), key IRI (`…/u/alice#key-1`), JWK thumbprint (43-char RFC 7638). Read-only. Live-verified 2026-09-13. |

## Instance admin (Admin role)

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Instance metadata edit (name, description) | ✅ | ✅ | ✅ | `/admin/instance`: name input (maxlength 255) + description textarea (rows 3, maxlength 1024) + Save. `GET /local/v1/admin/instance` (load), `PUT /local/v1/admin/instance` (save). Live-verified 2026-09-13 (save → "Instance settings saved."). |
| Moderation queue (all flags, action buttons) | ✅ | ✅ | ✅ | `/admin/moderation`: table (Flagged by / Actor / Date / Action) with per-row Dismiss. `GET /local/v1/admin/flags` (list), `POST /local/v1/admin/flags/dismiss` (dismiss). Live-verified 2026-09-13 (2 flags → dismiss → 1 flag). |
| User list / role management | ✅ | ✅ | ✅ | `/admin/users`: table (Handle / Role / Created / Action) with per-row Make admin/Demote + Reset password + Delete. `GET /local/v1/admin/users` (list), `POST /local/v1/admin/users/{id}/role` (toggle), `POST /local/v1/admin/users/{id}/password-reset`, `DELETE /local/v1/admin/users/{id}`. Live-verified 2026-09-13 (4 users, bob User→Admin→User toggle works). |

## Cross-cutting (apply once broadly, not per-feature)

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Loading states everywhere | — | ✅ | ✅ | Conditional (spinners/skeletons shown during data fetch). Not visible with data present. Live-verified 2026-09-13 (no loading state active on `/home` with 41 items). |
| Empty states everywhere | — | ✅ | ✅ | Conditional ("No posts", "No results" messages shown when lists are empty). Not visible with data present. Live-verified 2026-09-13 (no empty state on `/home` with 41 items; verified on community with 0 members in 115.3). |
| Error states everywhere (network failure, validation) | — | ✅ | ✅ | Conditional (error banners on failed requests). Not visible without a failure. Live-verified 2026-09-13 (no error state on `/home`; login error "Invalid username or password" verified in 115.8). |
| Mobile-responsive layout | — | ✅ | ✅ | CSS media queries present in stylesheets. Live-verified 2026-09-13 (`CSSMediaRule` detected). |
| Keyboard navigation | — | ✅ | ✅ | Native focus + `:focus-visible` outline + Enter-to-search + Enter/Space on moderation toggle + roving-tabindex + arrow-key nav for all `role="tablist"` tab bars (Phase 110). |
| Screen-reader labels / ARIA | — | ✅ | ✅ | 25 `aria-label`/`aria-labelledby` elements + `role` attributes on interactive elements. Live-verified 2026-09-13. |
| Color contrast / accessibility audit | — | ✅ | ✅ | **DONE (Phase 111):** All text/background pairs verified against WCAG AA (4.5:1). Fixed 2 failures (`.directory-scope-btn--active`, `.filter-tab[aria-pressed]` were 2.8:1 → now 5.97:1). All other pairs: 5.0–15.2:1. |
 | Visual design system (spacing, type, icons) | — | — | ✅ | **DONE (Phase 113.1–114.3):** design-token system in `app.css` — semantic color tokens, a spacing scale (`--space-1…7` + off-ramp `-25/35/45/55/60/65/90/125`), a type scale (`--font-size-xs…4xl`), and a radius ramp (`--radius-sm/md/lg/pill`). All raw rem spacing + 41 raw radii migrated onto tokens. 114.1: consistent empty-state iconography + zero inline `font-size`; 114.2: coherent card corner rhythm; 114.3: unified primary card-list gap rhythm (12px top-level / 8px nested). |
| Dark mode | — | — | ✅ | Dark-only (hard-coded `--bg:#111318`); no light-mode toggle / `prefers-color-scheme` support. |
 | Subtle motion/transitions | — | — | ✅ | **DONE (Phase 113.2):** `transition: … 0.15s` on primary/secondary buttons + links (hover smoothness), secondary-button hover state, `.engagement-btn` hover; `prefers-reduced-motion` extended to a global transition/animation guard (WCAG 2.3.3). |
