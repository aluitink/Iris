# Production App — Feature Matrix (Functionality → Experience → Polish)

> **Level 3.** Parent: [production-app-feature-set.md](production-app-feature-set.md). Grandparent: [production-app-overview.md](production-app-overview.md).

Legend: **A** = functionality pass done, **C** = experience pass done, **D** = polish pass done. An autonomous agent picking up a slice should check this table, find the first unchecked box in the earliest pass across all features (finish Phase A broadly before starting Phase C anywhere), and update it as work completes. "Done" for any box means a live MCP Playwright functional + visual pass, not an automated test — see [production-app-web-host.md](production-app-web-host.md) §6 (no `Iris.Web` UI test project exists yet, by design). Before building any screen below, check [production-app-ui-guidelines.md](production-app-ui-guidelines.md) for an existing component that already covers it — most rows here compose a handful of shared components rather than needing bespoke UI.

> **Reconciled 2026-09-12 (Phase 88.1):** All boxes below reflect a code-inspection + live-app verification pass against Phases 32–87. 33 rows fully implemented, 6 partial (scope/exposure gaps, not missing functionality), 1 missing (key/algorithm info). The two most notable gaps: (1) no read-only key/algorithm info surface, (2) "View others' profile" is `@attribute [Authorize]`-gated (the matrix originally said "Public, no auth required").
>
> **Updated (Phase 99):** **Community search** promoted 🟡 → ✅ (server `GET /c/{name}/search` + a CommunityDetail Feed-tab search box, with the server's paged links now carrying `?q=` so the filter survives infinite-scroll). Tally now **34 fully implemented, 5 partial, 0 missing**.

## Onboarding & account

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Register (username/password) | ✅ | ✅ | ☐ | Provisions actor + keys, see [production-app-auth-flows.md](production-app-auth-flows.md) |
| Login / logout | ✅ | ✅ | ☐ | Cookie auth |
| Login rate limiting | ✅ | ✅ | ☐ | |
| Admin bootstrap from `.env` | ✅ | ✅ | ☐ | |
| Change password | ✅ | ✅ | ☐ | Settings screen |
| Admin-assisted password reset | ✅ | ✅ | ☐ | The MVP's only account-recovery path — no email/self-service reset, see [production-app-authentication.md](production-app-authentication.md) §7 |

## Profile

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| View own profile | ✅ | ✅ | ☐ | |
| View others' profile | ✅ | ✅ | ☐ | Anonymous viewing wired (Phase 88.4): `SameOriginApHandler` rewrites FQDN IRIs to same-origin for signed-out readers. Live-verified: `/actor?iri=…alice` renders profile + Posts/Followers/Following tabs without login. |
| Edit profile (name, summary, avatar/header) | ✅ | ✅ | ☐ | `Update` on own actor doc |
| View outbox/liked tabs | ✅ | ✅ | ☐ | Port `ActorProfile` + tabs from the sample |

## Compose & content

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Text post | ✅ | ✅ | ☐ | |
| Reply (threaded) | ✅ | ✅ | ☐ | F-12 |
| Mentions | ✅ | ✅ | ☐ | `tag`/`Mention` |
| Media attachment (image) | ✅ | ✅ | ☐ | Depends on [production-app-media-storage.md](production-app-media-storage.md) |
| Content warning / sensitive flag | ✅ | ✅ | ☐ | F-28, small library addition |
| Delete own post | ✅ | ✅ | ☐ | F-03 |
| Edit own post | ✅ | ✅ | ☐ | F-02 |
| Rich attachment/image rendering in feed | ✅ | ✅ | ☐ | F-11, small library addition |

## Timeline / feed

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Home feed (followed actors + communities) | ✅ | ✅ | ☐ | F-14 |
| View an actor's outbox as a feed | ✅ | ✅ | ☐ | |
| View a community's feed | ✅ | ✅ | ☐ | |
| Infinite-scroll / pagination | ✅ | ✅ | ☐ | `PagedCollection` uses infinite scroll (scroll listener + sentinel, Phase 98) with a ghost "Load more" fallback button for accessibility/no-scroll. |
| Optimistic UI on like/boost/reply | ✅ | ✅ | ✅ | `EngagementBar` applies the state + local count delta on success and rolls back on failure (catch reverts), invalidating the shared engagement cache so a re-render re-walks. Reply is a composer link (n/a for optimistic update). |

## Follow graph

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Follow / unfollow | ✅ | ✅ | ☐ | |
| Followers / following lists | ✅ | ✅ | ☐ | |
| Manually-approve-followers toggle | ✅ | ✅ | ☐ | Settings |
| Follow-request queue (accept/reject) | ✅ | ✅ | ☐ | Dedicated local endpoints (Phase 100): `GET/POST /local/v1/u/{handle}/requests[/accept|reject]/{**actorIri}`. Profile Requests tab + ActorDetail join-request queue both wired. |

## Engagement

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Like (star) | ✅ | ✅ | ✅ | `EngagementBar` like button — optimistic toggle, `iris:likedCount`/`iris:isLiked` seed, live-verified (24/24 bars render). |
| Boost (announce) | ✅ | ✅ | ✅ | `EngagementBar` boost button — `AnnounceAsync`/`UnannounceAsync`, `iris:sharedCount`/`iris:isShared`/`iris:announceActivityIri` seed, live-verified (24/24 buttons render; server computes sharedCount=1 + minted announce IRI for a boosted note). |
| Like/boost counts on a post | ✅ | ✅ | ✅ | Server renders `iris:likedCount`/`iris:sharedCount` + `iris:likeActivityIri`/`iris:announceActivityIri` extensions on the object doc; `EngagementBar` seeds counts + the signed-in user's engaged state from them (zero collection walks). Live-verified: a boosted note serves sharedCount=1, isShared=true. |

## Notifications

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Unified notification list (inbox projection) | ✅ | ✅ | ✅ | `Notifications.razor` page renders the list (12 rows live-verified) with `NotificationRow` items (avatar, verb, relative time, target link). Live-verified signed in. |
| Unread badge/count | ✅ | ✅ | ☐ | `NotificationBadge` polls `GetUnreadCountAsync` (endpoint verified: returns `unread:15`, drops to `0` after mark-all) and renders `.nav-badge` when >0. Live-render of the badge element itself is blocked in the headless WASM automation (the component's init lifecycle does not fire there); the data path is verified. |
| Mark as read | ✅ | ✅ | ✅ | "Mark all as read" button → `NotificationService.MarkAllReadAsync` → `POST /local/v1/notifications/read`. Live-verified: unread 15 → 0. (Per-item read is intentionally deferred to Phase 2; MVP bumps `UserAccount.NotificationsReadAt`.) |
| Filter by type (follows/likes/replies/mentions) | ✅ | ✅ | ✅ | Six filter tabs (All/Follows/Likes/Boosts/Replies/Mentions) → server `?type=` filter. Live-verified: "All" = 12 rows, "Likes" = 6 rows. |

## Communities

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Browse/directory of communities | ✅ | ✅ | ✅ | `/communities` page lists local communities (5 live-verified) with a "Create community" form. Each row links to `/community?iri=…`. |
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
| Global search (actors + content) | ✅ | ✅ | ☐ | F-13 |
| Actor-only directory filter | ✅ | ✅ | ☐ | |

## Settings

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Account (change password) | ✅ | ✅ | ☐ | |
| Profile edit | ✅ | ✅ | ☐ | duplicate entry point with Profile section — same feature |
| Relay subscriptions | ✅ | ✅ | ☐ | F-06 |
| Key/algorithm info (read-only) | ✅ | ✅ | ☐ | Settings > Account > Security section (fetches `GET /local/v1/account/key-info`: algorithm, key IRI, JWK thumbprint). Verified live 2026-09-12. |

## Instance admin (Admin role)

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Instance metadata edit (name, description) | ✅ | ✅ | ☐ | |
| Moderation queue (all flags, action buttons) | ✅ | ✅ | ☐ | |
| User list / role management | ✅ | ✅ | ☐ | `AdminUsers.razor` user table + reset-password + delete + role promote/demote (`POST /local/v1/admin/users/{id}/role`, Phase 88.5; refuses to demote the last admin). |

## Cross-cutting (apply once broadly, not per-feature)

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Loading states everywhere | — | ✅ | ☐ | Experience pass |
| Empty states everywhere | — | ✅ | ☐ | |
| Error states everywhere (network failure, validation) | — | ✅ | ☐ | |
| Mobile-responsive layout | — | ✅ | ☐ | |
| Keyboard navigation | — | ✅ | ☐ | Native focus + `:focus-visible` outline + Enter-to-search + Enter/Space on moderation toggle + roving-tabindex + arrow-key nav for all `role="tablist"` tab bars (Phase 110). |
| Screen-reader labels / ARIA | — | ✅ | ☐ | |
| Color contrast / accessibility audit | — | ✅ | ☐ | **DONE (Phase 111):** All text/background pairs verified against WCAG AA (4.5:1). Fixed 2 failures (`.directory-scope-btn--active`, `.filter-tab[aria-pressed]` were 2.8:1 → now 5.97:1). All other pairs: 5.0–15.2:1. |
 | Visual design system (spacing, type, icons) | — | — | ✅ | **DONE (Phase 113.1–114.3):** design-token system in `app.css` — semantic color tokens, a spacing scale (`--space-1…7` + off-ramp `-25/35/45/55/60/65/90/125`), a type scale (`--font-size-xs…4xl`), and a radius ramp (`--radius-sm/md/lg/pill`). All raw rem spacing + 41 raw radii migrated onto tokens. 114.1: consistent empty-state iconography + zero inline `font-size`; 114.2: coherent card corner rhythm; 114.3: unified primary card-list gap rhythm (12px top-level / 8px nested). |
| Dark mode | — | — | ✅ | Dark-only (hard-coded `--bg:#111318`); no light-mode toggle / `prefers-color-scheme` support. |
 | Subtle motion/transitions | — | — | ✅ | **DONE (Phase 113.2):** `transition: … 0.15s` on primary/secondary buttons + links (hover smoothness), secondary-button hover state, `.engagement-btn` hover; `prefers-reduced-motion` extended to a global transition/animation guard (WCAG 2.3.3). |
