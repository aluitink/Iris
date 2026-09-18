# 117.2 — Notifications Card Layout

## Summary

Upgraded the notifications page from a flat list (`ul.notif-list > li.notif-row`) to a
card-based layout (`div.notif-cards > div.notification-card`). Each notification is now
a self-contained card with a structured header (avatar + display name + verb + timestamp)
and a contextual body section that varies by activity type.

## Changes

### NotificationRow.razor

- Rewritten from a flat `<li>` row to a `<div class="notification-card">` with:
  - `notification-card__header`: avatar image + display name link + verb + relative time
  - `notification-card__body`: type-specific content (see below)
- New helper methods:
  - `RenderCardBody()` — returns a `RenderFragment` that renders the body section
  - `GetDisplayName()` — prefers `preferredUsername` over raw IRI label
  - `GetIconIri()` — extracts icon IRI from actor's `icon` property
  - `IsSelfDeleteActivity()` — detects self-removal activities
- Body rendering by activity type:
  - **Create (Note/Article/Video)**: note author link + content preview (truncated) + "View →" link
  - **Like/Announce/other with object IRI**: target link with label
  - **Follow (target IRI only)**: actor link with handle
  - **Self-delete**: "deleted their account" text

### Notifications.razor

- Changed `<ul class="notif-list">` to `<div class="notif-cards">`
- Filter tabs and "Mark all as read" button unchanged

### app.css

- New `.notification-card` block with BEM-style classes:
  - `.notification-card` — card container (background, border, border-radius, hover)
  - `.notification-card__header` — flex row (avatar, name+verb, time)
  - `.notification-card__avatar` — 32px round avatar image
  - `.notification-card__actor` — name link (accent color)
  - `.notification-card__verb` — muted verb text
  - `.notification-card__time` — muted timestamp (right-aligned)
  - `.notification-card__body` — body container
  - `.notification-card__note` — note preview block
  - `.notification-card__note-author` — note author handle link
  - `.notification-card__note-content` — truncated content preview
  - `.notification-card__target` — target object/actor link
  - `.notification-card__self-delete` — self-delete text
  - `.notification-card__actions` — actions row
  - `.notification-card__view-link` — "View →" action link
  - `.notif-cards` — flex column gap container

## Verification

- `dotnet build` — 0 errors, 0 warnings
- `dotnet test` — all tests pass (known flaky federation tests pass in isolation)
- Live Playwright verification on `http://localhost:8088/notifications`:
  - Card layout renders correctly with header (avatar + name + verb + time) and body
  - Reply notifications show note author, content preview, and "View →" link
  - Boost/Like notifications show target object links
  - Filter tabs (All/Follows/Likes/Boosts/Replies/Mentions) all functional
  - "Load more" pagination works
  - No console errors (fixed initial render tree crash from attributes without wrapper element)

## Bug Fix

Initial implementation of `RenderCardBody()` called `builder.AddAttribute()` at the top
level of the fragment without a preceding `OpenElement()`, causing:
```
System.InvalidOperationException: Attributes may only be added immediately after frames of type Element or Component
```
Fixed by wrapping all body content in a `<div>` element before adding attributes.
