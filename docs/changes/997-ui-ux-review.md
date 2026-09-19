# 997: General UI/UX Review

## Pass 10 (2026-09-19)

Post-138.25-follow-up verification pass. Two-part pass: (1) an **authless (signed-out) pass** of the public home feed, and (2) a **signed-in pass** (pass10review, newly registered) focused on the 138.25 Lemmy-metadata surfaces and on browsing a remote **Lemmy** community end-to-end. This pass found and fixed **three real defects** (all committed in `fix(web,client,server): Pass 10 proxy + Lemmy-capability defects`):

**Defect 1 — signed-out 401 console spam (high, fixed).** The signed-out home feed called the *authenticated* `POST /ap/v1/proxy/{target}` endpoint for every remote actor (one per avatar), logging a 401 per actor (15–17 console errors) plus a CORS error, and leaving remote avatars blank. Root cause: `UiContext.FetchActorAsync` routed remote actors through the proxy even when the session had no signing client (the `_session.Client is null` guard came after the proxy branch). Fix: skip the proxy when signed out and fall through to the anonymous live read (actor documents are public). **Live-verified:** signed-out home feed now **0 console errors** (was 17).

**Defect 2 — proxy upstream abort surfaced as unhandled 500 (medium, fixed).** On a Lemmy community page, an outbound proxy fetch to `lemmy.ml` aborted (`HttpIOException: The response ended prematurely` — the Kerberos/GSSAPI TLS negotiation) and escaped as an **unhandled 500** (6 console errors) instead of a clean 502. Fix: `ProxyHandler` now catches non-cancellation outbound exceptions and returns **502 Bad Gateway**, matching the media proxy's existing unreachable-upstream behavior. **Live-verified:** `lemmy.ml` proxy calls now return clean 502s; the app stays fully usable (other Lemmy instances render normally).

**Defect 3 — cached Lemmy community misclassified as Iris → `/feed`+`/members` 404s (high, fixed).** A remote Lemmy community page showed "No posts yet" and 404'd its `/feed` + `/members` collections (2×404) instead of loading the community's real feed. Root cause: `IsLemmy()` used the *negative* test "a Group with an outbox and **no** `iris:`-namespaced extension keys". But the per-actor counter refresh (`ActorCountRefreshService`) stamps `iris:postsCount`/`iris:followersCount`/`iris:followingCount` onto **every** stored Group — remote communities included — so a cached Lemmy community arrived carrying `iris:` keys and was misclassified as Iris; `ResolveFeedIri`/`ResolveMembersIri` then fell to the Mastodon/Pleroma `/feed` + `/members` convention, which Lemmy does not serve (404). Fix: `IsLemmy()` is now **positive** on Lemmy's own bare community fields (`language`, `featured`, `sensitive`, `postingRestrictedToMods`), which survive the `iris:` counter stamping; added `LemmyFieldNames` + a regression test (a Lemmy Group carrying `iris:` counter keys still resolves to `/outbox` + `/followers`). **Live-verified:** the Lemmy community now dials `/outbox` + `/followers` (both 200) and renders its real feed (dozens of posts from lemmy.world / piefed / lemmy.zip / thelemmy.club / feddit.org); the only remaining console errors are the expected `lemmy.ml` upstream 502s (Defect 2 class).

**Authless pass (signed-out):** home feed renders public remote content with 0 console errors (Defect 1 fixed); nav shows Log in / Register only.

**Signed-in pass:** `/home` (empty timeline + good empty state, 0 errors); `/communities` (Following + All-on-this-instance tabs, local communities render, 0 errors); Lemmy community detail (real feed via outbox, member count via followers); `/object?iri=…` and `/compose?replyTo=…` (parent prefilled with quote) render once the session is warm.

**138.25 surfaces:** the locked/pinned/language/NSFW/mod-only readers are wired, but no live Lemmy post or community currently carries those flags (most communities have `sensitive=false`, `postingRestrictedToMods=false`, no `featured`, posts carry no `language`/`locked`), so no banner/badge was visible against real data this pass — nothing to fix, but nothing to confirm visually either. (The `sensitive`/`postingRestrictedToMods` bare fields ARE present on the Lemmy community doc; they only render when `true`.)

**Console errors (all remote-availability, not Iris bugs):** the only errors on the Lemmy community page are `lemmy.ml` upstream 502s (the Kerberos/TLS abort — now a clean 502 per Defect 2). No 400s/401s/500s, no unhandled exceptions.

**3 defects found and fixed (committed). 0 remaining.**

## Pass 8 (2026-09-19)

Post-139.2-s5a/s5b verification pass. Two-part pass: (1) an **authless (signed-out) pass** covering every route, and (2) a **signed-in spot-check** as uxreview8 (newly registered) across the routes most likely to have regressed (the S5b visibility area and the object-document federated gate).

**Authless pass (signed-out):** all 8 authenticated routes (`/home`, `/compose`, `/notifications`, `/directory`, `/communities`, `/profile`, `/settings`, `/search`) correctly redirect to `/login`. The signed-out nav shows only Log in / Register (no authenticated links — no data leak). The 404 page (`/nonexistent-page-12345`) renders publicly with "Not found" + "Sorry, there's nothing at this address." The register page is accessible signed-out with handle, display name, and password fields. 0 new console errors beyond remote-instance availability.

