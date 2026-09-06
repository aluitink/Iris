# Production App — UI/UX Guidelines & Shared Component Inventory

> **Level 3.** Parent: [production-app-web-host.md](production-app-web-host.md). Grandparent: [production-app-overview.md](production-app-overview.md). Sibling: [production-app-web-host-structure.md](production-app-web-host-structure.md).

## 1. Why this document exists

With five feature areas ([production-app-feature-set.md](production-app-feature-set.md)) being built across three passes each, and an autonomous agent picking up slices independently, the single biggest UI risk isn't "it looks bad" — it's **quiet duplication**: two slices independently inventing two different paged-list components, or three different "post card" renderers that each grew their own attachment-handling logic. This document is the single place that answers *"does something already do this?"* before a new component gets written. **Read it before creating any new Razor component.** Update it the same turn a new shared component is added.

## 2. The rule

> Before adding a new component, check the inventory table in §3. If an existing component covers the same responsibility, **extend it** (a new parameter, a new render fragment/template, a new optional slot) instead of writing a near-duplicate. If you do add a genuinely new shared component, add a row to §3 in the same change.

This is the same discipline the sample already demonstrated organically — `CollectionBrowser` (Change 280) was built specifically to replace hand-rolled paged followers/following lists that had drifted apart, and `ActorProfile` (Change 278) was built once and reused verbatim for communities (Change 279) because a `Group` is an `Actor`. Do that reuse *proactively* this time instead of *after* the drift already happened.

## 3. Canonical component inventory

