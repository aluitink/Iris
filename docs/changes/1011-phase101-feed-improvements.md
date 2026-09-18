# Phase 101 — Feed Improvements

## Summary

Improved the home timeline feed and post card rendering:

1. **Boost age display**: The "Boosted by" line now shows when the boost happened (relative time).
2. **Reply context position**: The "In reply to" context card now appears **below** the post body (not above), making the reply content the primary focus.
3. **Removed redundant mentions/hashtags sections**: Mentions and hashtags are already in the post body, so the separate sections were removed.
4. **Fixed post link hover**: The post body link no longer changes text color on hover (the link is still clickable, but the text color stays consistent).
5. **Reply filtering in home feed**: Replies from followed actors are now filtered out of the home timeline. Only top-level posts from followed actors appear. The signed-in actor's own replies are kept. Boosts (Announce) are always kept.

## Changes

### UI (ObjectView.razor + app.css)

- Moved the parent context ("In reply to") from above the content to below it in both the `Create` and `IObject` sections.
- Added CSS classes `object-parent-context--below` and `object-parent--below` with `margin-top` instead of `margin-bottom`.
- Removed the mentions section (`ActivityMentionIris` / `MentionIris` loops).
- Removed the hashtags section (`ActivityHashtagTags` / `HashtagTags` loops).
- Added boost time to the "Boosted by" line with CSS class `object-boost-time`.
- Changed `.object-post-link:hover .object-content` from `color: var(--accent)` to `color: inherit`.

### Server (FeedService.cs)

- Added `IsFollowReply` static method that checks if a feed item is a reply made by a followed actor.
- A reply is a `Create` activity whose content object has a non-public audience (the `to`/`cc` field contains a specific actor IRI, not just the public sentinel).
- In `BuildFeedAsync`, followed actors' outbox items are filtered: replies are excluded, top-level posts and boosts are kept.
- The actor's own posts (including their replies) are always kept.

## Tests

Added 3 new unit tests to `FeedServiceTests`:

1. `Feed_FollowReply_IsFilteredOut` — verifies that a reply from a followed actor is filtered out of the feed.
2. `Feed_OwnReply_IsKept` — verifies that the signed-in actor's own replies are kept in the feed.
3. `Feed_FollowAnnounce_IsKept` — verifies that boosts (Announce) from followed actors are kept in the feed.

Full test suite: **1415 passed / 0 failed** (17 skipped).

## Verification

Live-verified via Playwright:

- Posted a reply to bob's post as alice.
- The reply appears in alice's home timeline with the "In reply to" context **below** the content.
- The mentions are still visible in the post body (e.g., "Mention test 2 @bob").
- No separate mentions/hashtags sections are rendered.
- 0 console errors.

## Decisions

- **Reply detection**: Used the `GetAudienceIris()` method to detect replies. A reply has a non-public audience (the person being replied to), while a top-level post has only the public sentinel. This is the standard ActivityPub convention.
- **Own replies kept**: The signed-in actor's own replies are kept in their home timeline (they can see their own content in full). Only followed actors' replies are filtered.
- **Boosts always kept**: Announce activities are never filtered, regardless of whether the boosted content is a reply or top-level.
