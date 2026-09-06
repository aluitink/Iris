# Production App — Feature Set & UX

> **Level 2.** Parent: [production-app-overview.md](production-app-overview.md). Child: [production-app-feature-matrix.md](production-app-feature-matrix.md).

## 1. Philosophy: functionality → experience → polish

Every feature below is built in three passes, and a feature should not move to the next pass until the previous one is genuinely done:

1. **Functionality (Phase A/B).** The feature works end-to-end: data flows correctly, federation is correct, nothing crashes. UI can be minimal/ugly. This is where *coverage* comes from — get every MVP feature to this bar before spending time polishing any single one.
2. **Experience (Phase C).** The feature is *pleasant*: loading states, empty states, error states, sensible defaults, keyboard/mobile-friendly, obvious next actions, no dead ends.
3. **Polish (Phase D).** The feature *looks* finished: visual consistency with the rest of the app, spacing/typography, iconography, subtle motion where it helps (not where it doesn't), dark mode if adopted.

Each pass is verified live with the **MCP Playwright server** — functionally (click/type through the real flow in a real browser) and visually (screenshot review) — not with an automated UI test suite. See [production-app-web-host.md](production-app-web-host.md) §6: no bUnit/component test project exists for `Iris.Web` yet, by design, so the app's screens can keep changing shape through Phase A/B/C without a test suite fighting the churn. Every slice should still leave a short record of what was clicked through and visually confirmed (matching the library's existing `docs/changes/` "Playwright-MCP manual pass" convention).

[production-app-feature-matrix.md](production-app-feature-matrix.md) has the exhaustive per-feature checklist against these three passes. This document covers the feature scope and the notable library gaps to close.

## 2. MVP feature scope

Mapping a "good social platform experience" onto what `Iris.Server`/`Iris.Client` already support (see [docs/reference/MISSING_FEATURES.md](../reference/MISSING_FEATURES.md) for the full F-01…F-31 capability inventory this draws from):

| Area | Screens/actions | Library support today |
|---|---|---|
| **Onboarding** | Register, login, logout | New — see [production-app-authentication.md](production-app-authentication.md). |
| **Profile** | View own/others' profile (avatar, name, summary, stats), edit own profile, view someone's outbox/liked | `Update` on own actor doc (F-02 resolved); `ActorProfile` component already exists in the sample. |
| **Compose** | Text post, reply (threaded), content warning/sensitive flag, media attachment, mention | Core `Create`/reply threading (F-12) exist; content-warning (F-28) and rich object rendering (F-11) are **open gaps** — see §3. |
| **Timeline / feed** | Home feed (followed actors + communities), a given actor's outbox, a given community's feed | `FeedService`/`GetFollowFeedAsync` (F-14), community feed all exist. |
| **Follow graph** | Follow/unfollow, followers/following lists, manually-approve-followers + accept/reject queue | Fully supported (`FollowActivityHandler`, `AcceptActivityHandler`, `RejectActivityHandler`, `manuallyApprovesFollowers`). |
| **Engagement** | Like (star), boost (announce), reply | Supported; **like *notifications*/counts from remote likers are an open gap** (see the PLAN.md 31.10 item already tracked for the *library* — this app should build its notification UI against whatever that slice lands, not duplicate the fix). |
| **Notifications** | A unified "what happened to me" feed: new follower/follow request, like, boost, reply, mention | **No first-class concept in the library yet** — today this is reconstructable from the actor's *inbox read* (`GET /ap/v1/u/{handle}/inbox`, Decision 056) by classifying activity types client-side. MVP: build the notifications screen as a client-side projection over the inbox read. **"Mark as read" for the MVP is a single `UserAccount.NotificationsReadAt` (`DateTimeOffset`) column** — updated whenever the notifications screen is viewed, compared client-side against each projected item's timestamp to render its read/unread state. This is *not* a full per-notification read-state model (no per-item dismissal, no "mark this one as read" without opening the whole list) — it is the smallest thing that satisfies the Phase-A/experience checklist without building the dedicated store below. A dedicated `INotificationStore`/service (server-side, persisted, markable-as-read *per item*) is a reasonable Phase 2 library-adjacent addition once the UI proves out what shape is actually useful — don't over-build it speculatively for the MVP.
| **Communities** | Browse/directory, view, join/leave, create, post to, member list, community moderation, community search | Fully supported server-side (`ICommunityStore`, community feed/search/members/moderation). Creation UI + a "browse all communities" directory screen are new UI work over existing endpoints. |
| **Moderation** | Block, mute, flag/report (per-user); an admin-facing moderation queue (flags raised, actioned) | Block/mute/flag all resolved (F-07 and friends) at the *actor* level. An **instance-wide admin queue** (all flags across all users, action buttons) is new UI work — the data (flags) already exists via `IModerationStore`, but an instance-wide *aggregate* view needs a new admin-scoped `Iris.Server` endpoint (+ `IActivityPubClient` method) exposing it, per the "no new APIs" rule ([production-app-overview.md](production-app-overview.md) §3) — not a direct store query from `Iris.Web`. |
| **Search / directory** | Global search (actors + content), instance directory | `GET /ap/v1/search` (F-13) exists; UI work only. |
| **Media** | Upload (compose attachment), avatar/header image upload | `POST /local/v1/u/{handle}/media` exists; UI work + the production blob-storage backend ([production-app-media-storage.md](production-app-media-storage.md)). |
| **Settings** | Account (change password), profile edit, relay subscriptions, key/algorithm info (read-only), instance info | Relay subscription (F-06) exists server-side; the rest is new UI + the new `IUserAccountStore` for account settings. |
| **Instance admin** *(Admin role only)* | Instance metadata (name, description), moderation queue, user list/roles | New UI; instance metadata already partly exists via `ActivityPubServerOptions`/NodeInfo — surfacing it as an editable settings screen is new. |