| Component | Responsibility | Source | Used by |
|---|---|---|---|
| `CollectionBrowser` | Walks an ActivityPub ordered collection page-by-page, rendering each item via a registered `ItemTemplate` (with a sane default fallback) and an optional `ItemActions` slot for per-row controls. **The one and only paged-list primitive** — every followers/following/members/search-results/notifications list goes through this, not a hand-rolled loop. | Ported from `SampleBlazorClient/Components/CollectionBrowser.razor` | Timeline, notifications, followers/following, community members, search results, moderation lists |
| `PagedCollection` | Paging for *computed* (non-collection-IRI) reads — the followed feed, a community feed/members merge, replies. Distinct from `CollectionBrowser` because these reads aren't a single walkable AS2.0 collection IRI. | Ported from `SampleBlazorClient/Components/PagedCollection.razor` | Home timeline, community feed, reply threads |
| `ActorProfile` | Profile header: avatar (or name-initial fallback), handle, display name, summary, IRI. Works identically for a `Person` or a `Group` (a community). | Ported from `SampleBlazorClient/Components/ActorProfile.razor` | Profile page, community page |
| `ObjectView` / `PostCard` *(rename candidate — decide once, don't run both names)* | Renders a single ActivityStreams object (a `Note`/post) in a feed or thread: author line, content, attachments, reply/like/boost affordances, content-warning collapse. **This is the single "how does a post render" component** — compose preview, timeline, thread view, and search results all use it. | Extend `SampleBlazorClient/Components/ObjectView.razor` | Timeline, thread view, compose preview, search results |
| `RawInspector` | Raw AS2.0 JSON view of any object/actor/activity. Kept as a debug/admin surface, not just a dev leftover — genuinely useful for a self-hosted admin diagnosing a federation issue. | Ported from `SampleBlazorClient/Components/RawInspector.razor` | Object detail (behind a toggle), admin tools |
| `ComposeBox` | Text input + reply/mention context + content-warning toggle + attachment picker + submit. **One component for both "new post" and "reply"** (a reply is a compose box with a pre-filled `inReplyTo` context, not a separate component). | New | Home timeline (new post), thread view (reply) |
| `NotificationList` | Renders the notification feed (see [production-app-feature-set.md](production-app-feature-set.md) §2 — an inbox-read projection for the MVP), grouped/typed (follow, like, boost, reply, mention). Built on top of `CollectionBrowser` (a templated `ItemTemplate`), not a parallel list implementation. | New, composes `CollectionBrowser` | Notifications page |
| `FollowRequestList` | The manually-approve-followers accept/reject queue. Built on `CollectionBrowser` with an `ItemActions` slot (Accept/Reject buttons), mirroring the sample's existing follower Block-button pattern. | New, composes `CollectionBrowser` | Settings / follow requests |
| `CommunityCard` | A compact community summary (name, member count, one-line description, join/leave button) for directory/search listings — **not** the full `ActorProfile` header (that's for the community's own page). | New | Community directory, search results |
| `ModerationQueue` | Admin-facing list of raised flags with action buttons (dismiss/action). Built on `CollectionBrowser`. | New, composes `CollectionBrowser` | Admin page, per-actor/per-community moderation tab |
| `MediaUploader` | Attachment picker + upload progress + preview thumbnail, wraps the `POST /local/v1/u/{handle}/media` call. | New | `ComposeBox`, profile/community avatar-header edit |
| `SettingsPanel` | Shared shell for a settings section (heading, form, save/cancel affordances) — account, profile, relays each get one instance of this shell, not three different-looking forms. | New | Settings page |
| `EmptyState` | A consistent "nothing here yet" placeholder (icon/illustration slot + message + optional call-to-action button). Every list-shaped component (`CollectionBrowser`, `PagedCollection`, `NotificationList`) renders this when a page is empty — don't let each screen invent its own empty-state copy/layout. | New | Anywhere a collection can be empty |
| `ErrorState` | A consistent "this failed to load" placeholder (message + retry button). Same reasoning as `EmptyState` — one shared look, not per-screen bespoke error text. | New | Anywhere a fetch can fail |
| `LoadingSpinner` / skeleton | A consistent in-progress indicator. Start with a simple spinner for the functionality pass; a skeleton-shape placeholder is a legitimate Phase C/D upgrade (swap the component, callers don't change). | New | Anywhere a fetch is in flight |
| `ConfirmDialog` | A shared "are you sure?" modal (delete post, block, leave community, etc.) — one implementation, parameterized by message/confirm-label, not a bespoke `<div>` per action. | New | Delete, block, leave, moderation actions |
| `Avatar` | Renders an actor's icon image or a name-initial fallback (the same fallback logic `ActorProfile` already has) at a given size — extracted so `CommunityCard`, `ObjectView`'s author line, and `NotificationList` don't each reimplement the initial-fallback logic. | New (extract from `ActorProfile`'s existing fallback logic) | `ActorProfile`, `CommunityCard`, `ObjectView`, `NotificationList` |

This table is expected to grow. When it does, prefer adding a row over adding a component that isn't in it.

## 4. The state contract (every data-bound component follows the same shape)

`PagedCollection`/`CollectionBrowser` already established the convention (see Change 275's initial-load re-render fix and Change 189's error-state guard): a data-bound component has exactly four visual states, and every new one should reuse the same shape rather than invent its own:

1. **Loading** — shown until the first fetch resolves (`LoadingSpinner`).
2. **Error** — shown when the fetch fails (`ErrorState`, with retry).
3. **Empty** — shown when the fetch succeeds with zero items (`EmptyState`).
4. **Loaded** — the actual content.

A component that fetches data and doesn't explicitly account for all four is presumed incomplete, not "done for now."

## 5. Visual/UX guidelines

- **Design tokens over hardcoded values, even before adopting a CSS framework.** Define a small set of CSS custom properties early (`--space-1`…`--space-5` spacing scale, `--font-size-*` type scale, semantic color roles like `--color-surface`/`--color-accent`/`--color-danger` rather than raw hex sprinkled through component styles). This costs almost nothing during the functionality pass and means the Phase D polish pass is a token-value swap (or a theme/dark-mode toggle) instead of a find-and-replace across every component's CSS.
- **Mobile-first, single-column by default.** The home timeline, notifications, and profile all read as a single vertical column on a narrow viewport; widen to a two-column (content + sidebar) layout above a tablet breakpoint. Don't design desktop-first and retrofit mobile — it's the more common real-world client for this kind of app.
- **Accessibility baseline from day one** (cheap now, expensive to retrofit): semantic HTML elements (`<button>`, `<nav>`, `<article>` for a post) over generic `<div>`s with click handlers; every icon-only control gets an `aria-label`; visible focus outlines are never suppressed; color is never the *only* signal (e.g., an error state has an icon + text, not just red text).
- **Every empty/loading/error state gives the user a next action** where one exists — "No posts yet — follow someone to fill your timeline" beats a bare "No items."
- **CSS approach**: reuse the sample's hand-rolled CSS (`wwwroot/css/app.css`-style) for the functionality pass, per [production-app-web-host.md](production-app-web-host.md) §5; the design-token layer above is meant to make that choice non-blocking regardless of whether a framework gets adopted later.

## 6. What "done" looks like

- A new UI slice that needs a "list of things," a "post," or a "confirm this" never introduces a second implementation of `CollectionBrowser`/`ObjectView`/`ConfirmDialog` — it composes the existing one.
- Every data-bound component visibly (and correctly) handles all four states in §4 — verified as part of its Playwright-MCP pass ([production-app-web-host.md](production-app-web-host.md) §6), not just the happy path.
- The inventory table in §3 stays current — a PR/slice that adds a genuinely new shared component updates this file in the same change.
