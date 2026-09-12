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
| View others' profile | 🟡 | ☐ | ☐ | **GAP:** `ActorDetail.razor` is `@attribute [Authorize]`-gated — the matrix originally said "Public, no auth required". Anonymous viewing is NOT wired. |
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
| Optimistic UI on like/boost/reply | ✅ | ✅ | ☐ | Polish-pass item |

## Follow graph

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Follow / unfollow | ✅ | ✅ | ☐ | |
| Followers / following lists | ✅ | ✅ | ☐ | |
| Manually-approve-followers toggle | ✅ | ✅ | ☐ | Settings |
| Follow-request queue (accept/reject) | 🟡 | ☐ | ☐ | **PARTIAL:** Own-profile Requests tab + `FollowRequestRow` exist; relies on AP `Accept`/`Reject` to the remote inbox (no dedicated server-side accept/reject endpoint). |

## Engagement

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Like (star) | ✅ | ✅ | ☐ | |
| Boost (announce) | ✅ | ✅ | ☐ | |
| Like/boost counts on a post | ✅ | ✅ | ☐ | Depends on remote-liker recording (PLAN.md 31.10) |

## Notifications

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Unified notification list (inbox projection) | ✅ | ✅ | ☐ | See [production-app-feature-set.md](production-app-feature-set.md) §2 |
| Unread badge/count | ✅ | ✅ | ☐ | Derived client-side from `UserAccount.NotificationsReadAt` vs. each item's timestamp |
| Mark as read | ✅ | ✅ | ☐ | MVP (Phase A) = bump `UserAccount.NotificationsReadAt` on view, **not** a per-item read store; see [production-app-feature-set.md](production-app-feature-set.md) §2 for why a dedicated `INotificationStore` is deferred to Phase 2 |
| Filter by type (follows/likes/replies/mentions) | ✅ | ✅ | ☐ | All six filter tabs (All/Follows/Likes/Boosts/Replies/Mentions) + server `?type=` filter (Mentions = composite filter on Create activities with matching Mention tags, Phase 108). |

## Communities

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Browse/directory of communities | ✅ | ✅ | ☐ | |
| View a community | ✅ | ✅ | ☐ | Port the sample's community tabs |
| Create a community | ✅ | ✅ | ☐ | |
| Join / leave | ✅ | ✅ | ☐ | |
| Post to a community | ✅ | ✅ | ☐ | |
| Member list | ✅ | ✅ | ☐ | |
| Community moderation (block/mute within community) | 🟡 | ☐ | ☐ | **PARTIAL:** Server has `CommunityMuteHandler` + `CommunityRemoveMemberHandler` + owner promote/demote + join-requests; client UI covers remove-member + owner promote/demote + join requests, but **no community-scoped block/mute UI** surfaced. |
| Community search | ✅ | ✅ | ✅ | Server `GET /c/{name}/search` + a CommunityDetail Feed-tab search box (Phase 99) driving `PagedCollection` with a query-carrying IRI. The server's paged links now carry `?q=` (the `PageLink` fix) so the filter is preserved across infinite-scroll pages. |

## Moderation (per-user)

| Feature | A | C | D | Notes |
|---|---|---|---|---|
| Block | ✅ | ✅ | ☐ | F-07 |
| Mute | ✅ | ✅ | ☐ | |
| Flag/report | ✅ | ✅ | ☐ | |
| View own blocks/mutes/flags list | ✅ | ✅ | ☐ | |

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
| User list / role management | 🟡 | ☐ | ☐ | **PARTIAL:** User table + reset-password + delete exist; **role management** (promote/demote user to Admin) is NOT present. |

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
| Visual design system (spacing, type, icons) | — | — | ✅ | Polish pass |
| Dark mode | — | — | ✅ | Dark-only (hard-coded `--bg:#111318`); no light-mode toggle / `prefers-color-scheme` support. |
| Subtle motion/transitions | — | — | ✅ | `transition` on filter tabs + splash keyframes + `prefers-reduced-motion` guard. |