## 3. Library gaps worth closing as part of this effort (not blocking, but noted)

These are small, targeted library additions this app will likely want — evaluate each when the corresponding UI feature is being built, not upfront. **Consistent with the "no new APIs" rule** ([production-app-overview.md](production-app-overview.md) §3), every one of these is closed by extending `Iris.Client`/`Iris.Server` (a new client method, a new or widened endpoint) — never by adding a parallel `Iris.Web`-only API to work around a client/server gap:

- **F-28 (content warning / `sensitive`)** — surfacing an inbound `Note`'s `summary`/`sensitive` and letting compose set them. Small (`S` effort per the existing inventory).
- **F-11 (rich object rendering)** — interpreting `attachment`/`tag` more richly for feed rendering (images inline, mentions linked). Needed for a feed that doesn't look broken next to real Mastodon content.
- **Remote-liker like recording** (already tracked as PLAN.md item 31.10 for the library) — the notification/engagement-count UI depends on this; coordinate rather than duplicate.
- **A first-class notification concept** — only build this in the library once the client-side inbox-projection approach proves insufficient (e.g., performance, or "mark as read" semantics that don't fit the inbox model). Don't preemptively build it.

## 4. Suggested phased rollout across the whole feature set

Rather than finishing Phase A for every feature before starting Phase C for any, a reasonable MVP cadence (leave room for the agent to reorder based on what it learns):

- **Pass 1 (functionality):** onboarding → profile view/edit → compose (text only, no CW/media yet) → home feed → follow/unfollow → basic notifications (inbox projection) → communities (view/join/post) → search → block/mute/flag. Ship every screen at "it works."
- **Pass 2 (experience):** loading/empty/error states across all of the above; media upload + attachments in compose; content warnings; follow-request queue UX; moderation queue for admins; mobile layout pass.
- **Pass 3 (polish):** visual design pass, iconography, spacing/typography system, optional dark mode, subtle motion (page transitions, optimistic UI on compose/like/boost), accessibility audit (keyboard nav, screen-reader labels, color contrast).

See [production-app-feature-matrix.md](production-app-feature-matrix.md) for the exhaustive per-feature/per-pass checklist.
