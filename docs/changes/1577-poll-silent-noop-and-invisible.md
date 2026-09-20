# 1577 — Poll broken: silent no-op + invisible in "Your posts" (S11)

**Severity:** S2
**Status:** Fixed

## Problem

Two related poll bugs:
- **S11a**: A poll with a question + options but no main Content body was silently discarded
  (the empty-Content guard in `PostAsync` returned early).
- **S11b**: A posted poll (with or without a body) never appeared in the profile "Your posts"
  tab or any actor's Posts tab — the content-item filter excluded `Question`.

## Fix

**S11a** — `Compose.razor` `PostAsync`:
- The empty-Content guard now exempts polls: when `ContentType == "Poll"` and `Content` is
  empty, the guard passes if `PollQuestion` is non-empty (the question field). A body-less
  poll with a valid question + options now posts.

**S11b** — `OutboxFilter.IsContentItem`, `HomeTimeline.IsContentItem`, `Home.IsContentItem`:
- Added `Question` to the content-item check alongside `Note`, `Article`, and `Page`.
  Polls now appear in the profile "Your posts" tab, actor Posts tabs, home feed, and public feed.

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- Live-verified (signed-in, fresh browser context):
  - S11a: Compose → Poll → question "S11a body-less poll test?" + 2 options, no body →
    Post → "Posted (HTTP 202)" with IRI.
  - S11b: The poll appears in Profile "Your posts" with its options.
