# Phase 154 — Notifications

**Status:** COMPLETE (live-verified, build clean 0/0, web tests 106/106, 0 console errors/warnings)
**Date:** 2026-09-17

## What

The notifications page previously stood out as looking different from every other page. This phase brings
it in line with the rest of the app:

1. **Tabs instead of pill selectors.** The bespoke `.notif-filter-tabs`/`.filter-tab` pill buttons are
   replaced with the shared `.tab-bar`/`.tab`/`.tab--active` tab pattern (the same control used by the
   actor profile, communities, and directory pages), with proper `role=tablist`/`tab`/`tabpanel` ARIA.
   The six filters (All / Follows / Likes / Boosts / Replies / Mentions) are unchanged. "Mark all as
   read" moves to the page-header row, beside the tab bar.
2. **New notifications stand out, and clear when read.** Each notification that is *new* — published
   after the user's last "mark all as read" and not yet viewed this session — is highlighted with a left
   accent bar, a tinted surface, and a "New" badge. "Mark all as read" clears every highlight (and the
   nav badge); clicking a notification's View/content link marks it viewed so its highlight clears
   immediately.
3. **Normal object cards.** A notification whose target is a content note (`Note`/`Article`) now renders
   the target through the shared `<ObjectView>` card (full header, engagement bar, etc.) — exactly like
   the cards in the feeds — instead of the old compact preview box. Non-content targets (Follow / Accept /
   Reject, and remote notes that arrive without the object inline) keep the compact row + "View note/post"
   link.

## How

Client + a small server addition.

### Server — `apps/Iris.Web/WebAppFactory.cs`

- `GET /local/v1/notifications` now also returns **`readAt`** — the account's `NotificationsReadAt`
  cursor (`DateTimeOffset?`, serialized as an ISO-8601 string, `null` when the user has never marked all
  as read). The cursor already exists server-side (it drives the unread count via `CountUnread`);
  surfacing it means **no per-item read flags need to be stored or serialized** — the client computes
  per-item newness from this single value.

### Client — `apps/Iris.Web.Client/Accounts/NotificationService.cs`

- `NotificationPage` record gains a `ReadAt` (`DateTimeOffset?`) field. `GetNotificationsAsync`'s
  fallback now passes `null` for it.

### Client — `apps/Iris.Web.Client/Components/Pages/Notifications.razor`

- **Tabs:** the pill markup is replaced with a `role=tablist` of `.tab` anchors (`.tab--active` for the
  selected filter) + a `role=tabpanel`; the "Mark all as read" button sits in the page-header row next to
  the tab bar.
- **Newness state:** new fields `_readAt` (`DateTimeOffset?`, captured from each fetched page) and `_seen`
  (a `HashSet<string>` of viewed IRIs this session). `IsNewNotification(item)` returns true when the item's
  `published` is after `_readAt` (or always, when `_readAt` is null) and its IRI is not in `_seen`.
  `MarkViewed(item)` adds an item's IRI to `_seen`. `LoadNotificationsAsync` stores `page.ReadAt` and
  clears `_seen` on a fresh load; `OnMarkAllReadAsync` bumps `_readAt` to `DateTimeOffset.UtcNow` so every
  visible highlight clears (no re-fetch needed).
- The list is an indexed `@for` over `_items`, passing `IsNew="@IsNewNotification(item)"` and
  `OnViewed="@(() => MarkViewed(item))"` to each `NotificationRow`.

### Client — `apps/Iris.Web.Client/Components/NotificationRow.razor`

- New params: **`IsNew`** (`bool`) and **`OnViewed`** (`EventCallback`).
- When `IsNew`, the card root gets `notification-card--new` and a `<span class="notification-card__new">
  New</span>` badge is rendered in the header.
- **`IsContentNote(IObject)`** (static) — true when the object's type is `Note` or `Article`.
- **`RenderCardBody`:** a new branch — when the activity's object is a content note (`IsContentNote`), it
  renders `<div class="notification-card__object"><ObjectView Item="noteObj" /></div>` (a full object card)
  instead of the compact preview box. The compact preview branch (renamed variable `otherObj`) is kept for
  non-content objects.
- **`OnViewedClick`** (`Task`) — invoked from the "View →" link and the note-content preview link
  (`EventCallback.Factory.Create<MouseEvent>(this, OnViewedClick)`), so clicking through to the target
  marks the notification viewed and clears its highlight. `MouseEvent` is aliased to
  `Microsoft.AspNetCore.Components.Web.MouseEventArgs`.

### CSS (`app.css`, kept identical in both copies)

- `.notification-card--new` — left accent bar (`border-left: 3px solid var(--accent)`), an accent-mixed
  border, and a tinted surface (`color-mix(in srgb, var(--accent) 7%, var(--surface))`).
- `.notification-card__new` — the small uppercase "New" badge (accent text, accent-tinted pill).
- `.notification-card__object` — wrapper for the full `ObjectView` card rendered inside a notification
  card (top margin + the inner card keeps its own border so it doesn't read as a double-bordered
  "card-in-a-card").

## Verification

- **Build:** `dotnet build -c Release` (full solution) — 0 warnings / 0 errors.
- **Tests:** `dotnet test tests/Iris.Web.Tests -c Release` — 106/106 passed, 0 failures.
- **Live (docker, `irisweb-iris-web-1`, `irisweb-db-1`):**
  - **Build gotcha (recurring):** `docker compose build --no-cache iris-web` used to force a recompile; a
    **fresh browser context** (close + reopen) is required to bust the browser's HTTP cache of the
    content-hashed WASM. Verified the new code is in the deployed wasm via a UTF-16LE string search for
    `notification-card--new`, `notification-card__object`, `Notification filters`, and `Mark all as read`.
  - **Tabs:** the page renders a `role=tablist` with six `.tab` controls; "All" is selected on load.
    Clicking **Likes** → 8 cards, all with the "liked" verb; clicking **Boosts** → 20 cards, all with the
    "boosted" verb. Tab switching + server `?type=` filtering both work.
  - **New/unread highlight:** the endpoint returns `readAt` (the `NotificationsReadAt` cursor). With the
    cursor back-dated (all notifications now "new"), **all 7 cards** show the left accent bar + tinted
    surface + a "New" badge. Screenshot-confirmed.
  - **Mark all as read clears everything:** after clicking "Mark all as read", **0** cards remain
    highlighted, the badges are gone, and the nav badge (previously "99+") disappears. The cursor is bumped
    to now. Screenshot-confirmed.
  - **Normal object cards:** the "replied" notification (Eugen Rochko / Gargron) renders the **full
    `<ObjectView>` card** — Gargron's avatar, name, Block/Mute/Report, "TO" recipients, post content,
    "In reply to …" context, and the Like/Boost/Reply engagement bar — exactly like the feeds.
    Notifications whose target is a remote note without the object inline (liked a post / boosted a post)
    correctly fall through to the compact row + "View note"/"View post" link.
  - **0 console errors, 0 warnings** across the whole session (login → notifications → tab switching →
    mark-all).

## Files

- `apps/Iris.Web/WebAppFactory.cs`
- `apps/Iris.Web.Client/Accounts/NotificationService.cs`
- `apps/Iris.Web.Client/Components/Pages/Notifications.razor`
- `apps/Iris.Web.Client/Components/NotificationRow.razor`
- `apps/Iris.Web.Client/wwwroot/css/app.css`
- `apps/Iris.Web/wwwroot/css/app.css`
- `PLAN.md`, `docs/ROADMAP.md`, `docs/plans/production-app-feature-matrix.md`
