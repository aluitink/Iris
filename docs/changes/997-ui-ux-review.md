# 997: General UI/UX Review

## Pass 6 (2026-09-19)

Post-147.2-follow-up verification pass. Reviewed all 8 signed-in routes: Home, Compose, Profile, Settings, Communities, Search, Notifications, Directory, plus a community-detail feed (the code path changed by the 147.2 parallel fan-out). All pages load and render correctly. Home timeline renders boosted posts with like/boost counts and "Load more" pagination. Communities list and detail (Feed tab) render; the community feed resolved correctly to an empty state for a memberless community. Console: 1 transient `net::ERR_NETWORK_CHANGED` on the first community-feed fetch (resolved cleanly on retry — a network blip, not a code defect). No new defects. The 147.2 parallel fan-out does not regress the UI.

## Pass 5 (2026-09-19)

Post-139.2-s5a verification pass. Reviewed 6 signed-in routes: Home, Notifications, Search, Communities, Profile. All pages load correctly with 0 console errors. Home timeline renders correctly with boosted posts, like/boost counts, and "Load more" pagination. The object-document visibility gate (139.2-s5a) does not affect the UI — the WASM client signs as the session actor, so the user's own posts and DMs load normally. No new defects.

## Pass 4 (2026-09-19)

Post-139.2-s5c verification pass. Reviewed all 8 signed-in routes: Home, Compose, Profile, Settings, Communities, Search, Notifications, Directory. All pages load correctly with 0 console errors. Home timeline renders 33 items with "Load more" pagination. Navigation bar shows all expected links (Home, New post, Notifications 99+, Directory, Communities, Profile, Settings, Search, Log out). No new defects. The follow-feed owner gate (139.2-s5c) does not affect the UI — the WASM client signs as the session actor, so the owner's feed loads normally.

## Pass 3 (2026-09-19)

Post-139.4 verification pass. Reviewed Home, Settings, Compose after the design token migration (~30 hardcoded CSS values migrated to tokens) and the 404 page error overlay fix. All pages consistent and functional. 0 new defects. Console: 1 known cosmetic 404 proxy error (haunted.computer). Dark theme, spacing, and typography all render correctly with the new design tokens.

## Pass 2 (2026-09-19)

Reviewed Home, Notifications, Profile (own + remote), Compose (end-to-end post), Communities (list + detail). All pages consistent and functional. 0 new defects. Console: 2 errors (known cosmetic 404 proxy noise for deleted remote posts). Compose end-to-end post verified (HTTP 202). Recurring item stays at top of Up Next.

## Pass 1

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
- Tests: 1,346 passed, 0 failed, 25 skipped (unchanged)
- Live verification (Pass 6): All 8 signed-in routes + community-detail feed reviewed via MCP Playwright as andrew:Password1; 0 new defects; 1 transient `ERR_NETWORK_CHANGED` (network blip, not a code defect) on first community-feed fetch, resolved on retry.
- Live verification (Pass 3): Home, Settings, Compose reviewed via MCP Playwright as andrew:Password1; 0 new defects; design tokens render correctly; 1 known cosmetic 404 proxy error.
- Live verification (Pass 2): All pages reviewed via MCP Playwright as andrew:Password1; Compose end-to-end post returned HTTP 202; 0 new defects. Console: 2 known cosmetic 404 proxy errors.
- Live verification (Pass 1): All pages reviewed via MCP Playwright as andrew:Password1; Compose end-to-end post returned HTTP 202; Community detail feed 0 console errors/warnings.
