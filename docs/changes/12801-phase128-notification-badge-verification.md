# 128.1 — Unread notification badge: live-render verification

**Date:** 2026-09-13
**Type:** Verification (no code changes)
**Scope:** Confirm the `NotificationBadge` component renders and clears correctly in a live Playwright session

## Context

The feature matrix listed the badge's live-render as ☐ (unverified) due to a "WASM init lifecycle issue" noted during earlier verification. The data path (endpoint → unread count) was verified; the actual `<span class="nav-badge">` element render was never confirmed.

## Verification method

1. Started fresh Playwright session (caching disabled)
2. Navigated to `/login`, signed in as `alice` (adminpass123)
3. Landed on `/home`, waited 5s for WASM init
4. Searched accessibility tree for `.nav-badge` / "unread"
5. Clicked "Mark all as read" on `/notifications`
6. Checked badge state, navigated to `/directory`, checked again

## Results

| Check | Result |
|---|---|
| Badge renders when unread > 0 | ✅ `generic "1 unread": "1"` visible in nav |
| Badge shows correct count | ✅ Shows "1" (alice had 1 unread) |
| Badge disappears when unread = 0 | ✅ Gone after navigating away from notifications |
| Badge clears in real-time on notifications page | ⚠️ Does NOT clear — stays "1" until page navigation or 60s poll |

## Finding: stale badge on notifications page (LOW)

After clicking "Mark all as read" on `/notifications`, the badge in the nav still shows "1". The `NotificationBadge` component only updates via:
- Its own 60-second polling timer
- A full component re-render (page navigation)

The `Notifications.razor` page calls `MarkAllReadAsync` but does not signal the badge to refresh. The badge persists until the user navigates to a different page (which remounts the layout) or the 60s poll fires.

**Severity:** LOW — the badge clears within 60s at worst, and immediately on the next page navigation. Users who mark all as read and stay on the notifications page see a stale "1" for up to a minute.

**Potential fix:** Have `Notifications.razor`'s mark-all handler dispatch a callback or use a shared service (e.g., `CascadingValue` or an event aggregator) to notify `NotificationBadge` to reload. Alternatively, the badge could listen for a `StateHasChanged` signal from the notifications page.

**Decision:** Not fixing now. The 60s poll + navigation-clear behavior is acceptable for an MVP. If this becomes a user complaint, a small fix can wire the notifications page to invalidate the badge.

## Conclusion

The "WASM init lifecycle issue" noted in the feature matrix was a **false negative** — the badge renders correctly. The feature matrix ☐ on the D (polish) column for "Unread badge/count" can be closed.
