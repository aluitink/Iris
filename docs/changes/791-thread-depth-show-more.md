# Phase 79 — Thread depth & interop polish

## 79.1 — Thread depth: "show more" for deep threads

**Problem:** ObjectDetail's replies section was capped at 20 replies with no way to load more. Deep threads (common on Lemmy, Mastodon) would only show the first 20 replies.

**Solution:** Added a "Show more replies" button that loads 20 additional replies per click.

**Changes:**
- `ObjectDetail.razor`:
  - Added `RepliesLoaded`, `HasMoreReplies`, `LoadingMoreReplies`, `RepliesObjectIri` state fields
  - `LoadRepliesAsync` now tracks the loaded count and sets `HasMoreReplies = true` when 20+ replies are loaded
  - New `LoadMoreRepliesAsync` method fetches replies with a higher limit (`RepliesLoaded + 20`) and replaces the list with the full set
  - Replies section markup now conditionally renders the "Show more replies" button when `HasMoreReplies` is true
- `app.css`: Added `.replies-more` class (centered, top padding)

**Verification:**
- `dotnet build` clean (0 warn/0 err)
- `dotnet test` green (1666 passed, 0 failed, 17 skipped)
- Live verification via Playwright deferred (requires a thread with 20+ replies; the local test instance has limited content)

**Note:** The implementation re-fetches all replies (up to the new limit) rather than fetching only the next page. This is acceptable for typical thread sizes (20-100 replies) and avoids modifying the client API to support offset-based pagination.
