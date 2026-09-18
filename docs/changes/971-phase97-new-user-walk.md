# Phase 97 — Perform a new user walk

**Status:** COMPLETE

## Summary

Performed a Playwright-driven new-user onboarding walkthrough against the live Docker app (`iris.luit.ink`, host port 8088). Simulated a typical first-time user scenario: login, browse the directory, follow users, like posts, compose a new post, boost a post, and reply to a post. All interactions worked correctly with no defects found.

## Walkthrough steps

1. **Login** — Navigated to `http://localhost:8088`, logged in as `andrew` / `Password1`. Redirected to `/home` as expected.
2. **Directory + follow** — Navigated to `/directory`, found `bob` (iris.luit.ink), clicked Follow. Button changed to "Unfollow", confirming the follow edge was created.
3. **Home timeline** — Returned to `/home`. The timeline populated with content from followed actors (including remote posts from the fediverse). A "Load more" button appeared at the bottom for pagination.
4. **Like a post** — Clicked the Like button on andrew's Phase 96 test reply. The button entered `[pressed]` state and the count incremented from 0 to 1.
5. **Boost a post** — Clicked the Boost button on andrew's Phase 96 test post. The button entered `[pressed]` state and the count incremented from 0 to 1.
6. **Compose a new post** — Navigated to `/compose`, typed a new post ("Phase 97 new user walk: exploring Iris for the first time. The onboarding experience feels smooth so far!"), clicked Post. Success: "Posted (HTTP 202)" with the minted Create IRI.
7. **Reply to a post** — Navigated to `/compose?replyTo=...` for the Phase 96 test post. The reply page showed the parent post in a blockquote context. Typed a reply, clicked "Post reply". Success: "Posted (HTTP 202)" with the minted Create IRI.
8. **Object detail (thread view)** — Navigated to the object detail page for the Phase 96 test post. The page showed:
   - The parent post with boost count "1" (pressed) and "1 boost" summary
   - A "Replies" section with 2 replies: the original Phase 96 reply (Like pressed, count 1) and the new Phase 97 reply

## Console errors

All console errors encountered were expected federation behavior, not defects:

- **CORS errors** fetching remote actor profiles from `mas.to`, `mastodon.social` — the browser blocks cross-origin fetches; the app handles this gracefully (shows actor name without avatar). This is inherent to the WASM client architecture and not fixable without a proxy endpoint for actor documents.
- **403 from `/ap/v1/proxy/https://ursal.zone/users/gustavo`** — the remote instance is blocking the proxy request. Not a defect in Iris.

## Findings

**No defects found.** All onboarding interactions (login, follow, like, boost, compose, reply) work correctly. The UI is responsive, state updates are immediate, and the thread view correctly shows the full conversation.

## Verification evidence

- Like: button `[pressed]`, count 0→1
- Boost: button `[pressed]`, count 0→1
- Compose: "Posted (HTTP 202)", minted IRI displayed
- Reply: "Posted (HTTP 202)", minted IRI displayed, reply visible in thread
- Thread view: parent + 2 replies rendered, engagement counts correct
- 0 unexpected console errors

## New coded tests

None. This was a manual Playwright verification pass (WASM manual-test policy).
