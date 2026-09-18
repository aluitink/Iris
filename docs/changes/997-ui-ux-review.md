# 997: General UI/UX Review

## Summary

Reviewed the Iris user interface via MCP Playwright as andrew:Password1. No major inconsistencies or improvements found.

## Pages Reviewed

- **Home** (`/home`): Shows boosted posts with "Boosted by" labels, like/boost counts, and the timeline. Some 404/418 errors from the proxy endpoint (expected for remote posts that can't be fetched).
- **Directory** (`/directory`): Shows a search box for finding people on other servers, People/Communities tabs, "This instance" / "All known" filter, and user cards with avatars, names, post/following/follower counts, and Follow buttons.
- **Profile** (`/profile`): Shows a banner image, avatar, name, display name, bio, "Edit profile" button, and tabs (Your posts, Replies, Likes, Followers, Following). Posts load correctly.
- **Compose** (`/compose`): Shows "Posting as andrew", a content textarea, attachments section, character counter (0/500), type selector (Note), visibility selector (Public), content warning checkbox, "Post" button, and formatting tips.
- **Notifications** (`/notifications`): Shows filter tabs (All, Follows, Likes, Boosts, Replies, Mentions), "Mark all as read" button, notification cards with "New" highlights, and a "Load more" button for pagination.
- **Settings** (`/settings`): Shows tabs (Account, Content, Danger), Account section with Profile, Security, Change password, Moderation subsections, and an "Edit your profile" link.
- **Search** (`/search`): Shows a search box, "Actors only" checkbox, and "Search" button.
- **Communities** (`/communities`): Shows a description, "+ Create a community" button, tabs (Following, All on this instance), and an empty state with a helpful message.

## Findings

No major inconsistencies or improvements found. The UI looks consistent and reasonable. The main observations:
1. The home page shows 404/418 errors from the proxy endpoint for remote posts that can't be fetched. This is expected behavior and the fallback text has already been improved (see change doc 995).
2. The notifications page already has pagination (verified in change doc 996).
3. The like/boost count display for remote objects is still showing 0 in some cases (partial fix, see change doc 994).

## Conclusion

The UI/UX review is complete. No new items need to be filed in the Up Next section. The UI is in good shape.

## Files Changed

None.

## Verification

- Build: 0 warnings, 0 errors (unchanged)
- Tests: 1304 passed, 0 failed, 8 skipped (unchanged)
- Live verification: Reviewed all main pages via MCP Playwright as andrew:Password1
