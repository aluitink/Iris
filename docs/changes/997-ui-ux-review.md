# 997: General UI/UX Review

## Summary

Reviewed the Iris user interface via MCP Playwright as andrew:Password1. This is a recurring review; this pass covers Home, Notifications, Profile (own + remote), Compose (end-to-end post), and Communities (list + detail). All pages are consistent and functional. No defects found; two minor observations logged.

## Pages Reviewed

- **Home** (`/home`): Boosted posts with "Boosted by" labels, like/boost counts, timeline. Remote-post proxy 404/418 console noise degrades gracefully to a "Content unavailable — view original post" link (not broken images).
- **Notifications** (`/notifications`): Filter tabs (All/Follows/Likes/Boosts/Replies/Mentions), "Mark all as read", notification cards with "New" highlights, "Load more" pagination. Working.
- **Profile** (`/profile`, own + remote Gargron): Banner, avatar, name, display name, bio, "Edit profile" (own only), tabs (Your posts/Replies/Likes/Followers/Following). Posts load. Remote actor renders read-only.
- **Compose** (`/compose`): "Posting as andrew", content textarea, attachments picker, character counter, type selector (Note/Article/Poll), visibility selector (Public/Followers/Direct), content-warning toggle, Post button, formatting tips. **End-to-end post verified**: typed a note, clicked Post, got `Posted (HTTP 202)` with the minted ActivityPub IRI and a "View your posts →" link. The counter resets to 0/500 after a successful post (correct — `Content` is cleared on success).
- **Communities** (`/communities`): Description, "+ Create a community" button, Following / All-on-this-instance tabs. Following shows a helpful empty state. All-on-this-instance lists community cards (banner, avatar, handle, display name, "Community" badge, description, posts/following/followers stats, Follow button) — all render, no broken images.
- **Community detail** (`/community?iri=…`, technology): Header card (banner, avatar, handle, "Community" badge, "Edit community"), "Post to this community" link, tabs (Feed/Members/Owners/Peers/Requests), in-community search box, "Community Feed" with Refresh, a long list of boosted remote posts (mixed Mastodon-style Like/Boost and Lemmy-style Upvote/Downvote/Score engagement), "Load more". 0 console errors, 0 warnings.

## Findings

No defects. Two minor observations (no fix warranted this pass):

1. **Following-list actor names are plain text, not clickable links.** On the Profile "Following" tab, followed-actor names render as text rather than links to `/actor?iri=…`. Minor UX gap — a user must go to the Directory or search to reach a followed actor. Low priority; not a defect.
2. **Cosmetic console noise on unavailable boosted objects.** Remote posts whose content can't be fetched produce 404/418 proxy console errors. The UI degrades gracefully (link-out placeholder), so this is not a user-facing defect — only console noise.

## Conclusion

The UI/UX review is complete for this pass. All major pages (Home, Notifications, Profile, Compose, Communities, Community detail) are consistent and functional. No new defects to file. The recurring item stays at the top of Up Next for future refinement passes.

## Files Changed

None.

## Verification

- Build: 0 warnings, 0 errors (unchanged)
- Tests: 2,401 passed, 0 failed (unchanged)
- Live verification: All pages reviewed via MCP Playwright as andrew:Password1; Compose end-to-end post returned HTTP 202; Community detail feed 0 console errors/warnings.
