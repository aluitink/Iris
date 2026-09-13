# 126.2 — Notification row: null-safe icon/verb rendering

**Date:** 2026-09-13
**Type:** Bug fix (defensive)
**Scope:** `NotificationRow.razor` — actor-row rendering

## Problem

The notification card's actor-row div rendered the icon (`@NotificationIcon(Activity!)`)
and verb (`@VerbFor(Activity!)`) **outside** the `@if (Item is Activity ...)` guard.
The `Activity` property is `Item as Activity` — it returns null when `Item` is a
non-Activity `IObjectOrLink` (e.g. an `ILink`). The null-forgiving operator `!`
suppressed the compiler warning but would throw a `NullReferenceException` at
runtime, causing the entire actor-row div to render empty.

This was observed in the 125.1 audit as one notification card with avatar "B" but
empty name link and empty verb.

## Fix

Wrapped the icon + verb in `@if (Activity is { } notfAct)` so they only render when
the item is actually an `Activity`. Non-Activity inbox items (rare — the server
normally delivers Activity objects to the inbox) now render just the avatar + time
without crashing.

**Note:** The pattern variable was named `notfAct` to avoid a CS0128 collision with
the `act` pattern variable already in scope from the `ActorAvatar` component's
`a.Actor is { } act` expression on line 13.

## Verification

- All 9 notification cards render name + verb correctly (Bob: "sent you a follow
  request", carol: "liked a post", Andrew: "liked a post" / "sent you a follow
  request", alice: "liked a post").
- 0 console errors.
- Full test suite green: 1,979 pass, 0 fail, 1 skip.

## Files changed

- `apps/Iris.Web.Client/Components/NotificationRow.razor` — wrapped icon + verb in
  null check; renamed pattern variable to `notfAct`.