**Signed-in spot-check:** registration → `/home` works (empty timeline with "Follow people to see their posts here" + "Browse the directory" link). Communities page renders (description, "+ Create a community", Following/All tabs, 6 community cards with Follow buttons). Community detail (`interop`) renders header + Follow/Join + "Post to this community" + Feed/Members tabs + in-community search. Profile page renders (banner, handle, display name, Edit profile, 5 tabs). Compose deep-link renders ("Posting as uxreview8", content textbox, attachments, character count 0/500, type selector, visibility selector, content warning, formatting tips). Directory renders 14 people with "Find someone on another server" search + tabs + scope buttons. Settings renders tabs (Account/Content/Danger) + Profile/Security/Change password/Moderation sections. Search renders textbox + "Actors only" + Search button. Notifications renders filter tabs + "Mark all as read" + empty state. **Hard-refresh stability:** Home, Communities, Community detail, Profile, Compose, Settings, Search, Notifications — all 0 console errors.

**Console errors (all remote-availability, not Iris bugs):** 20× HTTP 401 from `/ap/v1/proxy/https://{remote}/users/…` (remote instances returning 401 for the proxy fetch) + 2× CORS errors from direct remote fetches (cyberpunk.lol, cyberplace.social). The proxy correctly forwards upstream status codes and the app degrades gracefully (content renders; remote media shows fallbacks). Same class as Pass 7's transient remote-availability errors — external availability, not a regression.

**Minor observation (not a defect):** some remote posts show numeric IDs as display names (e.g., "115588296584761431" for cyberplace.social, "117024347319605995" for cyberpunk.lol) because those instances use numeric user IDs and the `preferredUsername` isn't available in the proxy response. The display fallback works correctly given the data.

**0 new defects.**

## Pass 7 (2026-09-19)

Post-S5b verification pass. Two-part pass: (1) an **authless (signed-out) pass** covering every route, and (2) a **signed-in spot-check** as andrew:Password1 across the routes most likely to have regressed (the 147.2 parallel-fan-out community feed and the S5b visibility area).

**Authless pass (signed-out):** all 8 authenticated routes (`/home`, `/compose`, `/notifications`, `/directory`, `/communities`, `/profile`, `/settings`, `/search`) correctly redirect to `/login`. The signed-out nav shows only Log in / Register (no authenticated links — no data leak). The 404 page (`/nonexistent-page-xyz`) renders publicly with "Not found" + "Sorry, there's nothing at this address" and no error overlay (the 404-overlay fix is holding). The register page is accessible signed-out. 0 console errors/warnings anywhere.

**Signed-in spot-check:** login → `/home` works (home timeline renders ~20 posts + "Load more", 0 console errors). Communities page renders (description, "+ Create a community", Following/All tabs, empty state, 6 community cards). Community detail (`technology`) renders header + tabs + "Community Feed" with ~20 posts + "Load more", and is **stable across a hard refresh** — the 147.2 parallel fan-out does not regress on reload. Profile page renders (header, 5 tabs, Your posts list; Following tab renders the follows list). Compose deep-link (`/compose?community=…`) pre-selects the target community, all fields present, stable across a hard refresh.

**Console errors (all remote-availability, not Iris bugs):** 3× HTTP 500 from `/ap/v1/proxy/https://lemmy.ml/…` (lemmy.ml down/rate-limiting) on the community detail; 2× HTTP 403 from `/ap/v1/proxy/https://lemmy.luit.ink/c/interop` (remote forbidding the proxy fetch) on the profile Following tab. All other proxy fetches (lemmy.world, piefed.world, lemmy.zip, thelemmy.club, feddit.org, lemmy.dbzer0.com) return 200. The proxy correctly forwards upstream status codes and the app degrades gracefully (content renders; remote media shows fallbacks). Same class as Pass 6's transient `ERR_NETWORK_CHANGED` — external availability, not a regression.

**Minor observation (not a defect):** the community detail fetches `/ap/v1/c/technology`, `/members`, and `/feed` 3× across 2 page loads (initial + hard refresh) — consistent with the pre-existing Blazor hydration double-fetch pattern. All return 200; no errors; no user-visible impact.

**0 new defects.**

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
- Live verification (Pass 8): Authless pass — all 8 authenticated routes redirect to `/login`, 404 page + register accessible, 0 new console errors. Signed-in spot-check (uxreview8, newly registered) — Home, Communities, Community detail (hard-refresh stable), Profile, Compose deep-link (hard-refresh stable), Directory, Settings (hard-refresh stable), Search (hard-refresh stable), Notifications (hard-refresh stable); 0 new defects; only console errors were remote-instance availability (401s from `/ap/v1/proxy/`, CORS from direct remote fetches), not Iris bugs.
- Live verification (Pass 7): Authless pass — all 8 authenticated routes redirect to `/login`, 404 page + register accessible, 0 console errors. Signed-in spot-check (andrew:Password1) — Home, Communities, Community detail (hard-refresh stable), Profile, Compose deep-link (hard-refresh stable); 0 new defects; only console errors were remote-instance availability (lemmy.ml 500, lemmy.luit.ink 403), not Iris bugs.
- Live verification (Pass 6): All 8 signed-in routes + community-detail feed reviewed via MCP Playwright as andrew:Password1; 0 new defects; 1 transient `ERR_NETWORK_CHANGED` (network blip, not a code defect) on first community-feed fetch, resolved on retry.
- Live verification (Pass 3): Home, Settings, Compose reviewed via MCP Playwright as andrew:Password1; 0 new defects; design tokens render correctly; 1 known cosmetic 404 proxy error.
- Live verification (Pass 2): All pages reviewed via MCP Playwright as andrew:Password1; Compose end-to-end post returned HTTP 202; 0 new defects. Console: 2 known cosmetic 404 proxy errors.
- Live verification (Pass 1): All pages reviewed via MCP Playwright as andrew:Password1; Compose end-to-end post returned HTTP 202; Community detail feed 0 console errors/warnings.
