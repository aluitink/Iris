# 121.7 — Boosted posts: show inline content preview

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Problem

Announce (boost) activities in the feed carry their target as a bare link (`"object": "https://..."`) — no embedded object. The `ObjectView` Announce branch fell through to the `else` clause and rendered only "View boosted post →" with no content preview, unlike Mastodon which shows the boosted post's author, text, and media inline.

## Fix

Added lazy-fetch in `ObjectView.OnInitializedAsync`: when an `Announce`'s target is a bare link (no `ActivityEmbeddedObject`), the component fetches the boosted object via `Session.Client.GetObjectAsync()` (same pattern as the Like lazy-fetch added in 119.1). Three computed properties were added:

- `BoostedObject` — resolves to `ActivityEmbeddedObject ?? _announcedObject`
- `BoostedAuthorIri` — prefers the activity-level author, falls back to the boosted object's `attributedTo`
- `BoostedPublished` — prefers the activity-level published, falls back to the boosted object's `published`

The razor template's Announce branch now uses these properties, rendering the author bar, timestamp, content preview (markdown→HTML), and media gallery — matching the embedded-object path.

## Verification

- Live-verified: a boost of a remote post (RayvenMX on mastodon.world) now shows the author, timestamp, and content preview instead of just "View boosted post →".
- 0 console errors.
- `dotnet build` + `dotnet test` green (1126 server + 95 web tests pass).
