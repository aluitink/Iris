# 117.5 — Feed Content: Filter Replies from Home Timeline

## Summary

Extended the home feed's reply filtering (introduced in 117.1 for followed actors) to
also exclude the signed-in actor's own replies to other actors' content. The home
timeline now shows only top-level content by default; replies are visible on the
parent post's page. The `?depth` parameter opts in to include replies (both own and
followed).

## Context

Phase 117.1 added `IsFollowReply` filtering to `FeedService.BuildFeedAsync`, which
excluded replies from **followed actors' outboxes** when `threadDepth` was null/0.
However, the actor's **own outbox** items were unconditionally included — meaning
the user's own replies to other people's posts still appeared in their home feed,
while followed actors' replies did not. This was inconsistent: a reply is a reply,
regardless of who authored it.

## Changes

### FeedService.cs — `BuildFeedAsync`

- **Own outbox loop** (lines 154-163): Added the same `IsFollowReply` check that
  was already applied to followed actors' items. When `threadDepth is not (> 0)`,
  own replies (detected via `inReplyTo` or the audience heuristic) are skipped.
- **Follow loop comment** updated to reflect the consistent behavior.
- **`threadDepth` XML doc** updated: "replies (from both the actor's own outbox and
  followed actors' outboxes)" instead of just "replies from followed actors".

### FeedServiceTests.cs

- `Feed_OwnReply_IsKept` → **`Feed_OwnReply_FilteredByDefault`**: asserts the
  actor's own reply is filtered out (only the top-level post remains).
- **New `Feed_OwnReply_IncludedWithThreadDepth`**: asserts the reply appears when
  `threadDepth: 1` is passed.
- `Feed_OwnReply_AlwaysKept_RegardlessOfDepth` → **`Feed_OwnAndFollowReplies_FilteredByDefault`**:
  asserts both own and follow replies are filtered (only 2 top-level posts remain).
- **New `Feed_OwnAndFollowReplies_IncludedWithThreadDepth`**: asserts all 4 items
  (2 posts + 2 replies) appear with `threadDepth: 1`.

## Decision

The `?depth` parameter now uniformly controls reply inclusion for **all** content
(own + followed), not just followed actors' content. This is the simplest mental
model: "the feed shows top-level posts; `?depth=1` also shows first-level replies."

## Verification

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test` — 1,143 pass (2 flaky federation tests pass in isolation).
- 49 FeedService tests pass (including 4 updated/new tests).
- Live verification (Playwright on Docker app):
  - **Alice's feed**: her reply to bob ("Testing reply filtering in Phase 101") is
    no longer shown. Her 4 top-level posts + bob's top-level post are shown.
  - **Bob's feed** (previous turn): alice's top-level posts shown, alice's reply to
    bob not shown, bob's own posts shown.
  - No console errors.
