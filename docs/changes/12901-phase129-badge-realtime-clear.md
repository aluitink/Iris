# 129.1 — Notification badge: real-time clear on mark-all

**Date:** 2026-09-13
**Type:** Bug fix (client)
**Scope:** Wire the notifications page to clear the nav badge in real-time after "Mark all as read"

## Problem

After clicking "Mark all as read" on `/notifications`, the badge in the nav stayed stale (showing the old count) until the 60-second poll fired or the user navigated to a different page. Discovered in 128.1 verification.

## Fix

Added an `UnreadCountChanged` event to `NotificationService`:

1. **`NotificationService.cs`** — Added `public event Action? UnreadCountChanged` and a `RaiseUnreadCountChanged()` method.
2. **`NotificationBadge.razor`** — Subscribes to `UnreadCountChanged` in `OnInitialized()`, unsubscribes in `Dispose()`. On event, calls `LoadAsync` to re-fetch the count and re-render.
3. **`Notifications.razor`** — Calls `NotificationService.RaiseUnreadCountChanged()` after `MarkAllReadAsync` succeeds.

The badge's own 60s poll remains as a fallback for background count changes (new notifications arriving while the user is on another page).

## Design decision

Used a simple C# event on the scoped service rather than `CascadingValue` or a dedicated event-aggregator service. The scoped service is already injected into both components, so the event is the least-invasive wiring. No new abstractions needed.

## Verification (Playwright, live Docker app)

1. Set alice's `NotificationsReadAt` to 2026-09-01 via DB → badge shows "13"
2. Navigated to `/notifications` → clicked "Mark all as read"
3. **Badge cleared immediately** — no stale "13" in the nav, no page navigation needed
4. 0 console errors

Before fix: badge stayed "13" until next poll or navigation. After fix: badge gone within the same render cycle.
