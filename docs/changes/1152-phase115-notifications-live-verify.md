# Phase 115.2 — Notifications: Live Verification + Matrix Reconciliation

## What

Live-verified the notifications web integration and reconciled its D-column
(web-integration) matrix entries. The feature was **already fully implemented**;
this slice closes the verification gap and updates the feature matrix.

## What was verified (Playwright, signed in as alice)

### Unified notification list
- `Notifications.razor` (`/notifications`) renders the list: **12 rows** live,
  each a `NotificationRow` (avatar, user-centric verb e.g. "liked a post" /
  "sent you a follow request", relative time, target link).
- A mixed set of notification types renders correctly (follows, likes, replies).

### Filter by type
- Six filter tabs: All / Follows / Likes / Boosts / Replies / Mentions.
- Live-verified: **"All" = 12 rows → "Likes" = 6 rows** (the tab reloads with the
  server `?type=` filter; only Like notifications remain).

### Mark as read
- "Mark all as read" button → `NotificationService.MarkAllReadAsync` →
  `POST /local/v1/notifications/read`.
- Live-verified: **unread count 15 → 0** after clicking.

### Unread badge / count
- The unread-count **data path is verified**: `GET /local/v1/notifications/unread-count`
  returns `{"unread":15}` (drops to `{"unread":0}` after mark-all).
- `NotificationBadge` (in the `MainLayout` nav) polls this every 60s and renders
  a `.nav-badge` (capped at "99+") when the count is >0.
- **Limitation:** the badge element itself did **not** render in the headless
  WASM automation — the component's `OnInitialized` lifecycle does not fire in
  that environment (a pre-existing headless/WASM limitation, the same class of
  issue that blocks other authenticated-page interactions). The data path
  (endpoint + service) is verified working; the badge render is unverified
  headlessly but is standard Blazor (`@if (_unread > 0)`).

## Matrix reconciliation (D-column)

| Item | Before | After |
|---|---|---|
| Unified notification list (inbox projection) | ☐ | ✅ |
| Unread badge/count | ☐ | ☐ (data path verified; live-render blocked headlessly) |
| Mark as read | ☐ | ✅ |
| Filter by type | ☐ | ✅ |

The "Unread badge/count" row stays ☐ for the D column because the live-render
of the badge element could not be confirmed in the headless environment; its
data path is verified. (A / C columns remain ✅ — the server + library support
is complete and integration-tested.)

## Why a reconciliation slice

A and C were already ✅ with integration coverage. D was ☐ because the web
integration had never been live-verified and the matrix never reconciled. This
turn does exactly that — no new functional code (the badge was already
implemented; its headless non-render is an environment limitation, not a code
defect).

## Verification

- `dotnet build` clean; `Iris.Web.Tests` 95/95.
- Live Playwright verification as above (list, filter 12→6, mark-all 15→0,
  unread endpoint 15→0).

## Files

- `docs/plans/production-app-feature-matrix.md` (3 D-column rows reconciled;
  1 left ☐ with a limitation note).
