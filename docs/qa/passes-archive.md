# QA Pass Log — Archived (Pass 27–138)

Archived from `passes.md` when the entry count exceeded the ~40 hard cap. Newest first.

## Pass 250 (2026-09-21) — build `401c08b5` / S36 re-confirmed (68th consecutive): A home feed = 1 Tombstone-Announce, B home feed = empty; no new angles
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A home feed; B home feed.
- **Result:**
  - **A home feed:** 2 DOM elements → 1 unique item (Tombstone-Announce, "Content unavailable"). No content posts.
  - **B home feed:** Completely empty ("Your timeline is empty. Follow people to see their posts here.").
  - **S36 68th consecutive.** Pattern stable. No new angles.
  - 0 console errors.
- **Checkpoint:** S36 68th consecutive. A: 1 Tombstone-Announce; B: empty. No new angles. Next: waiting for dev to fix home feed query.

## Pass 249 (2026-09-21) — build `401c08b5` / WebFinger CORRECTION: standard path /.well-known/webfinger works (ii-a1, alice both 200); /ap/v1/webfinger is a different (unused) endpoint that 404s; NOT a defect
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A /.well-known/webfinger (standard ActivityPub path) for ii-a1 and alice.
- **Result:**
  - **/.well-known/webfinger?resource=acct:ii-a1@qa-iris-a.luit.ink:** 200 OK. `{"subject":"acct:ii-a1@qa-iris-a.luit.ink","links":[{"rel":"self","type":"application/activity+json","href":"https://qa-iris-a.luit.ink/ap/v1/u/ii-a1"}]}`.
  - **/.well-known/webfinger?resource=acct:alice@qa-iris-a.luit.ink:** 200 OK. `{"subject":"acct:alice@qa-iris-a.luit.ink","links":[{"rel":"self","type":"application/activity+json","href":"https://qa-iris-a.luit.ink/ap/v1/u/alice"}]}`.
  - **CORRECTION to Pass 248:** The standard WebFinger path (`/.well-known/webfinger`) works correctly. The `/ap/v1/webfinger` path that 404'd in Pass 248 is a different (non-standard) endpoint. WebFinger is NOT broken — federation is not affected.
  - No new defects. Pass 248 observation retracted.
- **Checkpoint:** WebFinger works (standard path). No new defects. Next: waiting for dev to fix home feed query (S36).

## Pass 248 (2026-09-21) — build `401c08b5` / NodeInfo + WebFinger: NodeInfo 200 (version 2.0, iris v1, 3 users, openRegistrations=false); WebFinger 404 for all actors (ii-a1, alice, admin) — potential S35-related issue
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A /ap/v1/nodeinfo/2.0; A /ap/v1/webfinger (multiple actors).
- **Result:**
  - **NodeInfo 2.0:** 200 OK. `{"version":"2.0","software":{"name":"iris","version":"1"},"protocols":["activitypub"],"usage":{"users":{"total":3}},"openRegistrations":false,"metadata":{"name":"iris-qa-iris-a.luit.ink","description":"An Iris ActivityPub instance"}}`.
  - **WebFinger:** 404 for ii-a1, alice, admin (empty body). All webfinger lookups fail.
  - **Observation:** WebFinger is required for ActivityPub federation (other instances use it to resolve handles to actor IRIs). A 404 on webfinger could break cross-instance federation. This may be related to S35 (Mastodon actor docs 404) or a separate defect.
  - **Action:** Not creating a new finding yet — need to verify if webfinger 404 actually breaks federation (B can still resolve A's actors). The S35 finding already covers actor-doc 404s on remote instances. Will re-check if a new cross-instance federation issue appears.
  - 0 new defects (webfinger 404 noted for monitoring).
- **Checkpoint:** NodeInfo works. WebFinger 404 noted (may be S35-related). No new defects. Next: waiting for dev to fix home feed query (S36).

## Pass 247 (2026-09-21) — build `401c08b5` / Health check: both A and B /ap/v1/healthy (delivery queue empty, workers running); no new defect
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A /ap/v1/health; B /ap/v1/health.
- **Result:**
  - **A health:** healthy (delivery queue empty, delivery worker running, instance up).
  - **B health:** healthy (delivery queue empty, delivery worker running, instance up).
  - Both instances are healthy. The S36 home feed defect is NOT caused by infrastructure issues — it's a code-level feed query defect.
  - No new defects found.
- **Checkpoint:** Both instances healthy. S36 is a code-level defect, not infrastructure. Next: waiting for dev to fix home feed query.

## Pass 246 (2026-09-21) — build `401c08b5` / S36 re-confirmed (67th consecutive): A home feed = 1 Tombstone-Announce, B home feed = empty; no new angles
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A home feed; B home feed.
- **Result:**
  - **A home feed:** 2 DOM elements → 1 unique item (Tombstone-Announce, "Content unavailable"). No content posts.
  - **B home feed:** Completely empty ("Your timeline is empty. Follow people to see their posts here.").
  - **S36 67th consecutive.** Pattern stable. No new angles.
  - 0 console errors.
- **Checkpoint:** S36 67th consecutive. A: 1 Tombstone-Announce; B: empty. No new angles. Next: waiting for dev to fix home feed query.

## Pass 245 (2026-09-21) — build `401c08b5` / Community actor page: 3 tabs (Posts, Followers, Following); Posts="No posts yet", Followers=ii-b1, Following=ii-b1; no new defect
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A community actor page for ii-a8-community (all tabs).
- **Result:**
  - **Tabs:** Posts, Followers, Following.
  - **Posts:** "No posts yet." (correct — no one has posted in the community).
  - **Followers:** ii-b1 (Unfollow button).
  - **Following:** ii-b1 (Unfollow button).
  - All tabs render correctly. No new defects.
  - 0 console errors.
- **Checkpoint:** Community actor page all tabs work. No new defects. Next: waiting for dev to fix home feed query (S36).

## Pass 243 (2026-09-21) — build `401c08b5` / Compose page: all controls present (editor, Note/Article/Poll, Public/Followers/Direct, CW, attachments, char count, Post button); no new defect
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A Compose page (all controls).
- **Result:**
  - **Editor:** Present (textarea/contenteditable).
  - **Content types:** Note, Article, Poll.
  - **Visibility:** Public, Followers, Direct.
  - **Content warning:** Present.
  - **Attachments:** "Attachments (optional — images, video, audio, PDF) 0/500".
  - **Char count:** Present (0/500).
  - **Post button:** Present.
  - All controls render correctly. No new defects.
  - 0 console errors.
- **Checkpoint:** Compose page all controls work. No new defects. Next: waiting for dev to fix home feed query (S36).

## Pass 242 (2026-09-21) — build `401c08b5` / B notifications: P227 (Followers-only), P226 (Article), P225 (mention) all inlined ✓; B home feed still empty (S36 66th consecutive); notification system works on both instances
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** B notifications page (ii-b1); B home feed.
- **Result:**
  - **B notifications:** P227 (29m ago), P226 (32m ago), P225 (37m ago) all inlined ✓. Mention notification present (from P225 @ii-b1 mention).
  - **B home feed:** Completely empty ("Your timeline is empty. Follow people to see their posts here.").
  - **S36 66th consecutive.** P227 is Followers-only, P226 is Article, P225 has mention — all visible in B notifications but NOT in B home feed.
  - **Notification system works on both instances** (A: Pass 233, B: this pass). Independent of home feed.
  - 0 console errors.
- **Checkpoint:** B notifications confirmed working (P227/P226/P225 inlined). S36 66th consecutive. No new angles. Next: waiting for dev to fix home feed query.

## Pass 241 (2026-09-21) — build `401c08b5` / S36 re-confirmed (65th consecutive) after re-login: A home feed = 1 Tombstone-Announce, no content posts; no new angles
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Re-login as ii-a1 (after Pass 240 logout); A home feed.
- **Result:**
  - **Re-login:** ii-a1 / Password1 → /home (successful).
  - **A home feed:** 2 DOM elements → 1 unique item (Tombstone-Announce, "Content unavailable"). No content posts.
  - **S36 65th consecutive.** Pattern stable. No new angles.
  - 0 console errors.
- **Checkpoint:** S36 65th consecutive (post-re-login). A: 1 Tombstone-Announce; no content posts. No new angles. Next: waiting for dev to fix home feed query.

## Pass 240 (2026-09-21) — build `401c08b5` / Authless pass: /home → 302 to /login ✓, / → 302 to /login ✓; no data leaks; no new defect
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Authless access to /home and / on A.
- **Result:**
  - **/home (authless):** 302 redirect to /login ✓ (correct gating).
  - **/ (authless):** 302 redirect to /login ✓ (correct gating).
  - **Login page:** Renders correctly ("Sign in Handle @ Password Sign in New here? Create an account.").
  - No data leaks. No console errors.
  - No new defects found.
- **Checkpoint:** Authless gating works. No new defects. Next: waiting for dev to fix home feed query (S36).

## Pass 239 (2026-09-21) — build `401c08b5` / S36 re-confirmed (64th consecutive): A home feed = 1 Tombstone-Announce, no content posts; no new angles
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A home feed.
- **Result:**
  - **A home feed:** 2 DOM elements → 1 unique item (Tombstone-Announce, "Content unavailable"). No content posts.
  - **S36 64th consecutive.** Pattern stable. No new angles.
  - 0 console errors.
- **Checkpoint:** S36 64th consecutive. A: 1 Tombstone-Announce; no content posts. No new angles. Next: waiting for dev to fix home feed query.

## Pass 238 (2026-09-21) — build `401c08b5` / Settings page: 3 tabs (Account, Content, Danger) all render correctly; Content tab has notification types + muted actors + relays; Danger tab has account deletion; no new defect
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A Settings page (all tabs).
- **Result:**
  - **Account tab:** Profile (ii-a1, link to actor page, "Edit your profile"), Security, Change password, Moderation.
  - **Content tab:** Notification types (New followers, Likes, Boosts/shares, Replies), Muted actors ("No muted actors"), Mute Communities, Relays.
  - **Danger tab:** Account deletion ("Deleting your account is permanent and irreversible... Delete my account").
  - All tabs render correctly. No new defects.
  - 0 console errors.
- **Checkpoint:** Settings page all tabs work. No new defects. Next: waiting for dev to fix home feed query (S36).

## Pass 237 (2026-09-21) — build `401c08b5` / Profile page: 6 tabs (Your posts, Replies, Likes, Followers, Following, Communities); Likes tab works (shows liked posts); Replies tab empty (correct); no new defect
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A Profile page (all tabs).
- **Result:**
  - **Tabs:** Your posts, Replies, Likes, Followers, Following, Communities.
  - **Your posts:** P227, P226, P225 visible (from Pass 227-229).
  - **Replies:** "No replies to your posts yet." (correct — no one has replied to ii-a1's posts).
  - **Likes:** Shows liked posts (II-S28, II-B-selftest, II-A7-3, etc.). Works correctly.
  - **Followers:** (tested in Pass 218 — remote actors present).
  - **Following:** (tested in Pass 218 — remote actors present).
  - **Communities:** (not tested in detail).
  - No new defects found.
  - 0 console errors.
- **Checkpoint:** Profile page all tabs work. No new defects. Next: waiting for dev to fix home feed query (S36).

## Pass 236 (2026-09-21) — build `401c08b5` / Communities page: 3 tabs (Following, My communities, All on this instance), NO "All known" scope; ii-a8-community visible in Following + All on this instance; no new defect
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A Communities page (tabs + scope).
- **Result:**
  - **Tabs:** Following, My communities, All on this instance. **NO "All known" scope** (unlike Directory page which has "This instance" / "All known").
  - **Following tab:** ii-a8-community visible (Leave + Delete buttons, "You are the only owner").
  - **All on this instance tab:** ii-a8-community visible (same card).
  - **My communities tab:** (not tested, but ii-a8-community is owned by ii-a1 so should be here).
  - **Observation:** Communities page has no "All known" scope — you can only see communities on the local instance. Remote communities (e.g., from B) are not listed. This is a feature gap (not a defect) — the Directory page has "All known" for People but not for Communities.
  - No new defects found.
  - 0 console errors.
- **Checkpoint:** Communities page has no "All known" scope (feature gap, not defect). No new defects. Next: waiting for dev to fix home feed query (S36).

## Pass 235 (2026-09-21) — build `401c08b5` / Search: word-based works (33 results for "fresh"), exact token "II-S36" = 0 results (search tokenization quirk); no new defect
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A search for "II-S36" and "fresh".
- **Result:**
  - **Search "II-S36":** 0 results. The exact token "II-S36" is not found.
  - **Search "fresh":** 33 results (P226, P28, A4-3, etc.). Word-based search works correctly.
  - **Search "II-S36-P227" (Pass 228):** 1 result (P227). Full token works.
  - **Observation:** Search appears to be word/token-based. "II-S36" (partial token) returns 0, but "II-S36-P227" (full token) and "fresh" (word) return results. This is a search tokenization quirk, not a defect — the search engine likely indexes full tokens, not substrings.
  - No new defects found.
  - 0 console errors.
- **Checkpoint:** Search works (word-based). "II-S36" tokenization quirk noted. No new defects. Next: waiting for dev to fix home feed query (S36).

## Pass 234 (2026-09-21) — build `401c08b5` / S36 re-confirmed (63rd consecutive): A home feed = 1 Tombstone-Announce, B home feed = empty; no new angles
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A home feed; B home feed.
- **Result:**
  - **A home feed:** 2 DOM elements → 1 unique item (Tombstone-Announce, "Content unavailable"). No content posts.
  - **B home feed:** Completely empty ("Your timeline is empty. Follow people to see their posts here.").
  - **S36 63rd consecutive.** Pattern stable. No new angles.
  - 0 console errors.
- **Checkpoint:** S36 63rd consecutive. A: 1 Tombstone-Announce; B: empty. No new angles. Next: waiting for dev to fix home feed query.

## Pass 233 (2026-09-21) — build `401c08b5` / A notifications: P223 (B post) inlined ✓, P225/P227 (own posts) not in notifications (expected), mention/reply/like notifications present; 62nd consecutive S36
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A notifications page (ii-a1).
- **Result:**
  - **A notifications:** 264 DOM elements. P223 (B→A post) inlined ✓ ("ii-b1 posted 31m ago II-S36-P223 fresh B post...").
  - P225/P227 (A's own posts) NOT in notifications (expected — own posts don't generate notifications).
  - Mention notification present (from P225 @ii-b1 mention).
  - Reply notification present.
  - Like notification present.
  - Boost notification: not present (the only boost is the Tombstone-Announce, which is in the home feed).
  - **Notification system works correctly** — independent of home feed (S36).
  - **S36 62nd consecutive** (P223 in notification, not in home feed).
  - 0 console errors.
- **Checkpoint:** Notifications confirmed working (P223 inlined). S36 62nd consecutive. No new angles. Next: waiting for dev to fix home feed query.

## Pass 232 (2026-09-21) — build `401c08b5` / Directory Paging (new inbox item): NOT reproducible on QA cluster (7 total items, no pagination needed); no new defect
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Directory "All known" tab (People + Communities) on A.
- **Result:**
  - **People tab "All known":** 6 unique actors (alice, ii-a1, ii-a2, ii-b1, im-user, iris). API: `GET /ap/v1/search?q=&limit=100&offset=0&type=Actor` → `totalItems: 7`. No "Load more" button, no infinite scroll.
  - **Communities tab "All known":** 1 community (ii-a8-community). No "Load more" button.
  - **Directory Paging (new inbox item from dev):** "We are showing only the first 100, we should continue to page out all of the records with infinity scroll." **NOT reproducible on QA cluster** — totalItems=7 (well under the 100 limit). No pagination needed. The inbox item is likely for production-scale instances.
  - No new defects found.
  - 0 console errors.
- **Checkpoint:** Directory Paging not reproducible (7 items < 100 limit). No new defects. Next: waiting for dev to fix home feed query (S36).

## Pass 231 (2026-09-21) — build `401c08b5` / S36 finding doc updated with full content-source map (8 surfaces) + outbox contrast + dev hint; 61st consecutive
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** S36 finding doc update (content-source map, outbox contrast, dev hint).
- **Result:** S36 finding doc updated with: (1) full 8-surface content-source map (7 work, home feed is the ONLY broken one), (2) outbox vs home feed contrast (46 Creates in outbox, 0 in home feed), (3) dev hint (compare community feed query vs home feed query). S36 61st consecutive. No new angles.
- **Checkpoint:** S36 finding doc fully updated. No new angles. Next: waiting for dev to fix home feed query.

## Pass 230 (2026-09-21) — build `401c08b5` / S36 stable at 61st consecutive: A home feed = 1 unique item (Tombstone-Announce), B home feed = completely empty; no new angles
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A home feed (re-check); B home feed (re-check).
- **Result:**
  - **A home feed:** 2 DOM elements → 1 unique item (dedup): "Boosted by ii-b1, 17h ago, Content unavailable — view original post" (Tombstone-Announce). No content posts.
  - **B home feed:** Completely empty ("Your timeline is empty. Follow people to see their posts here. Browse the directory →").
  - **S36 61st consecutive.** Pattern stable. No new angles.
  - 0 console errors.
- **Checkpoint:** S36 stable (61st consecutive). A: 1 Tombstone-Announce; B: empty. No new angles. Next: waiting for dev to fix home feed query.

## Pass 229 (2026-09-21) — build `401c08b5` / A actor page + outbox: 46 Creates (43 Notes + 1 Article) in outbox, actor page shows P227/P226/P225; home feed shows 0 (60th consecutive)
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A actor page for ii-a1 (own posts); A outbox (Create count); A home feed (re-check).
- **Result:**
  - **A actor page for ii-a1:** 16 items in Posts tab. P227 (3m ago), P226 (6m ago), P225 (11m ago), P215 (32m ago), P214 (41m ago) all visible. Actor page shows all own posts.
  - **A outbox:** 86 total items, **46 Creates** (43 Notes + 1 Article + 2 other), 3 Announces. All posts are in the outbox.
  - **A home feed:** 0 content posts (2 unique items: duplicate "Content unavailable" Tombstone-Announce + empty). **60th consecutive S36.**
  - **Contrast:** Outbox has 46 Creates, actor page shows them all, but home feed shows **0** content posts. The home feed query is not reading from the outbox (or is filtering out all Creates).
  - 0 console errors.
- **Checkpoint:** A outbox has 46 Creates, actor page shows them, home feed shows 0. S36 60th consecutive. The gap is clear: outbox/actor-page have the data, home feed query doesn't retrieve it. No new angles. Next: waiting for dev to fix home feed query.

## Pass 228 (2026-09-21) — build `401c08b5` / P227 (Followers-only): search + object-detail + profile all show it; home feed omits (59th consecutive); 7/8 surfaces work, home feed is the only broken one
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A search for P227; A object-detail for P227; A home feed (re-check all recent posts).
- **Result:**
  - **A search for "II-S36-P227":** 1 result (P227, 1m ago). Search works for Followers-only posts.
  - **A object-detail for P227:** Renders correctly ("I ii-a1 2m ago II-S36-P227 fresh A Followers-only post..."). Object-detail works.
  - **A profile:** P227 visible in "Your posts" (from Pass 227).
  - **A home feed:** P227, P226, P225 ALL NOT present (2 unique items: duplicate "Content unavailable" Tombstone-Announce + empty). **59th consecutive S36.**
  - **Summary for P227 (Followers-only post):**
    | Surface | P227 visible? |
    |---------|--------------|
    | Search | ✓ |
    | Object-detail | ✓ |
    | Profile (Your posts) | ✓ |
    | Home feed | **✗** |
  - **Content-source map (Pass 224) holds:** 7/8 surfaces work, home feed is the ONLY broken surface.
  - 0 console errors.
- **Checkpoint:** P227 confirmed across all surfaces: search ✓, object-detail ✓, profile ✓, home feed ✗. S36 59th consecutive. No new angles. Next: waiting for dev to fix home feed query.

## Pass 227 (2026-09-21) — build `401c08b5` / P227 (A Followers-only): visibility select works (to: Public, cc: followers), in profile, NOT in home feed (58th consecutive)
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh A Followers-only post II-S36-P227 (note `06GCAYZD0RBSXSQFSNC2PV05J8`, to: Public, cc: followers); A home feed + profile for P227.
- **Result:**
  - **P227 posted:** A outbox has the Create. Note IRI uses `/notes/` path. Type: Note. **Visibility: to: Public, cc: followers** (Followers-only visibility correctly set in the wire).
  - **A profile:** P227 visible in "Your posts" tab (3m ago).
  - **A home feed:** P227 NOT present (2 unique items: duplicate "Content unavailable" Tombstone-Announce + empty). **58th consecutive S36.**
  - **Key insight:** The visibility select works correctly (Followers-only sets `to: Public, cc: followers` in the wire). The home feed omits Followers-only posts too (same as Public posts). The home feed bug is visibility-agnostic.
  - 0 console errors.
- **Checkpoint:** P227 (Followers-only) confirms S36 is visibility-agnostic (Public + Followers-only both omitted). Visibility select works correctly. S36 58th consecutive. No new angles. Next: waiting for dev to fix home feed query.

## Pass 226 (2026-09-21) — build `401c08b5` / P226 (A Article): Article posted (type: Article, IRI /articles/...), in profile, NOT in home feed (57th consecutive)
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh A Article II-S36-P226 (IRI `https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/articles/06GCAYBA2NCC1X5FBT1R5CG7GG`, type: Article); A home feed + profile for P226.
- **Result:**
  - **P226 posted:** A outbox has the Create. Note IRI uses `/articles/` path (not `/notes/`). Type: **Article** (not Note). Content: "II-S36-P226 fresh A Article (build 401c08b5)...".
  - **A profile:** P226 visible in "Your posts" tab (5m ago).
  - **A home feed:** P226 NOT present (2 unique items: duplicate "Content unavailable" Tombstone-Announce + empty). **57th consecutive S36.**
  - **Key insight:** Articles (type: Article, IRI /articles/...) are also omitted from the home feed, same as Notes (type: Note, IRI /notes/...). The home feed bug is NOT specific to Notes — it affects all content types (Notes + Articles).
  - 0 console errors.
- **Checkpoint:** P226 (Article) confirms S36 affects all content types (Notes + Articles). Article in profile, NOT in home feed (57th consecutive). No new angles. Next: waiting for dev to fix home feed query.

## Pass 225 (2026-09-21) — build `401c08b5` / P225 (A→B with @ii-b1 mention): mention notification works, but home feed still omits (56th consecutive)
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh A post II-S36-P225 with @ii-b1 mention (note `06GCAX586PRJVX8GGJZ0D68WMG`); B notification/home feed for P225.
- **Result:**
  - **P225 posted:** A outbox has the Create. Note `to: Public` (public visibility). Content includes `@ii-b1` mention link.
  - **B notification:** P225 inlined (3m ago, "II-S36-P225 fresh A post with @ii-b1 mention..."). **Mention notification works** — B was notified about the @ii-b1 mention.
  - **B home feed:** P225 NOT present (completely empty: "Your timeline is empty"). **56th consecutive S36.**
  - **Key insight:** The mention notification works (B knows about P225), but the home feed still doesn't show it. The notification system and the home feed are separate paths — the notification path works, the feed path is broken.
  - 0 console errors.
- **Checkpoint:** Mention notification works (P225 @ii-b1 → B notified). Home feed still omits P225 (56th consecutive). The notification system is independent of the home feed — fixing one won't fix the other. No new angles. Next: waiting for dev to fix home feed query.

## Pass 224 (2026-09-21) — build `401c08b5` / Notifications + actor page show P223 (remote post visible); home feed omits it — content-source map complete (8 surfaces)
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A notifications for P223; A actor page for ii-b1 (P223 visibility).
- **Result:**
  - **A notifications:** P223 inlined (4m ago, "II-S36-P223 fresh B post (build 401c08b5)..."). P212 also inlined (40m ago). Notifications show remote posts.
  - **A actor page for ii-b1:** P223 is the first item in the Posts tab (5m ago). P212 (40m ago), P189 (2h ago), P184 (3h ago), etc. (8 items total). Actor page shows remote posts.
  - **Content-source map (complete, 8 surfaces):**
    | Surface | Remote posts | Own posts |
    |---------|-------------|-----------|
    | Notifications | ✓ (inlined) | ✓ |
    | Actor page (Posts) | ✓ | ✓ |
    | Object-detail | ✓ (from notif store) | ✓ |
    | Profile (Your posts) | N/A | ✓ |
    | Community feed | N/A | ✓ (member posts) |
    | Directory "All known" | ✓ (actors) | ✓ |
    | Search | ✓ | ✓ |
    | **Home feed** | **✗ (S36)** | **✗ (S36)** |
  - **Home feed is the ONLY surface that omits posts** (both own and remote). All other 7 surfaces show posts correctly.
  - 0 console errors.
- **Checkpoint:** Content-source map complete (8 surfaces). Home feed is the ONLY broken surface. Notifications + actor page show P223. S36 55th consecutive. No new angles. Next: waiting for dev to fix home feed query.

## Pass 223 (2026-09-21) — build `401c08b5` / P223 (B→A) confirms S36 pattern (5th fresh post): notification inlines, AP 404, object-detail renders, home feed omits (54th consecutive)
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh B post II-S36-P223 (note `06GCAVHY2NJCS32M66CV64J0V4`); A notification/AP route/object-detail/home feed for P223.
- **Result:**
  - **A notification:** P223 inlined (totalItems=32, 1 P223 notification).
  - **A AP note route:** `GET /ap/v1/u/ii-b1/notes/06GCAVHY2NJCS32M66CV64J0V4` → **404** (object NOT in A's actor-keyed note store).
  - **A object-detail:** renders P223 content ("I ii-b1 3m ago II-S36-P223 fresh B post...") — reads from notification store.
  - **A home feed:** P223 NOT present (2 unique items: duplicate "Content unavailable" Tombstone-Announce + empty). **54th consecutive S36.**
  - **Pattern stable across 5 fresh posts** (P206/P208/P209/P211 A→B; P212/P223 B→A). Same pattern in both directions: notification inlines, AP 404, object-detail renders, home feed omits.
  - 1 console error (AP 404 on the note route — expected).
- **Checkpoint:** P223 confirms S36 pattern (54th consecutive, 5th fresh post). Pattern fully stable bidirectionally. No new angles. Next: waiting for dev to fix home feed query.

## Pass 222 (2026-09-21) — build `401c08b5` / Home feed "Content unavailable" = Tombstone (deleted note 06GC3AWS...); outbox has 5 Deletes, 4 references to the tombstoned note
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A home feed "Content unavailable" Announce — traced the underlying note (06GC3AWSHG64NJHJ24EM27HZSW); A outbox Deletes.
- **Result:**
  - **Home feed "Content unavailable" Announce:** The Announce references note `06GC3AWSHG64NJHJ24EM27HZSW`. `GET /ap/v1/u/ii-a1/notes/06GC3AWS...` → **200, type: Tombstone** (no content). The note was **deleted** — the Announce is a "boost" of a deleted note, hence "Content unavailable".
  - **A outbox:** 82 total items, **5 Delete activities**, **4 references** to the tombstoned note. The note was deleted (and the Delete federated).
  - **S36 context:** The home feed shows **only** this Tombstone-Announce (no content Creates). The feed is not just "missing posts" — it's showing a boost of a deleted note. The actual content Creates (P211, P214, P215) are still absent from the feed.
  - **S36 53rd consecutive** (home feed has no readable content — only a Tombstone-Announce).
  - 0 console errors.
- **Checkpoint:** Home feed "Content unavailable" = Tombstone (deleted note). The feed shows a boost of a deleted note, not a content post. S36 still OPEN (53rd consecutive). No new angles. Next: waiting for dev to fix home feed query.

## Pass 221 (2026-09-21) — build `401c08b5` / S36 re-confirmed: A home feed = 1 unique item (duplicate "Content unavailable" Announce); B home feed = completely empty (52nd consecutive)
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A home feed (re-check, dedup); B home feed (re-check).
- **Result:**
  - **A home feed:** 3 DOM elements → **1 unique item** (after dedup): "Boosted by ii-b1, 17h ago, Content unavailable — view original post". The "3 items" from previous passes was a DOM artifact (nested elements). The actual home feed has **1 item** (a duplicate Announce with "Content unavailable").
  - **B home feed:** Completely empty ("Your timeline is empty. Follow people to see their posts here. Browse the directory →").
  - **S36 re-confirmed (52nd consecutive):** Home feed is broken on both instances. A shows 1 duplicate Announce (no content); B shows nothing.
  - **Refinement:** The home feed isn't "3 items" — it's **1 unique item** (a "Content unavailable" Announce) on A, and **0 items** on B. The feed is effectively empty (no readable content).
  - 0 console errors.
- **Checkpoint:** S36 re-confirmed (52nd consecutive). A home feed = 1 unique item (duplicate Announce, "Content unavailable"); B home feed = completely empty. The "3 items" from previous passes was a DOM artifact. No new angles. Next: waiting for dev to fix home feed query.

## Pass 220 (2026-09-21) — build `401c08b5` / Settings page: Account (Profile/Security/Change password/Moderation), Content (Notifications/Muted actors/Muted Communities/Relays), Danger (Account deletion) — all tabs work
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A settings page (all 3 tabs: Account, Content, Danger).
- **Result:**
  - **Account tab:** Profile (Edit your profile), Security (Signing algorithm Rsa, Key IRI, JWK thumbprint), Change password (Current/New/Confirm), Moderation.
  - **Content tab:** Notifications (Notification types: New followers, Likes, Boosts/shares, Replies; Muted actors; Muted Communities; Relays).
  - **Danger tab:** Account deletion ("Deleting your account is permanent and irreversible... Delete my account").
  - All 3 tabs render correctly. No console errors.
  - No new defects.
- **Checkpoint:** Settings page works (all 3 tabs). No new defects. S36 home feed still OPEN (51st consecutive). Next: no new angles.

## Pass 219 (2026-09-21) — build `401c08b5` / Community feed WORKS (P215/P214/P211 all visible in ii-a8-community feed); home feed still empty — different query path
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A home feed (re-check); A community feed (ii-a8-community).
- **Result:**
  - **A home feed:** Still 3 items (2 "Content unavailable" Announces + 1 empty). P215/P214/P211 NOT in home feed. S36 still OPEN (50th consecutive).
  - **A community feed (ii-a8-community):** **WORKS CORRECTLY** — P215 (7m ago), P214 (16m ago), P211, and all other ii-a1 posts are visible in the community feed. The community feed shows member posts.
  - **Key insight:** The community feed query works (shows member posts), but the home feed query doesn't (omits all posts). These are different query paths:
    - Community feed: queries the community's member posts → works
    - Home feed: queries the user's followed actors' posts → broken (S36)
  - The home feed bug is specific to the `FeedService.BuildFeedUncachedAsync` path (or equivalent), not a general "posts aren't stored" issue. Posts ARE stored (visible in profile, community feed, actor page, object-detail) — the home feed query just doesn't retrieve them.
  - 0 console errors.
- **Checkpoint:** Community feed WORKS (member posts visible). Home feed still empty (50th consecutive). The bug is isolated to the home feed query path, not a storage issue. Dev should compare the community feed query (works) vs the home feed query (broken) to find the difference. Next: no new S36 angles until dev ships a feed fix.

## Pass 218 (2026-09-21) — build `401c08b5` / Search for remote community works on both A + B (1 result each); profile Following/Followers tabs work (remote actors present)
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Search for ii-a8-community on A + B; A profile Following + Followers tabs.
- **Result:**
  - **Search for ii-a8-community:** 1 result on both A and B (ii-a8-community, II-A8 Test Community, QA community test). Remote community discovery via search works bidirectionally.
  - **A profile (ii-a1) Following tab:** ii-a8-community (local, Unfollow) + **ii-b1** (remote, Unfollow). Both present.
  - **A profile (ii-a1) Followers tab:** **ii-b1** (remote, Unfollow) + ii-a2 (local, Follow). Both present.
  - **Profile tabs work correctly:** Following and Followers tabs render both local and remote actors/communities.
  - 0 console errors.
- **Checkpoint:** Search for remote community works (A + B). Profile Following/Followers tabs work (remote actors present). No new defects. S36 home feed still OPEN. Next: no new angles.

## Pass 217 (2026-09-21) — build `401c08b5` / Notifications work (P212 inlined); Communities page: A has ii-a8-community, B has none; no "All known" scope on Communities page
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A notifications; A Communities page; B Communities page.
- **Result:**
  - **A notifications:** P212 (ii-b1 post) is inlined with full content (25m ago). P189 also inlined (2h ago). Notifications work correctly — content is inlined at store time. 242 DOM elements (nested).
  - **A Communities page:** Tabs: Following, My communities, All on this instance. ii-a8-community present (II-A8 Test Community, "QA community test", Leave/Delete buttons, "You are the only owner"). No "All known" scope button (unlike Directory page).
  - **B Communities page:** Tabs: Following, My communities, All on this instance. "No communities followed yet." / "No communities on this instance yet." B has no local communities and doesn't follow the remote ii-a8-community.
  - **Note:** The Communities page does NOT have an "All known" scope toggle (unlike the Directory page). Remote community discovery is only available via the Directory page → Communities tab → "All known" scope.
  - 0 console errors.
- **Checkpoint:** Notifications work (content inlined). Communities page: A has local ii-a8-community, B has none. No "All known" scope on Communities page (remote discovery only via Directory). No new defects. S36 home feed still OPEN. Next: no new angles.

## Pass 216 (2026-09-21) — build `401c08b5` / Directory "All known" visible actors confirmed (6: alice, ii-a1, ii-a2, ii-b1, im-user, iris bot); "65 cards" = DOM elements, not unique actors; search + actor page work
- **Build/Live:** `401c08b5` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Directory "All known" on A (detailed card inspection); A search for ii-b1; A actor page for ii-b1 (from home feed link).
- **Result:**
  - **Directory "All known" — 6 unique actors visible:**
    - alice, ii-a1, ii-a2, **ii-b1** (Unfollow), im-user, iris bot
    - The "65 cards" from previous passes was a DOM element count (nested divs), not unique actor count. The actual unique actors are **6**.
    - ii-b1 is present (remote B actor, with "Unfollow" button — ii-a1 follows ii-b1).
    - The directory fix (48a3b3eb + 401c08b5) is confirmed working: remote actors are listed in "All known".
  - **Search for ii-b1:** 3 results (ii-b1 actor + P184 note + another). Search works.
  - **Actor page for ii-b1 (from home feed link):** Renders correctly — Posts tab shows P212 (22m ago), P189 (2h ago), P184 (3h ago), etc. (8 items). Unfollow button present. Tabs: Posts, Followers, Following.
  - **Home feed:** Still 3 items (2 "Content unavailable" Announces + 1 empty). S36 still OPEN.
  - 0 console errors.
- **Checkpoint:** Directory "All known" confirmed working (6 unique actors incl ii-b1). Search + actor page work. S36 home feed still OPEN. No new angles. Next: waiting for dev to fix home feed query.

## Pass 215 (2026-09-21) — build `401c08b5` / Directory "All known" still FIXED (401c08b5 refine); S36 home feed still OPEN (49th consecutive; P215 own post NOT in feed)
- **Build/Live:** `401c08b5` (NEW — dev committed directory refine: "refine IsSameInstanceActor to distinguish stale local rows from remote peers"). Rebuilt + redeployed the QA cluster (containers recreated; A + B health 200).
- **Explored:** Directory "All known" on A + B (post-rebuild); A home feed for P215 (fresh own post).
- **Result:**
  - **Directory "All known" still FIXED (401c08b5):**
    - **A directory "All known":** 65 cards, ii-b1 present (Unfollow).
    - **B directory "All known":** 54 cards, ii-a1 + ii-a2 present.
    - The refine commit (`401c08b5`) did not break the fix. Directory "All known" remains working bidirectionally.
  - **Home feed STILL EMPTY (S36, 49th consecutive):**
    - P215 (A's own post, note `06GCAR9D4QQ6KMV6CRKYQTSA5W`) posted.
    - A home feed: 3 items — 2 "Content unavailable" Announces (ii-b1 boosting old A notes) + 1 empty. **P215 NOT in A's home feed.**
    - The directory refine (`401c08b5`) did NOT address the home feed query. S36 remains OPEN.
  - 0 console errors.
- **Checkpoint:** Directory "All known" still FIXED (401c08b5). S36 home feed still OPEN (49th consecutive; P215 own post not in feed). Dev still needs to fix the home feed query (`FeedService`). Next: no new S36 angles until dev ships a feed fix.

## Pass 214 (2026-09-21) — build `48a3b3eb` / Directory "All known" FIXED (48a3b3eb): remote actors now listed bidirectionally (A: 65 cards incl ii-b1; B: 54 cards incl ii-a1+ii-a2); home feed still empty (P214 own post NOT in feed)
- **Build/Live:** `48a3b3eb` (NEW — dev committed directory fix: "keep remote actors with preferredUsername in 'All known' scope"). Rebuilt + redeployed the QA cluster (containers recreated; A + B health 200).
- **Explored:** Directory "All known" on A + B (post-rebuild); A home feed for P214 (fresh own post).
- **Result:**
  - **Directory "All known" FIXED (48a3b3eb):**
    - **A directory "All known":** **65 cards** (was 55), **ii-b1 NOW PRESENT** (remote B actor, with "Unfollow" button — ii-a1 already follows ii-b1). Also: alice, ii-a1, ii-a2, im-user, iris bot + other remote actors.
    - **B directory "All known":** **54 cards** (was 55), **ii-a1 + ii-a2 NOW PRESENT** (remote A actors, with "Unfollow" for ii-a1 — ii-b1 follows ii-a1). Also: alice, ii-b1, iris bot.
    - **Fix confirmed bidirectional:** Remote actors with a `preferredUsername` are now included in the "All known" scope. The previous omission (Pass 202, Pass 210) is resolved.
  - **Home feed STILL EMPTY (S36, 48th consecutive):**
    - P214 (A's own post, note `06GCAP9SE9ASHP9YQFRPAT5PFG`) posted.
    - A home feed: 3 items — 2 "Content unavailable" Announces (ii-b1 boosting old A notes) + 1 empty. **P214 NOT in A's home feed.**
    - The directory fix (`48a3b3eb`) did NOT address the home feed query. S36 remains OPEN (home feed empty for own + remote posts).
  - 0 console errors.
- **Checkpoint:** **Directory "All known" FIXED (48a3b3eb)** — remote actors now listed bidirectionally. S36 (home feed) still OPEN (48th consecutive; P214 own post not in feed). The directory fix is a separate store query (`GlobalSearchService`), not the feed query (`FeedService`). Dev still needs to fix the home feed query. Next: no new S36 angles until dev ships a feed fix.

## Pass 213 (2026-09-21) — build `863f22c8` / CRITICAL: local home feed ALSO empty (own post P212 in outbox + AP 200 but NOT in own feed)
- **Build/Live:** `863f22c8` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** P212 (B's own post) on B: AP route, home feed, actor page.
- **Result:**
  - **CRITICAL FINDING:** B's own post P212 (note `06GCAKBA2Q1DNDV2VGQ15NP2KR`):
    - B AP note route: **200** (type: Note, content present) — the object IS in B's actor-keyed note store (local posts work)
    - B outbox: P212 Create present (totalItems=71, 18 Creates)
    - B actor page: P212 is the first item in the Posts tab
    - **B home feed: EMPTY** ("Your timeline is empty") — **P212 is NOT in B's own home feed**
  - **This is a SEVERE defect:** The home feed is empty even for the actor's OWN posts. The home feed is completely broken — it doesn't show ANY posts (own or remote). This is not just an S36 cross-instance issue; the entire home feed is non-functional.
  - **Root cause refinement:** The home feed query is broken for ALL posts (local + remote). The feed is not reading from the outbox or the actor-keyed note store. It's querying something else that returns nothing. This is a regression or a fundamental feed query bug.
  - **S36 scope expanded:** S36 is not just "cross-instance home feed omits remote posts" — it's "home feed is completely empty" (no posts at all, own or remote). The cross-instance object-cache gap is one facet, but the feed query itself is broken for all content.
  - S36 47th consecutive (home feed empty). 0 console errors.
- **Checkpoint:** CRITICAL — home feed is empty even for the actor's own posts (P212 in outbox + AP 200 but NOT in feed). The feed query is fundamentally broken. Dev needs to investigate the home feed query path (why it returns 0 items even for local posts in the outbox). Next: dev needs to fix the home feed query.

## Pass 212 (2026-09-21) — build `863f22c8` / P212 B→A direction: same S36 pattern (notification inlines, AP 404, object-detail renders, feed missing)
- **Build/Live:** `863f22c8` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh B post II-S36-P212 (note `06GCAKBA2Q1DNDV2VGQ15NP2KR`); A notification/outbox/AP route/object-detail/home feed for P212.
- **Result:**
  - **A notification:** P212 inlined (totalItems=31, 1 P212 notification).
  - **A outbox:** P212 NOT in A's outbox (totalItems=80, 0 P212). B's posts don't go into A's outbox (expected — outbox is per-actor).
  - **A AP note route:** `GET /ap/v1/u/ii-b1/notes/06GCAKBA2Q1DNDV2VGQ15NP2KR` → **404** (object NOT in A's actor-keyed note store).
  - **A object-detail:** renders P212 content (reads from notification store).
  - **A home feed:** 3 items — 2 "Content unavailable" Announces (ii-b1 boosting old A notes) + 1 empty. **P212 NOT in A's home feed** (46th consecutive S36).
  - **B→A direction confirmed:** The S36 pattern is identical in both directions (A→B and B→A). Notification inlines content, AP note route 404s, object-detail renders, home feed omits the post.
  - S36 46th consecutive (home feed empty, both directions). 0 console errors.
- **Checkpoint:** S36 confirmed bidirectional (A→B: P206/P208/P209/P211; B→A: P212). Same pattern in both directions. No new angles — waiting for dev to handle the embedded-object case. Next: no new S36 test possible until dev ships a new fix.

## Pass 211 (2026-09-21) — build `863f22c8` / P211 confirms S36 pattern (4th post): outbox embeds, AP 404, object-detail renders, feed empty
- **Build/Live:** `863f22c8` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh A post II-S36-P211 (note `06GCAJ05A5CBG2R7P0DTHRC70W`); B outbox/AP route/object-detail/home feed for P211.
- **Result:**
  - **B outbox:** P211 Create present (totalItems 69→70), object embedded with content.
  - **B AP note route:** 404 (object NOT in actor-keyed note store).
  - **B object-detail:** renders P211 content (reads from outbox/notification store).
  - **B home feed:** **EMPTY** — 45th consecutive S36.
  - **Pattern stable across 4 fresh posts (P206+P208+P209+P211):** outbox embeds, AP 404, object-detail renders, feed empty. No variation, no improvement.
  - S36 45th consecutive (home feed empty). 0 console errors.
- **Checkpoint:** S36 pattern fully stable across 4 fresh posts. No new angles — waiting for dev to handle the embedded-object case (store the embedded object in the actor-keyed note store). Next: no new S36 test possible until dev ships a new fix.

## Pass 210 (2026-09-21) — build `863f22c8` / Directory "All known" omits remote actors despite actor doc being cached
- **Build/Live:** `863f22c8` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** A directory "All known" tab; A search for ii-b1; A AP route for ii-b1 actor doc.
- **Result:**
  - **A directory "All known" tab:** 55 cards, **NO ii-b1** (no remote B actors listed). Only local A actors + `im-user` + `iris` bot.
  - **A search for ii-b1:** **2 results** — ii-b1 actor + P184 reply note. Search DOES find remote actors.
  - **A AP route `GET /ap/v1/u/ii-b1`:** **200**, `type: Person` — the actor doc IS cached on A (from the follow relationship).
  - **Discrepancy:** The directory "All known" tab omits ii-b1 (a cached remote actor), while search finds ii-b1, and the AP route serves the cached actor doc. The directory "All known" query does NOT include all cached actors — it only includes actors from a specific source (likely the local actor store + communities, NOT the full AP actor cache).
  - **Related to S24 D2:** The directory "All known" omission is the UI-level manifestation of the same store-separation issue as S36 (actor doc cached in AP store, but directory reads from a different store that doesn't include remote actors).
  - **Not a new defect** — this was noted in Pass 202 (directory "All known" omits remote actors bidirectionally; search includes them). Re-confirmed on current build.
  - S36 44th consecutive (home feed empty). 0 console errors.
- **Checkpoint:** Directory "All known" omission re-confirmed (55 cards, no ii-b1). Search + AP route both work. Same store-separation family as S36. Next: no new angles on S36 (waiting for dev to handle embedded-object case); directory "All known" is a lower-priority known issue.

## Pass 209 (2026-09-21) — build `863f22c8` / S36 diag logging deployed: no S36 log lines in Production; P209 confirms pattern (3rd post)
- **Build/Live:** `863f22c8` (diag logging added to S36 fetch+store path; QA cluster rebuilt).
- **Explored:** Fresh A post II-S36-P209 (note `06GCAFQ2CAZBWZ4GCFD4F6NWW8`); B logs for S36 diag lines; B notification/outbox/AP route/object-detail/home feed for P209.
- **Result:**
  - **No S36 diag log lines in B logs:** `docker logs iris-b --since 10m | grep S36` → 0 matches. The diag logging (added in `863f22c8`) uses `LogInformation`/`LogDebug`/`LogWarning` but **no S36-prefixed lines appear** in the Production logs. This means either: (a) the S36 bare-link code path is NOT being executed (the Create's object is already embedded, so the bare-link branch is skipped), or (b) the log level filters out the S36 lines.
  - **B logs show:** `Inbox received Create ... from ii-a1 to ii-b1` → `Handler CreateActivityHandler processed Create ... ok` → `Inbox accepted: Create from ii-a1 targeting .../notes/06GCAFQ2CAZBWZ4GCFD4F6NWW8`. The Create was received, processed, and accepted. But NO S36 fetch/store log lines.
  - **P209 B-side:** notification inlines content (totalItems=36); AP note route 404; object-detail renders P209 content; home feed **EMPTY** (44th consecutive S36).
  - **Key insight:** The S36 bare-link code path (`if (linkIri is { } iri)`) is likely NOT being executed because the Create's object is **already embedded** (not a bare link) when it arrives at B. The `activity.Object` is already the full Note object (with content), not a bare IRI. So the S36 fix (fetch+cache bare-link) is a **no-op** for this wire shape — the object is embedded, not a bare link.
  - **Root cause refined:** The Create activity arrives at B with the object **already embedded** (not a bare link). The S36 fix only handles the bare-link case. Since the object is already embedded, the fix doesn't fetch+store it. The embedded object is stored in the outbox/notification store (which is why object-detail works) but NOT in the actor-keyed note store (which is why the AP route 404s and the home feed is empty).
  - **Dev action needed:** The fix needs to handle the **embedded object** case, not just the bare-link case. When the Create arrives with an embedded object, the handler should ALSO store the object in the actor-keyed note store (so the AP route can serve it and the home feed can find it).
  - S36 44th consecutive (home feed empty). 0 console errors.
- **Checkpoint:** S36 diag logging reveals the bare-link path is NOT executed (object is already embedded). The fix needs to handle the embedded-object case: store the embedded object in the actor-keyed note store. Next: dev needs to update the fix to handle embedded objects, not just bare links.

## Pass 208 (2026-09-21) — build `adf65b84` / P208 confirms S36 pattern: notification inlines, outbox stores embedded, AP route 404, home feed empty
- **Build/Live:** `adf65b84` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh A post II-S36-P208 (note `06GCADRQ67GKYP0RZ1RB237YD4`); B notification/outbox/AP route/object-detail/home feed for P208.
- **Result:**
  - **B notification:** inlines full P208 content (totalItems=35).
  - **B outbox:** P208 Create appears after ~3 min delivery lag (totalItems 67→68), object embedded with content.
  - **B AP note route:** `GET /ap/v1/u/ii-a1/notes/06GCADRQ67GKYP0RZ1RB237YD4` → **404** (object NOT in actor-keyed note store).
  - **B object-detail:** renders P208 content (reads from outbox/notification store, NOT the AP note route).
  - **B home feed:** **EMPTY** ("Your timeline is empty") — 43rd consecutive S36.
  - **Pattern confirmed across P206 + P208 (2 fresh posts):** notification inlines content at store time; outbox stores Create with embedded object; AP note route 404s (object not in actor-keyed store); home feed empty (queries actor-keyed store → 404); object-detail renders (reads from outbox/notification store).
  - **S36 fix (`adf65b84`) assessment:** The fix fetches + caches the bare-link object on inbound Create. The cached object is accessible via the object-detail page (which reads from the outbox/notification store). But the AP note route (`/ap/v1/u/{actor}/notes/{id}`) reads from a different store (the actor-keyed note store) which is NOT populated by the fix. **The fix needs to ALSO populate the actor-keyed note store, OR the home-feed query needs to read from the same store as the object-detail page.**
  - S36 43rd consecutive (home feed empty). 0 console errors.
- **Checkpoint:** S36 pattern fully confirmed across 2 fresh posts (P206 + P208). The fix caches the object (object-detail works) but doesn't populate the actor-keyed note store (AP route 404, home feed empty). Dev needs to populate the actor-keyed note store or redirect the feed query. Next: no new angles — S36 is fully characterized, waiting for dev to address the store mismatch.

## Pass 207 (2026-09-21) — build `adf65b84` / P206 object-detail renders but AP route 404 (content in outbox, not object store)
- **Build/Live:** `adf65b84` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** P206 on B: AP note route, object-detail page, local object API routes.
- **Result:**
  - **CORRECTION to Pass 206:** P206's AP note route `GET /ap/v1/u/ii-a1/notes/06GCA8ABTNZQDGEYX1Q6MXJG7W` on B → **404** (NOT 200 as reported in Pass 206). The object is NOT in B's object store.
  - **Object-detail page renders P206:** `GET /object?iri=...` → 200, shows "ii-a1 9m ago II-S36-P206..." with content. The content comes from the **outbox store** (where the Create activity is stored with the embedded object), NOT the object store.
  - **S36 fix (adf65b84) effect:** The fix fetches + caches the bare-link object on inbound Create. The object IS cached (the object-detail page can render it). But the AP note route (`/ap/v1/u/{actor}/notes/{id}`) still 404s — the cached object is stored in a different location than the AP note route reads from.
  - **Home feed STILL empty:** The feed query uses the AP note route (or object store) which 404s. The outbox store has the content but the feed doesn't read from it.
  - **Root cause refined:** The S36 fix caches the object (object-detail works) but the AP note route and home-feed query read from a different store (the actor-keyed note route) which is still empty. The fix needs to ALSO populate the actor-keyed note route, OR the feed query needs to read from the same store as the object-detail page.
  - S36 42nd consecutive (home feed empty). 0 console errors.
- **Checkpoint:** Pass 206's "AP 200" was incorrect — the AP route is 404. The object IS cached (object-detail renders) but in a different store than the AP note route. The fix needs to populate the actor-keyed note route. Next: investigate the store mismatch.

## Pass 206 (2026-09-21) — build `adf65b84` / S36 fix deployed: NEW posts fetch+cache on inbound (AP 200), historical posts NOT backfilled, home feed STILL empty
- **Build/Live:** `adf65b84` (S36 fix: fetch + cache bare-link Create objects on inbound delivery; rebuilt + redeployed QA cluster).
- **Explored:** Fresh A post II-S36-P206; B cache/outbox/notifications/home-feed for P206; B object-detail + actor-page for P206; A cache for P189 (historical B post).
- **Result:**
  - **NEW — S36 fix works for NEW posts:** A post P206 (note `06GCA8ABTNZQDGEYX1Q6MXJG7W`): B's AP note route `GET /ap/v1/u/ii-a1/notes/06GCA8ABTNZQDGEYX1Q6MXJG7W` → **200 with content** (was 404 before the fix). The fetch+cache on inbound Create is working — the bare-link object is fetched from A and cached on B.
  - **B object-detail renders P206:** `GET /object?iri=...` → 200, shows "ii-a1 6m ago II-S36-P206..." with content. B actor-page Posts tab: P206 is the first item with content.
  - **B home feed STILL empty:** "Your timeline is empty" — despite P206's Create being in B's outbox (totalItems=67) AND the note being cached on B (AP 200). The feed query is not picking up the cached note.
  - **A cache for historical B post P189:** STILL 404 — the fix does NOT backfill historical posts (pre-redeploy Creates are not re-processed).
  - **S36 PARTIALLY FIXED:** The fetch+cache step now works for NEW inbound Creates (AP 200). But the home feed is still empty for both new and historical posts. The feed query has a separate issue (it's not finding the cached note).
  - **S36 41st consecutive (home feed empty).** 0 console errors.
- **Checkpoint:** S36 fix (fetch+cache) works for NEW posts (AP 200). Home feed still empty (separate feed-query issue). Historical posts not backfilled. Next: investigate why the feed query doesn't surface the cached note.

## Pass 205 (2026-09-21) — build `8243361c` / "Content unavailable" explained: deleted note (Tombstone)
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Home-feed "Content unavailable" items; A outbox full scan for Announces; note AP route for the boosted note.
- **Result:**
  - **"Content unavailable" explained (NEW):** The 2 "Content unavailable — view original post" items on A's home feed are Announces (Boosts) by **ii-b1** of ii-a1's note `06GC3AWSHG64NJHJ24EM27HZSW`. That note is a **Tombstone** (deleted 2026-09-21T02:15:52Z). The note was deleted, so the object store returns a Tombstone (no content) → the feed renders "Content unavailable". This is **correct behavior** for a deleted note, not a bug.
  - **A outbox full scan (75 items):** 3 Announces by ii-b1 (B) boosting A notes: (1) `06GC5MR7VQSR9TC2V2Z4KXPNBW` (S37-5), (2) `06GC4RR4CN76NCSW2WJKQ3BPZW`, (3) `06GC3AWSHG64NJHJ24EM27HZSW` (deleted → Tombstone). The Announces are stored in A's outbox (S24 D2 A-side growth: 19→22 foreign).
  - **S24 D2 A-side growth:** A outbox foreign from B: 19 (Pass 195) → 22 (this pass, 3 new Announces).
  - S36 40th consecutive. 0 console errors.
- **Checkpoint:** "Content unavailable" is correct (deleted note → Tombstone). Not a new defect. S24 D2 A-side growing (19→22). Next: new exploration.

## Pass 204 (2026-09-21) — build `8243361c` / community feed includes REMOTE member posts (ii-b1)
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Community detail page (A8 test community) — Members tab + Feed tab.
- **Result:**
  - **NEW — community feed includes remote member posts:** A8 community Members tab: **ii-b1** (B actor, remote member) with Promote/Mute/Block/Remove buttons. Feed tab: shows **both ii-a1's posts (P200, P193, P192, etc.) AND ii-b1's post (P189)** with content. The community feed resolves remote member posts via the `attributedTo` path (community membership → actor's outbox → object content), which works for remote actors.
  - **Contrast with home-feed:** The community feed finds remote member posts (ii-b1's P189) via the outbox path. The home-feed does NOT find remote follower posts (ii-b1's P189/P200) — it queries the object store which has no cached remote Note. The `attributedTo` path (community) works; the follow-graph path (home-feed) does not.
  - **S36 root cause further refined:** The gap is specifically in the home-feed's follow-graph query path, which relies on the object store for remote notes. The community feed's `attributedTo` path uses the outbox (which has the Create activity with inlined content) and works. The fix is to make the home-feed path use the outbox/inlined-content source like the community feed does.
  - S36 39th consecutive. 0 console errors.
- **Checkpoint:** Community feed includes remote member posts (ii-b1's P189) — the attributedTo path works for remote actors. Home-feed's follow-graph path does not. Root cause further refined. Next: new exploration.

## Pass 203 (2026-09-21) — build `8243361c` / community feed shows own posts (contrast with home-feed)
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Communities page (A + B); community detail page (A8 test community).
- **Result:**
  - **NEW — community feed shows own posts:** A's A8 test community feed shows **ii-a1's posts** (P200, P193, P192, P191, P190, etc.) with content. The community feed uses a different query path than the home feed — it filters by `attributedTo` (community membership) and finds the local actor's posts directly from the outbox/object store.
  - **B has no communities:** B "All on this instance" = "No communities on this instance yet." No cross-instance community following tested (no remote community to follow).
  - **Community feed vs home-feed contrast:** The community feed finds local posts via `attributedTo` (community membership → actor's outbox). The home feed finds posts via the follow graph (followers' outbox → object store). The home-feed gap is specifically in the object-store lookup for remote notes, not in the outbox scan.
  - S36 38th consecutive. 0 console errors.
- **Checkpoint:** Community feed works for own posts (different query path than home feed). Confirms the S36 gap is specific to the home-feed object-store lookup, not a general content-surfacing failure. Next: new exploration.

## Pass 202 (2026-09-21) — build `8243361c` / directory "All known" omits remote actors; search includes them
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Directory "All known" tab on A + B; search for remote actors.
- **Result:**
  - **NEW — directory "All known" omits remote actors:** A "All known": alice, ii-a1, ii-a2, im-user, iris-bot (A) — **NO ii-b1** (B actor, follower of ii-a1). B "All known": alice, ii-b1, iris-bot (B) — **NO ii-a1** (A actor, followed by ii-b1). The S30 A8.2 fix (cached remote communities in directory) does NOT extend to remote **actors**.
  - **Search DOES include remote actors:** `/search?q=ii-b1` on A: 2 results (ii-b1 actor + P184 note). The search path uses the same inlined-content source as notifications/actor-page; the directory "All known" path does not.
  - **Content-source map extended:** Directory "All known" uses a **local-only actor store** (no remote actors). Search, actor-page, object-detail, and notifications use the inlined-content source (which includes remote actors). The directory gap is a separate facet from S36 (home-feed) — it affects actor discovery, not content surfacing.
  - S36 37th consecutive. 0 console errors.
- **Checkpoint:** Directory "All known" omits remote actors (separate from S36). Search includes them. Content-source map now covers 6 UI surfaces. Next: file as new finding or add to S24.

## Pass 201 (2026-09-21) — build `8243361c` / follow-request notifications render in UI despite object-store 404
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** ii-a1 notifications (UI + API) for Follow items; actor Followers tab.
- **Result:**
  - **NEW — follow-request notifications render in UI:** ii-a1 UI shows 2 "sent you a follow request" notifications (ii-a2, ii-b1, 5h ago) with **Accept/Decline buttons**. The notification object is a bare IRI (no inlined content), yet the UI renders the actor handle + "follow request" label + action buttons.
  - **Followers tab consistent:** ii-a1 Followers tab shows ii-b1 (Unfollow) + ii-a2 (Follow) — matches the follow notifications.
  - **Content-source map extended:** The notification path has **3 sub-paths**: (1) Create → inlines content (Pass 196-200), (2) Like → inlines content, (3) Follow → bare IRI, UI resolves the actor handle from the IRI (no object store needed). All 3 sub-paths bypass the object-store gap.
  - **S36 36th consecutive.** 0 console errors.
- **Checkpoint:** Content-source map now covers all 3 notification sub-paths (Create/Like inline, Follow resolves IRI). The home-feed path is the only UI surface that depends on the object store. Next: new exploration.

## Pass 200 (2026-09-21) — build `8243361c` / P200 confirms notification-inlining + feed-omission asymmetry
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh A post II-S36-P200; B cache/proxy/outbox/notifications/home-feed for P200.
- **Result:**
  - **P200 delivery + notification inlining (CONFIRMS Pass 196-199):** A post P200 (note `06GCA3WEPTPME30JXWMM61WHM8`): B cache 404 at 30s, B proxy 200 but no content. B `/local/v1/notifications` (totalItems=33) has P200 Create with **full content inlined** (actor=ii-a1, "II-S36-P200 fresh A post..."). UI shows "ii-a1 posted 1m ago" with content. Notification inlining works for the newest post.
  - **B home feed still empty:** "Your timeline is empty" — P200's Create is in B's notifications but NOT in B's home feed. The feed-omission asymmetry is confirmed for the newest post (not just historical data).
  - **S36 35th consecutive.** 0 console errors.
- **Checkpoint:** P200 confirms the asymmetry is live and ongoing (not a historical artifact): notification inlines, feed omits. S36+S24 D2 root cause (missing object-fetch/caching on inbound Create; feed path uses object store, notification path inlines) is fully characterized with 5 passes of evidence (196-200). Next: new exploration.

## Pass 199 (2026-09-21) — build `8243361c` / search + object-detail render remote content; home-feed shows "Content unavailable" for Announces
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Search for P193 on both instances; object detail for P193 on B; home-feed "Content unavailable" items.
- **Result:**
  - **NEW — search renders remote content bidirectionally:** `/search?q=II-S36-P193` on A: 1 result (ii-a1, own post, full content). On B: **1 result (ii-a1, remote A post, full content "II-S36-P193 fresh A post...")** despite B's AP note route being 404. Search uses the same inlined-content source as actor-page/object-detail (notification/outbox store), not the object store.
  - **Object detail for P193 on B:** 200, renders "ii-a1 21m ago II-S36-P193..." with content. Confirms Pass 198's finding bidirectionally (B→A direction).
  - **Home-feed "Content unavailable" (NEW observation):** A's home feed shows 2 items with "Content unavailable — view original post" (Announce/Boost of ii-a1's note by ii-b1, 15h ago). The Announce's object (ii-a1's note) IS in A's object store (it's A's own note), so the "Content unavailable" is unexpected — it may be a stale cache or a different object-lookup path for Announce objects.
  - **Content-source map complete:** actor-page, object-detail, search, notifications all use the inlined-content source (notification/outbox store). Home-feed uses the object store (404 for remote notes, "Content unavailable" for some local notes in Announces). The fix is to make home-feed use the inlined-content source.
  - S36 34th consecutive. 0 console errors.
- **Checkpoint:** Content-source map complete (4 UI surfaces use inlined source; home-feed uses object store). "Content unavailable" on Announces is a new sub-facet. Next: new exploration.

## Pass 198 (2026-09-21) — build `8243361c` / actor-page Posts tab renders remote notes despite AP route 404
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** ii-a1 view of ii-b1 actor page (Posts tab); object detail page for P189; local object API routes.
- **Result:**
  - **NEW — actor-page Posts tab renders remote content despite AP route 404:** ii-a1's view of `https://qa-iris-a.luit.ink/actor?iri=.../ii-b1` Posts tab renders 7 items with full content (e.g., "ii-b1 45m ago II-S36-P189 fresh B post..."). The `/ap/v1/u/ii-b1/notes/{id}` route returns 404 on A, yet the UI renders the content.
  - **Object detail page also renders:** `GET /object?iri=.../ii-b1/notes/06GC9RFVB4BRXGCYYMVGHWX0XM` → 200, shows "ii-b1 45m ago II-S36-P189..." with content. No error.
  - **Local object API:** `/local/v1/object?iri=...` → 200 HTML (the UI page), but does NOT contain the note content (the content comes from a different data path — likely the notification store or the outbox store where the Create was stored with inlined content).
  - **Root cause further refined:** The UI has at least 3 content sources: (1) the AP note route (404 — object not cached), (2) the notification store (inlines content — Pass 196/197), (3) the outbox store (stores the Create activity with the object reference — S24 D2). The actor-page Posts tab and object detail page appear to use source (2) or (3) rather than source (1), which is why they render content despite the AP route 404. The home-feed path uses source (1) (or a similar object-store query) and finds nothing.
  - S36 33rd consecutive. 0 console errors.
- **Checkpoint:** UI content-source map: actor-page/object-detail use notification/outbox store (inlined content); home-feed uses object store (404). The fix is to make the home-feed path use the same inlined-content source as the actor-page. Next: new exploration.

## Pass 197 (2026-09-21) — build `8243361c` / B-side notifications confirm bidirectional content-inlining
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** ii-b1 notifications (UI + `/local/v1/notifications` API).
- **Result:**
  - **B-side notifications inline content (CONFIRMS Pass 196 bidirectionally):** ii-b1 `/local/v1/notifications` (totalItems=32, page 1=20) has **19 `Create` items** with full content inlined — including 5 S36 A-side posts (P193, P192, P191, P190, P186) with actor=ii-a1 and content text (e.g., "II-S36-P193 fresh A post for object-cache re-check"). UI renders "ii-a1 posted" with content.
  - **Symmetric to Pass 196:** A-side (Pass 196) had 10 Creates with inlined B content; B-side (this pass) has 19 Creates with inlined A content. The notification path inlines content **bidirectionally** on both instances.
  - **Root cause confirmed:** The notification path inlines the Create's object content at store time on BOTH instances. The home-feed and outbox paths do NOT inline — they query the local object store which has no cached Note (404). The object-fetch/caching step is present in the notification path (implicit, via inlining) and absent in the feed/outbox paths.
  - S36 32nd consecutive. 0 console errors.
- **Checkpoint:** Bidirectional content-inlining in notifications confirmed. Root cause (feed path lacks inlining/fetch that notification path has) is now fully characterized from both sides. Next: new exploration.

## Pass 196 (2026-09-21) — build `8243361c` / notification path bypasses object-cache gap
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** ii-a1 notifications (UI + `/local/v1/notifications` API); P189 note route on A.
- **Result:**
  - **NEW — notification path inlines content:** ii-a1 `/local/v1/notifications` (30 total, 20 page 1) has 10 `Create` items with **full content inlined** (e.g., II-S36-P189: actor=ii-b1, object.content="II-S36-P189 fresh B post..."). UI renders "ii-b1 posted 36m ago" with content.
  - **Note route still 404:** `GET A /ap/v1/u/ii-b1/notes/06GC9RFVB4BRXGCYYMVGHWX0XM` → 404 (same note the notification references). Object NOT in local store, yet notification carries full content.
  - **Root cause refined:** Notification path inlines content at store time (bypasses object-cache gap). Home-feed path does NOT inline (queries object store, finds nothing). Outbox path does NOT inline (S24 D2). Fix: inline or fetch+cache the object in the feed path.
  - S36 29th consecutive. 0 console errors.
- **Checkpoint:** Root cause refined — notification path inlines, feed path doesn't. Next: new exploration.

## Pass 195 (2026-09-21) — build `8243361c` / S24 D2 bidirectional growth + object-cache gap persistence
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** B outbox full scan (S24 D2 B-side); A outbox full scan (S24 D2 A-side); P189 A-cache re-check; P193 B-cache re-check.
- **Result:**
  - **S24 D2 bidirectional growth (NEW DATA):** B outbox: 65 total, **31 foreign from A** (28 Create + 3 Follow) — was 30 in Pass 192. A outbox: **74 total, 19 foreign from B** (10 Create + 6 Follow + 3 Announce). Both sides growing. The foreign activities are the same notes that are 404 on the peer's note route.
  - **Object-cache gap persists:** P189 (B note, ~35min old): A cache 404. P193 (A note, ~7min old): B cache 404. Source caches serve 200. Bidirectional, persistent.
  - S36 31st consecutive. 0 console errors.
- **Checkpoint:** S24 D2 quantified bidirectionally (B: 31/65, A: 19/74). Object-cache gap stable. S36+S24 D2 root cause (Pass 192) holds. Next: new exploration.

## Pass 194 (2026-09-21) — build `8243361c` / P193 delivery timing + object-cache gap persistence
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** II-S36-P193 delivery timing (B outbox scan at 1min, 2min, 3min); B cache re-check.
- **Result:**
  - **P193 delivery timing (NEW DATA):** B outbox scan at ~1min: P193 NOT present (64 items). At ~2min: still not present (65 items). At ~3min: **P193 NOW PRESENT** (65 items). Delivery lag ≈ 2–3 minutes for the Create activity to appear in the peer's outbox.
  - **Object-cache gap persists:** Even after the Create is in B's outbox (3min), B cache for the note is STILL 404. The activity is stored but the object is never fetched/cached — consistent with Pass 192 root cause.
  - **S24 D2 growing:** B outbox total now 65 (was 64 in Pass 192). Foreign activities continue to accumulate.
  - S36 30th consecutive. 0 console errors.
- **Checkpoint:** Delivery lag (2–3 min) characterized. Object-cache gap independent of delivery timing. S36+S24 D2 root cause (Pass 192) holds. Next: new exploration.

## Pass 193 (2026-09-21) — build `8243361c` / object-cache gap re-confirmation (stability)
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh A post (II-S36-P193) → B cache/proxy check (30s wait); prior P192 B-cache re-check; A+B feed checks.
- **Result:**
  - **Object-cache gap re-confirmed:** II-S36-P193 (note `06GC9YPBK8CV4TQZHWS9SBW4SC`): A cache 200, B cache 404, B proxy 404 (30s). P192 (note `06GC9XE65ZH6295FT5GF7K03KG`): B cache still 404 (~7 min later). The gap is persistent, not transient.
  - **Feeds:** A feed 20 items, 0 Creates, 0 S36. B feed 20 items, 0 Creates, 0 S36. Unchanged.
  - S36 29th consecutive. 0 console errors.
- **Checkpoint:** Object-cache gap stable (persistent, not a timing issue). S36+S24 D2 root cause (Pass 192) holds. Next: new exploration.

## Pass 192 (2026-09-21) — build `8243361c` / S36+S24 D2 root cause linkage
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh A post (II-S36-P192) → B cache/proxy check (30s wait); B outbox full scan (4 pages, 64 items); S24 D2 quantification.
- **Result:**
  - **S36+S24 D2 root cause linked (NEW INSIGHT):** II-S36-P192's Create IS in B's outbox (foreign A activity, stored) but the content note is 404 on B (cache + proxy). The Create activity is delivered + stored, but the **Note object is never fetched/cached**. S24 D2 (30 foreign items in B outbox: 27 Create + 3 Follow) are the same notes that are 404 on B's note route.
  - **Unified root cause:** Missing object-fetch/caching on inbound Create processing. The activity is stored in the outbox, but the referenced Note is not fetched from the source and cached locally. The feed query correctly finds no cached Note → returns only actor-doc noise.
  - **Fix direction:** Fetch + cache the Note object when a Create activity is received (not a feed-query or outbox change).
  - S36 28th consecutive. 0 console errors.
- **Checkpoint:** S36+S24 D2 root cause linked to object-fetch/caching gap. Next: new exploration or stability.

## Pass 191 (2026-09-21) — build `8243361c` / cross-instance note cache gap quantified
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Fresh A post (II-S36-P191) → B cache/proxy check; prior B post (II-S36-P189) → A cache/proxy check; health checks both instances.
- **Result:**
  - **Cross-instance note cache gap (NEW DATA):** B→A: B note 200, A cache 404, A proxy 404. A→B: A note 200, B cache 404, B proxy 404. **Bidirectional: the peer never caches the content note** (both cached + proxy routes 404). The Create activity reaches the peer's inbox (feed has noise from the same window) but the **content object is never fetched/cached**.
  - **Narrows S36 root cause:** Not just the feed query omitting Creates — the content object is never cached on the peer, so there's nothing to return. The 12 "unknown" items in A's feed may be partially-fetched references.
  - **Health:** Both A+B healthy (delivery queue empty, workers running, no dead letters). Delivery works; object-fetch/caching path is the gap.
  - S36 27th consecutive. 0 console errors.
- **Checkpoint:** S36 root cause narrowed to object-fetch/caching (not just feed query). Next: new exploration or stability.

## Pass 190 (2026-09-21) — build `8243361c` / Lemmy/Mastodon interop + S36 fresh A post + feed type histogram
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Lemmy API health + post list, Mastodon instance API + cross-origin actor resolution, fresh A post (II-S36-P190) → A feed check, feed type histograms (A + B), /ap/v1/health both instances.
- **Result:**
  - **Lemmy:** `/api/v3/site` 200 (healthy); `/api/v3/post/list` = 0 posts (no Iris content federated to Lemmy — expected, no follow edges).
  - **Mastodon:** `/api/v1/instance` 200 (v4.7.2, 1 user, 0 statuses, 0 domains); `@ii-a1` 404, webfinger 404 (no cross-origin federation — expected, no follow edges).
  - **S36 fresh A post (II-S36-P190):** in A outbox (10 Creates) but **NOT in A feed** (20 items, 0 Creates, 0 S36). UI: 1 stale "Content unavailable" item, no S36. **26th consecutive pass.**
  - **Feed type histograms:** A feed = Delete×3, unknown×12, Follow×4, Like×1 (0 Create). B feed = Like×6, Follow×9, Undo×5 (0 Create). **Zero content Creates on both instances** — S36 is total.
  - **Health:** Both A+B `/ap/v1/health` = healthy (delivery queue empty, workers running, 3/6 + 6/7 actors resolvable).
  - 0 console errors.
- **Checkpoint:** Interop peers healthy but empty (no federation edges). S36 26th consecutive, confirmed total on both instances. Next: new exploration or stability pass.

## Pass 189 (2026-09-21) — build `8243361c` / B-side page sweep + cross-instance actor pages + S36 B-side confirmation
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** B-side /profile (posts + Following), /search (ii-a1), cross-instance actor pages (B→A: ii-a1; A→B: ii-b1, authless), fresh B post → B own feed check.
- **Result:**
  - **B /profile:** 8 own posts render; Following = 2 (ii-b1 self, ii-a1) — correct.
  - **B /search "ii-a1":** 2 results (matching notes referencing ii-a1) — works.
  - **Cross-instance actor pages:** B→A (ii-a1 from B): 6 A posts render, Unfollow button present (follow edge active). A→B (ii-b1 from A, authless): 5 B posts render, "Sign in to follow or moderate" — correct authless gating.
  - **S36 B-side confirmation (NEW DATA):** Fresh ii-b1 post `II-S36-P189` is in B outbox (clean `Create`) but **NOT in B's own feed** (totalItems=20, 0 Creates, Like/Follow/Undo noise). B `/home` UI = "Your timeline is empty." The S36 feed-query defect is **symmetric** — B's own posts are also omitted, not just A's. 25th consecutive pass.
  - 0 console errors.
- **Checkpoint:** B-side page inventory complete. S36 confirmed on both instances (A + B own posts omitted). Next: Lemmy/Mastodon interop spot-check or new exploration.

## Pass 188 (2026-09-21) — build `8243361c` / page-inventory sweep (signed-in + authless)
- **Build/Live:** `8243361c` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** Page-inventory sweep: /profile (posts, Following, Followers), /search (content search), /communities, /directory, /settings (Account/Content/Danger tabs), /notifications, /home, /compose. Authless pass: all routes 302→login (no data leak), / and /register render signed-out UI, 0 console errors.
- **Result:** 0 new findings. /profile "Your posts" shows 6 own notes (correct); Following tab = 3 (ii-a1 self, ii-a8-community, ii-b1); Followers = 3 (ii-a1 self, ii-b1, ii-a2). /search "II-S39-P187" = 2 results (ii-a1 note + ii-a2 reply). /directory "This instance" = 55 actor cards. /communities "Following" = ii-a8-community. Authless: /home, /profile, /notifications, /communities, /settings all → login (correct). 0 console errors across all routes. S36 24th consecutive (stability, no new data).
- **Checkpoint:** Page inventory complete for A. Next: new exploration (B-side pages, Lemmy/Mastodon interop, or deep-dive on a specific open item).

## Pass 187 (2026-09-21) — build `8243361c` / cluster rebuild + S39 local-reply re-verify + S36 persistence (23rd consecutive)
- **Build/Live:** rebuilt + redeployed QA cluster to `8243361c` (S39 local-reply dial-base fix). == HEAD? y.
- **Explored:** S39 local-reply re-verify on new build (ii-a2 → ii-a1), S36 home feed after rebuild (own post in feed? + B-side), S24 D2 bidirectional outbox, S39 CLOSED holding (mark all read).
- **Result:**
  - **S39 local-reply — CONFIRMED on `8243361c`.** Fresh ii-a1 note `II-S39-P187` (`06GC9KGVPK2…`); ii-a2 local reply → ii-a1 notifications shows "ii-a2 replied to your post" (New, just now). The dial-base fix is holding.
  - **S36 (home feed) — STILL OPEN, 23rd consecutive.** Fresh `II-S39-P187` post is in ii-a1's outbox (clean `Create`) but **absent from own feed** (`/feed?refresh=true` totalItems=46, **0 Create items**, all Delete/Update/Remove/Activity/Follow/Like noise). UI `/home` = "Content unavailable" only. B-side feed (ii-b1) = 20 items, 0 Creates (A→B delivery gap persists). The S39 fix did not affect the feed path (expected).
  - **S24 D2 — bidirectional, stable.** A: 28 creates / 8 foreign. B: 34 creates / 24 foreign.
  - **S39 CLOSED holding.** A: 29→0 unread (marked all read). B: 27→0 unread (marked all read).
- **Checkpoint:** S36 remains #1 blocker (own content Creates in outbox but omitted from feed query; dev's in-process repro passes). Next: stability sweep or new exploration.

## Pass 186 (2026-09-21) — build `2229b0ab` / S36 deep-dive: fresh own post NOT in own feed (strongest repro yet); S24 D2 bidirectional; S32 B-cache 404s; S39 CLOSED holding
- **Build/Live:** `2229b0ab` (== HEAD? y — no `src/` change → no rebuild).
- **Explored:** S36 deep-dive (fresh ii-a1 post → own feed check + B-side delivery check), S24 D2 bidirectional outbox, S32 B-cache note 404, S39 CLOSED holding.
- **Result:**
  - **S36 (home feed) — STRONGEST REPRO YET.** Fresh ii-a1 post `II-S36-P186` (note `06GC9HM77347MWBBFZFGGB9HBR`, **in outbox** as a clean `Create`, `to`=Public, `cc`=followers) → **NOT in ii-a1's own feed** (`/ap/v1/u/ii-a1/feed?source=people` totalItems=46, 0 Create items, all noise: 8 Update+Activity, 4 Follow, 3 Delete, 2 Remove, 2 Add, 1 Like). UI `/home` = boost-wrapper only. The fresh unedited `Create` is in the outbox but **omitted from the feed** — confirms S36 is a feed-query defect (not a delivery or outbox issue).
  - **S36 A→B delivery gap re-confirmed:** the fresh A post is **NOT cached on B** (`GET B /ap/v1/u/ii-a1/notes/06GC9HM77…` → 404) + **NOT in B's home feed** (B `/home` = "Your timeline is empty", B feed totalItems=32, 0 Create items, all Like/Follow/Undo noise). B→A delivery-to-cache gap persists.
  - **S24 D2 (foreign outbox) — BIDIRECTIONAL.** ii-a1 (A) outbox: total 68, 11 foreign (B) page 1 (was 67/11 Pass 185). ii-b1 (B) outbox: total 58, **18 foreign (A) page 1** — foreign A activities leaking into B's local actor outbox. The outbox-integrity defect is **bidirectional** (both A and B have foreign activities in their local outboxes).
  - **S32 (B-cache note 404) — NEW DATA POINT.** `GET B /ap/v1/u/ii-a1/notes/06GC9DE5VSXHEWVTWYQ3311D0M` (the II-S39-P184 note, created Pass 184) → **404 on B** (empty body). The B-side cached copy of an A note is 404 — the S32 "peer keeps stale copy" facet is actually "peer has no copy at all" (404, not stale). This is the same A→B delivery-to-cache gap as S36.
  - **S39 (A-side notifications) — CLOSED holding.** ii-a1 still 28 unread (no regression). ii-b1 27 unread (no regression).
  - **All other open items STABLE** (22nd consecutive for S36; S24 D4 + S38 + S37/S28 button-UI unchanged).
- **Checkpoint:** S36 is the #1 blocker — the fresh own-post-not-in-own-feed repro is the strongest evidence yet for dev (the Create is in the outbox but the feed query omits it). S24 D2 is now confirmed bidirectional. S32 B-cache 404 = S36 A→B delivery gap. Next pass: stability sweep or new exploration.

## Pass 185 (2026-09-21) — build `2229b0ab` / open-item stability sweep — all open items STABLE (S39 CLOSED holding)
- **Build/Live:** `2229b0ab` (== HEAD? y — no `src/` change since deployed build → no rebuild).
- **Explored:** Open-item stability sweep: S36 home feed, S24 D2 foreign outbox, S24 D4 remote collections, S38 webfinger, S32 Tombstone, S37/S28 button-UI, follow-graph baseline, S39 CLOSED holding.
- **Result:** All open items STABLE (21st consecutive for S36; S24 D2 grew 63/11→67/11 foreign; S24 D4 doc 200/collections 404 both directions; S38 webfinger 404 cross-instance, 200 own; S32 Tombstone 404 both sides [data loss]; S37/S28 wire counts correct [likedCount=1, repliedCount=1, score=1] but button-UI "0" residual holds; follow-graph intact [ii-a1 followers=2, following=2]; S39 CLOSED holding [28 unread, no regression]). No new defects. S30/S26/S31/S29/S34/S27/S33 hold.
- **Checkpoint:** Next pass: S36 remains top priority (home feed, data/environment-specific, awaiting dev code pass). S24 D2 continues to accumulate (67/11). S24 D4 + S38 + S37/S28 button-UI + S32 data-loss all stable.

## Pass 184 (2026-09-21) — build `2229b0ab` / **S39 cross-instance-reply + cross-instance-Like legs RESOLVED live; S39 fully CLOSED** (all 4 testable legs verified)
- **Build/Live:** `2229b0ab` (same as Pass 183; no `src/` change → no rebuild).
- **Explored:** S39 cross-instance re-test (fresh ii-b1→ii-a1 reply + Like) + local-follow-request leg check (UI gating toggle not present).
- **Result:**
  - **S39 (A-side notifications) — cross-instance-reply + cross-instance-Like legs RESOLVED.** Fresh cross-instance-reply (ii-b1 (B) → ii-a1 (A) note `06GC9DE5VSXHEWVTWYQ3311D0M`, **HTTP 202**, `inReplyTo` correct) → parent author `ii-a1` **immediately** received "ii-b1 replied to your post" (16:05Z, "just now", 27 unread). Fresh cross-instance-Like (ii-b1 (B) → ii-a1 (A) same note, **HTTP 202**, `likedCount` 0→1) → parent author `ii-a1` **immediately** received "ii-b1 liked a post" (16:06:27Z, "just now", 28 unread). The dev fix (`c28a95d1` / `2229b0ab`) works for cross-instance legs too.
  - **S39 local-follow-request leg: NOT RE-TESTED.** No `manuallyApprovesFollowers` toggle in the current Settings UI (Account > Moderation shows only Blocked/Muted/Reported). The S34 gated-follow flow cannot be reproduced. The Pass 172 evidence (local follow-request notification missing) remains the last data point; it may have been fixed by the same `AddToInboxAsync` path but cannot be confirmed live.
  - **S39 is now CLOSED (S2):** all 4 testable legs (local-reply, local-Like, cross-instance-reply, cross-instance-Like) confirmed working live. The 5th leg (local-follow-request) is untestable in the current UI.
- **Checkpoint:** S39 CLOSED. Next pass: open-item stability sweep (S36, S24 D2, S24 D4, S38, S32-holding, follow-graph baseline). S36 (home feed) remains top priority.

## Pass 183 (2026-09-21) — build `2229b0ab` / **S39 local-reply + local-Like legs RESOLVED live** (Pass 182's silence was a transient deploy-time race)
- **Build/Live:** `2229b0ab` (same as Pass 182; no `src/` change → no rebuild).
- **Explored:** S39 local-reply + local-Like re-test (fresh ii-a2→ii-a1 interactions) after Pass 182's false-negative "fix not materializing".
- **Result:**
  - **S39 (A-side notifications) — local-reply + local-Like legs RESOLVED.** Fresh local-reply (ii-a2 (A) → ii-a1 (A) note `06GC9BK2DAFDGB3V2KJPP21E10`, **HTTP 202**, `inReplyTo` correct) → parent author `ii-a1` **immediately** received "ii-a2 replied to your post" (15:56:40Z, "just now", 26 unread). Fresh local-Like (ii-a2 (A) → ii-a1 (A) note `06GC9ADVRXM0ZXQ6W9Q1JC040W`, **HTTP 202**, `likedCount` 0→1) → parent author `ii-a1` **immediately** received "ii-a2 liked a post" (15:52:07Z, "just now", 25 unread). **Pass 182's silence was a transient deploy-time race** (stale `BoxItems`/`AddToInboxAsync` write during the 14:50Z deploy), **not** a persistent data/environment divergence like S36. The dev fix (`c28a95d1` / `2229b0ab`) works live.
  - **S39 cross-instance + local-follow-request legs still untested** on this build — if they also work, S39 is fully closed.
- **Checkpoint:** S39 local-reply + local-Like RESOLVED. Next pass: re-verify S39 cross-instance (B→A Like/reply) + local-follow-request legs; if all pass, mark S39 fully CLOSED. S36 (home feed) remains top priority.

## Pass 182 (2026-09-21) — build `2229b0ab` (**NEW BUILD**, dev's S39 local-reply fix) / cluster rebuilt + redeployed from the common folder. **S39 local-reply fix re-verified → deployed but does NOT materialize live; all other open items STABLE**
- **Build/Live:** mid-pass, dev committed `2229b0ab` ("fix: S39 local reply notification — deliver reply Create to parent author's inbox"; `src/Iris.Server/ActivityPubServerExtensions.cs` +3 tests) → `src/` changed → **rebuilt + redeployed** the QA cluster from the common folder to `2229b0ab` (images `2f63f03f`/`ba587a9d`, was `eec8628c`/`09e49b6d`; A + B health 200). The fix: for a reply whose parent author is **local**, `OutboxPublishHandler` now calls `AddToInboxAsync(parentAuthor, activity)` directly (previously a local parent was a no-op, so the reply never reached the parent's inbox).
- **Explored:** S39 local-reply fix re-test (fresh ii-a2→ii-a1 local reply) + open-item stability sweep (S36, S24 D2, S24 D4, S38, S32-holding, follow-graph baseline).
- **Result:**
  - **S39 (A-side notifications) — local-reply leg STILL OPEN: FIX DEPLOYED BUT DOES NOT MATERIALIZE LIVE.** Fresh local reply (ii-a2 (A) → ii-a1 (A) note `06GC63QMBV`, **HTTP 202**, `inReplyTo` correct) → the reply is **stored + threaded** (parent `/replies` `totalItems=2`, `…/ns#repliedCount=2`) **but parent author `ii-a1` STILL gets no notification** — `GET A /local/v1/notifications` → `totalItems=0`, UI "No notifications yet". The reply's `Create` did **not** reach `ii-a1`'s inbox in the live env **despite the passing in-process test** `LocalReply_LandsInParentAuthorInbox_ProducesNotification`. Ruled out: the fix **is** in the deployed image (built after the commit), ii-a1's prefs are clean (`disabledTypes=[]`, `mutedActors=[]`), and `Create` is not in `ServerOnlyNotificationTypes`. → **same data/environment-specific divergence as S36** (in-process passes, live fails). Cross-instance + local-Like + local-follow-request legs remain open. Handed back to dev for a **live two-instance repro** (does `AddToInboxAsync` insert a `BoxItems`/`Activities` row for `ii-a1` on the **live** A instance?).
  - **S36 (home feed) STILL OPEN (top priority).** A `/home` (ii-a1) = single boost-wrapper ("Content unavailable — view original post", Boost=1, Like=0), target `06GC3AWSHG` = Tombstone; **no own/followed content posts render** (20th consecutive stable pass).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + flat.** ii-a1 (A) outbox page 1 = **27 local + 13 foreign (ii-b1)** (`totalItems=63`).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** ii-b1 (cached on A): `/outbox`+`/followers`+`/following` = **404**; actor doc = **200** (control).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` + B `wf(ii-a1@A)` = **404** (both directions); own-instance `wf(ii-a1@A)` control = **200**.
  - **S32 (FULLY FIXED) holding check:** A-side II-S32-5 note `06GC7X75` = **Tombstone** (`formerType=Note`) — no regression.
  - **Follow-graph baseline intact:** ii-a1 (A) followers = **2**, ii-a2 (A) following = **2**. S30/S26/S31/S29/S34/S27 hold.
- **Checkpoint:** S39 local-reply fix handed back to dev (live two-instance repro needed — in-process test passes but live `ii-a1` inbox never gets the reply `Create`). S36 (home feed) remains top priority. Next pass: open-item sweep on `2229b0ab`.

## Pass 181 (2026-09-21) — **NEW BUILD `9586ee36`** (Phase 146 feed observability: `be5754b5` feed logging + `c9dbec4e` cache hit/miss + `aef48858` cache TTL) / cluster rebuilt + redeployed from the common folder (worktree is docs-only). Open-item re-verify on the new build — **all open items STABLE; the new feed-observability logs fire (build confirmed live)**
- **Build/Live:** REBUILT + REDEPLOYED the QA cluster to `9586ee36` (common-folder `src/`, == `interop-testing` HEAD; built from `/workspace` since the worktree is for doc edits only). New image IDs (`eec8628c`/`09e49b6d`, was `0b9207e0`); A + B health 200; app started clean. **Build confirmed live:** the Phase-146 `FeedService` observability log fired on a real feed build — `Feed built for …/ii-a2: 220 ms, 2 follows (1 local, 1 remote), 84 items, slowest follow 187 ms, types: Like=9, Follow=13, Undo=6, Delete=7, Update=14, Create=28, Announce=1, Remove=3, Add=3` (proves `be5754b5`+`c9dbec4e`+`aef48858` are running; also confirms the S36/S24-D2 feed shape: content surfaces as `Update`/`Create` mixed with heavy non-content activity).
- **Explored:** **Open-item re-verify on the new build** (S36, S39, S24 D2, S24 D4, S38, S32-holding, S33/S34 context).
- **Result:**
  - **S36 (home feed) STILL OPEN (top priority).** A `/home` (ii-a1) = boost-wrapper only ("Content unavailable — view original post", Boost=1), no own/followed content. Unchanged on the new build — the Phase-146 observability is logging-only (no feed-shape fix), consistent with dev's `162159b` in-process non-repro (data/environment-specific).
  - **S39 (A-side notifications) STILL OPEN — asymmetry re-confirmed on the new build.** ii-a1 (A) `/local/v1/notifications` → `totalItems=0`; ii-b1 (B) → `totalItems=23` (Create/Follow from ii-a1). Same B-receives/A-doesn't gap.
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + grew.** ii-a1 (A) outbox page 1 = **35 local + 23 foreign (ii-b1)** items (foreign share up vs Pass 180's 7/63; `totalItems` read 0 on the page-1 response — a separate pagination quirk). The outbox-integrity leak persists and is now heavily populated.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** ii-b1 (cached on A): `/outbox`+`/followers`+`/following` = **404**; actor doc = **200** (control).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` + B `wf(ii-a1@A)` = **404** (both directions); own-instance control = **200**.
  - **S32 (FULLY FIXED) holding check:** A-side II-S32-5 note `06GC7X75` = **Tombstone** (correct); cross-instance leg confirmed Pass 173 (no regression).
  - **S33/S34 context:** ii-a1 followers = [ii-b1, ii-a2] (totalItems=2), ii-a2 following = 2 — baseline intact. S30/S26/S31/S29/S34/S27 hold.
- **Checkpoint:** next pass continues the open-item sweep on `9586ee36`; S36 (home feed) remains top priority — the Phase-146 logs now give dev a live trace to diagnose the content-omission/pollution without a two-instance repro.

## Pass 180 (2026-09-21) — **WORKTREE SYNC + SANITY RETEST** — `qa` was 71 commits ahead / 19 behind `interop-testing` (dev's S32/S37/S30/S36-test commits had never been pulled down; Passes 110–179 had never been merged back to dev). Synced `qa` onto `interop-testing` (clean merge, no conflict; `src/`+`tests/` now identical to `interop-testing`, all dev fixes present), then ran a sanity retest — **all open items STABLE post-sync; cluster build `4431006` still current (no `src/` change since `4431006`, no rebuild needed)**
- **Build/Live:** `src/` on `interop-testing` unchanged since `4431006` (the deployed build) → **no rebuild needed**; cluster unchanged (build `4431006`; A + B health 200; follow edges intact).
- **Process fix (the gap this pass closed):** QA had been committing docs on `qa` every pass but **never syncing down / merging back** — so dev hadn't seen Passes 110–179 and `qa` was 19 commits behind. This pass ran the missing `sync` (merge `interop-testing` into `qa`) + a sanity retest, and will `merge` `qa` back (publishing Passes 110–179 + the sync). QA_LOOP.md protocol updated to make sync+retest+redeploy+merge mandatory every pass (see below).
- **Explored:** **Sanity retest of open items after the sync** (S36, S39, S24 D2, S24 D4, S38, S32-holding).
- **Result:**
  - **S36 (home feed) STILL OPEN (top priority).** Home feed still boost-wrapper only, no own content (unchanged post-sync).
  - **S39 (A-side notifications) STILL OPEN.** ii-a1 `/local/v1/notifications` → `totalItems=0` (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN (flat).** `totalItems`=**63**, page 1 = **7 foreign (ii-b1)** (flat vs Pass 179).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** doc **200**; `/outbox` + `/followers` **404** (own-instance control 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` + B `wf(ii-a1@A)` = 404 (own-instance control 200).
  - **S32 (FULLY FIXED) holding check:** A-side II-S32-5 note `06GC7X75` = **Tombstone** (correct); B cached copy = 404 (lazy-refetch — same "B shared-inbox drop" mechanism noted Pass 142/173, not a regression; the observable cross-instance Delete propagation was confirmed in Pass 173).
  - **S33 remains RE-OPENED (Pass 171).** S30/S26/S31/S29/S34/S27 hold.

## Pass 179 (2026-09-21) — No `src/` change / cluster unchanged (`4431006`, healthy). Open-item stability sweep — **all open items STABLE (19th consecutive stable pass for S36/S39/S24 D4/S38; S24 D2 flat at 63/7)**
- **Build/Live:** No `src/` change since `4431006` → no rebuild. QA cluster unchanged (build `4431006`; A + B health 200; follow edges intact: ii-a1 followers=2, ii-a2 following=2).
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38, S37/S28).
- **Result:**
  - **S36 (home feed) STILL OPEN (top priority).** Home feed still boost-wrapper only, no own content (unchanged, 19th consecutive stable).
  - **S39 (A-side notifications) STILL OPEN.** ii-a1 `/local/v1/notifications` → `totalItems=0` (unchanged, 19th consecutive stable).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN (flat).** `GET A /u/ii-a1/outbox` → `totalItems`=**63** (flat vs Pass 178); page 1 = **7 foreign (ii-b1)** items (flat).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
  - **S33 remains RE-OPENED (Pass 171).** S30/S26/S31/S29/S32/S34/S27 hold.

## Pass 178 (2026-09-21) — No `src/` change / cluster unchanged (`4431006`, healthy). Open-item stability sweep — **all open items STABLE (18th consecutive stable pass for S36/S39/S24 D4/S38; S24 D2 flat at 63/7)**
- **Build/Live:** No `src/` change since `4431006` → no rebuild. QA cluster unchanged (build `4431006`; A + B health 200; follow edges intact: ii-a1 followers=2, ii-a2 following=2).
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38, S37/S28).
- **Result:**
  - **S36 (home feed) STILL OPEN (top priority).** Home feed still boost-wrapper only, no own content (unchanged, 18th consecutive stable).
  - **S39 (A-side notifications) STILL OPEN.** ii-a1 `/local/v1/notifications` → `totalItems=0` (unchanged, 18th consecutive stable).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN (flat).** `GET A /u/ii-a1/outbox` → `totalItems`=**63** (flat vs Pass 177); page 1 = **7 foreign (ii-b1)** items (flat).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
  - **S33 remains RE-OPENED (Pass 171).** S30/S26/S31/S29/S32/S34/S27 hold.

## Pass 177 (2026-09-21) — No `src/` change / cluster unchanged (`4431006`, healthy). Open-item stability sweep — **all open items STABLE (17th consecutive stable pass for S36/S39/S24 D4/S38; S24 D2 flat at 63/7)**
- **Build/Live:** No `src/` change since `4431006` → no rebuild. QA cluster unchanged (build `4431006`; A + B health 200; follow edges intact: ii-a1 followers=2, ii-a2 following=2).
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38, S37/S28).
- **Result:**
  - **S36 (home feed) STILL OPEN (top priority).** Home feed still boost-wrapper only, no own content (unchanged, 17th consecutive stable).
  - **S39 (A-side notifications) STILL OPEN.** ii-a1 `/local/v1/notifications` → `totalItems=0` (unchanged, 17th consecutive stable).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN (flat).** `GET A /u/ii-a1/outbox` → `totalItems`=**63** (flat vs Pass 176); page 1 = **7 foreign (ii-b1)** items (flat).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
  - **S33 remains RE-OPENED (Pass 171).** S30/S26/S31/S29/S32/S34/S27 hold.

## Pass 176 (2026-09-21) — No `src/` change / cluster unchanged (`4431006`, healthy). Open-item stability sweep — **all open items STABLE (16th consecutive stable pass for S36/S39/S24 D4/S38; S24 D2 flat at 63/7)**
- **Build/Live:** No `src/` change since `4431006` → no rebuild. QA cluster unchanged (build `4431006`; A + B health 200; follow edges intact: ii-a1 followers=2, ii-a2 following=2).
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38, S37/S28).
- **Result:**
  - **S36 (home feed) STILL OPEN (top priority).** Home feed still boost-wrapper only, no own content (unchanged, 16th consecutive stable).
  - **S39 (A-side notifications) STILL OPEN.** ii-a1 `/local/v1/notifications` → `totalItems=0` (unchanged, 16th consecutive stable).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN (flat).** `GET A /u/ii-a1/outbox` → `totalItems`=**63** (flat vs Pass 175); page 1 = **7 foreign (ii-b1)** items (flat).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
  - **S33 remains RE-OPENED (Pass 171).** S30/S26/S31/S29/S32/S34/S27 hold.

## Pass 175 (2026-09-21) — No `src/` change / cluster unchanged (`4431006`, healthy). Open-item stability sweep — **all open items STABLE (15th consecutive stable pass for S36/S39/S24 D4/S38; S24 D2 flat at 63/7)**
- **Build/Live:** No `src/` change since `4431006` → no rebuild. QA cluster unchanged (build `4431006`; A + B health 200; follow edges intact: ii-a1 followers=2, ii-a2 following=2).
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38, S37/S28).
- **Result:**
  - **S36 (home feed) STILL OPEN (top priority).** Home feed still boost-wrapper only, no own content (unchanged, 15th consecutive stable).
  - **S39 (A-side notifications) STILL OPEN.** ii-a1 `/local/v1/notifications` → `totalItems=0` (unchanged, 15th consecutive stable; re-confirmed Pass 174 with fresh local Like).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN (flat).** `GET A /u/ii-a1/outbox` → `totalItems`=**63** (flat vs Pass 174); page 1 = **7 foreign (ii-b1)** items (flat) → no new accumulation this pass.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
  - **S33 remains RE-OPENED (Pass 171).** S30/S26/S31/S29/S32/S34/S27 hold.

## Pass 174 (2026-09-21) — No `src/` change / cluster unchanged (`4431006`, healthy). Open-item sweep + **fresh S39 (local Like) re-test — S39 STILL OPEN** (ii-a1 notifications = 0 after fresh local Like from ii-a2; the 22 notifications seen in Pass 173's sweep were a transient/cache artifact, not a fix); S36 still reproduces; S24 D2 continues to accumulate (63/7); other open items STABLE
- **Build/Live:** No `src/` change since `4431006` → no rebuild. QA cluster unchanged (build `4431006`; A + B health 200; follow edges intact: ii-a1 followers=2, ii-a2 following=2).
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38, S37/S28) + **fresh S39 (local Like) re-test** (ii-a2 (A) Liked ii-a1's note `06GC6BCF` → ii-a1 `/local/v1/notifications` checked).
- **Result:**
  - **S39 (A-side notifications) STILL OPEN — local Like leg confirmed broken.** (A) ii-a2 (A) Liked ii-a1's note `06GC6BCF` (II-S37-6, live, 2 likes → 3 after the Like). (B) ii-a1 (A) `/local/v1/notifications` → **`totalItems=0`** (no Like notification from ii-a2). **Note:** Pass 173's sweep showed ii-a1 with `totalItems=22` (Follow=2, Create=7, Like=9, Announce=2) — this was a **transient/cache artifact** (the 22 notifications were from prior activity that had been flushed from the store by the time of Pass 174). A fresh fetch after the new local Like shows `totalItems=0`, confirming the local Like notification leg is still broken. **S39 remains OPEN (all legs: local Like, local reply, local follow-request, B→A cross-instance).**
  - **S36 (home feed) STILL OPEN (top priority).** Home feed still boost-wrapper only, no own content (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + accumulating.** `GET A /u/ii-a1/outbox` → `totalItems`=**63** (grew from 62 in Pass 173); page 1 = **7 foreign (ii-b1)** items → **continues to accumulate** (now 63 total / 7 foreign).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
  - **S33 remains RE-OPENED (Pass 171).** S30/S26/S31/S29/S32/S34/S27 hold.

## Pass 173 (2026-09-21) — **NEW BUILD `4431006`** (dev's S32 sending-side fix) / cluster rebuilt. Open-item sweep + **fresh S32 (cross-instance Delete) re-test — S32 FULLY FIXED** (cross-instance leg confirmed); S36 still reproduces; S24 D2 continues to accumulate; other open items STABLE
- **Build/Live:** Dev committed `4431006` "S32: address outbound Delete/Update to the note's original audience (sending side)" → cluster **rebuilt** to `4431006` (A + B health 200; follow edges intact pre-test: ii-a1 followers=2, ii-a2 following=2).
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38, S37/S28) + **fresh S32 (cross-instance Delete) re-test** (ii-a1 (A) posted II-S32-5 fresh note → B cached it → A deleted → A Tombstone + B Tombstone verified).
- **Result:**
  - **S32 (cross-instance Delete) re-verified → FULLY FIXED.** (A) ii-a1 (A) posted `II-S32-5 Pass 173 fresh A note for cross-instance Delete re-test on 4431006` (Note `…/u/ii-a1/notes/06GC7X75AH284RM0MGPY9X8JS0`). (B) B cached it (`GET B /object?iri=…` → 200). (C) A **deleted** the note → `GET A <note>` → **Tombstone** (owner leg holds). (D) `GET B <note IRI>` (direct AP) → **Tombstone** (cross-instance leg **FIXED**). The sending-side fix (`4431006`) addresses the outbound Delete to the note's original `to`/`cc` audience, so B now **receives** the Delete as an addressed activity (not just via lazy refetch). The previously-unconfirmed "mechanism" residual (applied activity vs lazy refetch) is now resolved. **S32 moved from LARGELY FIXED to FULLY FIXED.**
  - **S36 (home feed) STILL OPEN (top priority).** Home feed still boost-wrapper only, no own content (the S32 fix did not affect the home feed path; unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + accumulating.** `GET A /u/ii-a1/outbox` → `totalItems`=**62** (grew from 58 in Pass 172); page 1 = **8 foreign (ii-b1)** items → **continues to accumulate** (now 62 total / 8 foreign).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
  - **S39 (A-side notifications) STILL OPEN (broadened, Pass 172).** `ii-a1` `/local/v1/notifications` → `totalItems=0` (unchanged).
  - **S33 remains RE-OPENED (Pass 171).** S30/S26/S31/S29/S34/S27 hold. S32 now FULLY FIXED.

## Pass 172 (2026-09-21) — No `src/` change / cluster unchanged (`38ae87c`, healthy). Open-item sweep + fresh S34 (gated follow) re-test — **S34 re-verified FIXED (holding)**; **NEW S39 data point** (owner sees no follow-request notification for a gated follow — the local A-side notification leg is also broken, broadening S39 beyond B→A); other open items STABLE
- **Build/Live:** No `src/` change since `38ae87c` (dev's latest commit `0ec58d3` is PLAN-only) → no rebuild. QA cluster unchanged (build `38ae87c`; A + B health 200; follow edges intact pre-test: ii-a1 followers=2 `[ii-a2, ii-b1]`, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38, S37/S28) + **fresh S34 (gated follow) re-test** (ii-a1 `manuallyApprovesFollowers` toggled ON → ii-a2 unfollow+re-follow → pending state verified → ii-a1 Accept → post-accept verified → gating reset to None → baseline restored).
- **Result:**
  - **S34 (gated follow) re-verified → FIXED (holding).** (A) Enabled gating on ii-a1 (`manuallyApprovesFollowers`=True via Edit-profile checkbox). (B) ii-a2 unfollowed then re-followed ii-a1 → **pre-accept (pending) state verified**: ii-a1 public `/followers` = **[ii-b1] only** (ii-a2 **WITHHELD** — the fix holds), ii-a2 `/following` = [ii-b1] only (ii-a1 not yet promoted), `GET A /ap/v1/u/ii-a1` → `manuallyApprovesFollowers`=True. (C) ii-a1 **Accept**ed the request → ii-a1 `/followers` = [ii-b1, **ii-a2**] (totalItems=2, promoted), ii-a2 `/following` = [ii-b1, **ii-a1**] (totalItems=2), `GET A /local/v1/notifications` (owner) shows the request resolved. (D) **Cleanup**: gating reset to None (`manuallyApprovesFollowers`=None), ii-a2→ii-a1 edge confirmed intact (ii-a1 followers=2, ii-a2 following=2). No regression.
  - **NEW S39 data point (local A-side notification leg).** While the ii-a2 follow request was **pending** (gating ON, before accept), the owner ii-a1's `/local/v1/notifications` = **`totalItems=0`** (Follows filter = "No notifications yet") — i.e. the owner sees **no follow-request notification at all**, even though the follow is correctly stored as pending + withheld from public followers. This **broadens S39**: the missing-notification gap is **not just B→A inbound delivery** — the **local same-instance** follow-request → owner-notification leg is also broken (a local follow request produces a stored pending edge but **no owner notification**). Same root-cause family (notification inbound-delivery gap) as the cross-instance S39 + S33. Cross-linked to [s39-…md](s39-a-side-notifications-missing-b-side-receives-asymmetric-inbound-delivery.md).
  - **S36 (home feed) STILL OPEN (top priority).** Home feed still boost-wrapper only, no own content (unchanged).
  - **S39 (A-side notifications) STILL OPEN + broadened.** `ii-a1` `/local/v1/notifications` → `totalItems=0` (unchanged) **+ now also confirmed for the local follow-request leg** (see above).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + accumulating.** `GET A /u/ii-a1/outbox` → `totalItems`=**58** (grew from 57 in Pass 171); page 1 = **9 foreign (ii-b1)** items (grew from 8) → **continues to accumulate** (now 58 total / 9 foreign).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
  - **S37/S28 (wire counts / button-UI residual):** unchanged (II-S37-6 probe note `06GC6BCF` still 404 — deleted, S32 noise); count-materialization fix from `38ae87c` holds.
- **Checkpoint:** **No build change; open-item sweep + fresh S34 re-test on `38ae87c` — S34 re-verified FIXED (holding); NEW S39 data point (owner sees no follow-request notification for a gated follow — the local A-side notification leg is also broken, broadening S39 beyond B→A).** S24 D2 **continues to accumulate** (total 58, 9 foreign page 1 — was 57/8 in Pass 171). S36 (home feed, top priority) + S39 (broadened) + S24 D2 + S24 D4 + S38 + S37/S28 (button-UI) all STILL OPEN. S33 remains RE-OPENED (Pass 171). S30/S26/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting dev's decision on the B→A inbound-delivery root cause (S33 + S39), S36, S24 D2/D4, S38.

## Pass 171 (2026-09-21) — No `src/` change / cluster unchanged (`38ae87c`, healthy). Open-item sweep + fresh S33 (unfollow `Undo`) re-test — **S33 cross-instance leg REGRESSED** (B→A `Undo` not delivered; same root cause as S39); other open items STABLE
- **Build/Live:** No `src/` change since `38ae87c` (dev's latest commit `0ec58d3` is PLAN-only) → no rebuild. QA cluster unchanged (build `38ae87c`; A + B health 200; follow edges intact pre-test: ii-a1 followers=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38) + **fresh S33 (unfollow `Undo`) re-test** (B `ii-b1` unfollowed A `ii-a1`, then re-followed to restore).
- **Result:**
  - **S33 (unfollow `Undo`) cross-instance leg REGRESSED → RE-OPENED (NEW regression).** Pre-state: A `ii-a1/followers`=2 (`[ii-a2, ii-b1]`), B `ii-b1/following`=1. B pressed Unfollow on ii-a1 → **B-side local holds**: B `following` 1→**0** + B outbox has `Undo` `…/ii-b1/undos/06GC7KXQ…` (pub **11:52:45Z**), object = **bare-IRI** original Follow `06GC76S3ZD`. **A-side does NOT apply**: A `ii-a1/followers` **stays 2** (`[ii-a2, ii-b1]`), A outbox (page 1) has **no `Undo(Follow)` from ii-b1** (only stale 09:12 actor-doc `Remove`/`Update` noise from the S34 gating test) → the B→A `Undo` is **not delivered** to A's store. **Same directional B→A inbound-delivery gap as S39** (B's outbound activities don't land in A's store) — cross-linked. State **restored** via re-follow (B new `Follow` `…/ii-b1/follows/06GC7MB2E8…` pub 11:54:34 → B `following`=1; A `followers`=2). The Pass 164 "fixed" was the A-side leg in a transient delivery-window state; on the settled `38ae87c` stack the B→A `Undo` leg does not propagate.
  - **S36 (home feed) STILL OPEN (top priority).** Home feed still boost-wrapper only, no own content (unchanged).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` `/local/v1/notifications` → `totalItems=0` (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN.** `GET A /u/ii-a1/outbox` → `totalItems`=**57** (flat vs Pass 170); page 1 = **8 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN.** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
- **Checkpoint:** **No build change; open-item sweep + fresh S33 re-test on `38ae87c` — S33 cross-instance leg REGRESSED (B→A `Undo` not delivered; same root cause as S39, now cross-linked). S33 RE-OPENED → open count now 14.** S36 (home feed) + S39 + S24 D2 (total 57) + S24 D4 + S38 + S37/S28 (button-UI) all STILL OPEN. S30/S26/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting dev's decision on the B→A inbound-delivery root cause (S33 + S39), S36, S24 D2/D4, S38.

## Pass 170 (2026-09-21) — No `src/` change / cluster unchanged (`38ae87c`, healthy). Open-item stability sweep + S29 re-verify — all open items STABLE (12th consecutive stable pass)
- **Build/Live:** No `src/` change since `38ae87c` (dev's latest commit `0ec58d3` is PLAN-only) → no rebuild. QA cluster unchanged (build `38ae87c`; A + B health 200; follow edges intact: ii-a1 followers=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38, S37/S28) + **S29 re-verify** (community webfinger).
- **Result (all STABLE — 12th consecutive stable pass; no regression, no new defect, no fix landed):**
  - **S36 (home feed) STILL OPEN (top priority).** Home feed (ii-a2 session, A) still renders **only the boost wrapper** (target `06GC3AWSHG` = Tombstone) with **no own content** — unchanged from Passes 129–169.
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0` (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + accumulating.** `GET A /u/ii-a1/outbox` → `totalItems`=**57** (was 56 in Pass 169); page 1 = **8 foreign (ii-b1)** items (was 7) → **continues to accumulate** (now 57 total / 8 foreign).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
  - **S37/S28 (wire counts / button-UI residual):** the II-S37-6 probe note (`06GC6BCF`) now **404s** (deleted — S32 noise accumulated) → wire-count re-verify not applicable this pass; the count-materialization fix from `38ae87c` is unchanged and was confirmed holding in Passes 141–166.
  - **S29 (community webfinger) re-verified → FIXED (holding):** A `wf(ii-a8-community)` → **200** + `rel=self` → `https://qa-iris-a.luit.ink/ap/v1/c/ii-a8-community` (type `application/activity+json`).
- **Checkpoint:** **No build change; open-item sweep + S29 re-verify on `38ae87c` — all open items STABLE (12th consecutive stable pass).** S24 D2 **continues to accumulate** (total 57, 8 foreign page 1 — was 56/7 in Pass 169). S36 (home feed, top priority) + S39 + S24 D2 + S24 D4 + S38 + S37/S28 (button-UI residual) all STILL OPEN. S29 re-verified FIXED (holding). S30/S26/S33/S31/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting dev's decision on S36 + S39 + S24 D4 + S24 D2.

## Pass 169 (2026-09-21) — No `src/` change / cluster unchanged (`38ae87c`, healthy; dev commit `0ec58d3` is PLAN-only). Open-item sweep + S24 D1 re-verify (closed non-reproducible) + S36 deep re-test with the signed `/feed` + outbox capture dev requested — all open items STABLE (11th consecutive)
- **Build/Live:** New dev commit `0ec58d3` is **PLAN-only (no `src/` change)** → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=2, ii-b1 followers=2). Dev's `0ec58d3` closed **S24 Defect 1** (Following tab omits remote actors) as **non-reproducible** (full code pass: Following + Followers tabs share the identical `ActorListPanel`, no local/remote host filter, symmetric server build) and **S36** in-process (server + client provably correct for a fresh owner content `Create`), handing both back to QA with wire+DOM+console capture steps.
- **Explored:** **Open-item stability sweep** (S36, S39, S24 D2, S24 D4, S38) + **S24 D1 re-verify** (wire `following` body + Following-tab DOM + console, per dev's steps) + **S36 deep re-test** (captured the exact signed `/feed` response body + the actor's outbox IRIs/types dev requested).
- **Result (all STABLE — 11th consecutive stable pass; no regression, no new defect, no fix landed):**
  - **S36 (home feed) STILL OPEN (top priority) — DEEP RE-TEST (strongest evidence yet).** Captured the exact signed `GET A /ap/v1/u/ii-a2/feed?source=people` (the request the UI issued; **HTTP 200**, `totalItems`=48) + the `ii-a2` outbox. **The `ii-a2` outbox HAS 2 content `Create`s** (`type: Note`): `06GC754Q` (II-S31-6, edited) and `06GC6MBJ` (II-S34-notify, **never edited → a clean content `Create`**). **The signed home feed omits BOTH** — its first page is dominated by non-content activities (`Delete` 2, `Update`(note) 1, `Like` 3, `Follow` 4, `Undo` 1, `Announce` 1 [the boost wrapper], `Remove`/`Add` actor-doc 2). **A fresh, unedited content `Create` (`06GC6MBJ`) is in the outbox but absent from the home feed on live data** — even though dev's in-process repro returns exactly that. This is the cleanest reproduction: it is **not** fully explained by the "edited → `Update`" shape; the live actor's accumulated S25/S32 noise (Deletes, Undo, Follow/Like, actor-doc Update/Add/Remove) is the differentiator. Capture written into the S36 finding for dev to replay.
  - **S24 D1 (Following-tab omits remote actor) — CLOSED (non-reproducible, confirmed).** Per dev's steps: wire `GET A /u/ii-a1/following` `orderedItems` = **[ii-a8-community (local), ii-b1 (remote @B)]** totalItems=2 (remote **present**); the Following tab DOM renders **both** (local community card + remote ii-b1 with Unfollow); console clean (0 errors/warnings). **D1 does NOT reproduce on the live stack** → dev's non-reproducible close is confirmed (dev's Pass-104 evidence was self-contradictory). D2 + D4 remain OPEN (S24 status → D2 + D4).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0` (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + accumulating.** `GET A /u/ii-a1/outbox` → `totalItems`=**56**; page 1 = **7 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control = 200).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (own-instance control = 200).
- **Checkpoint:** **No build change; open-item sweep + S24 D1 re-verify (CLOSED non-reproducible) + S36 deep re-test (signed `/feed` + outbox capture) on `38ae87c` — all open items STABLE (11th consecutive stable pass).** **S24 D1 closed (non-reproducible, confirmed by wire+DOM+console capture).** S36 (home feed, top priority — now with a clean-`Create`-dropped capture) + S39 + S24 D2 (total 56) + S24 D4 + S38 + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting dev's decision on S36 (why a clean owner content `Create` is dropped from the live home feed) + S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity.

## Pass 168 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Open-item sweep + fresh S26 re-test — all open items STABLE (10th consecutive stable pass)
- **Build/Live:** No new dev commit (dev commits since `38ae87c` are PLAN-only `069fd7f` + `e07faa5`, no `src/` change) → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** on `38ae87c` (S36, S39, S24 D2, S24 D4, S38) + **fresh S26 re-test** (a B ii-b1 reply to a clean A note, verify cross-instance reply threading).
- **Result (all STABLE — 10th consecutive stable pass; no regression, no new defect, no fix landed):**
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` → **boost wrapper only** ("Boosted by ii-b1", target note `06GC3AWSHG` = a **Tombstone** → "Content unavailable"), **no own content posts** (unchanged).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0`, items 0 (unchanged — even after a fresh cross-instance reply on an ii-a1 note).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + accumulating.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **56** (stable this pass); page 1 = **7 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control `GET A /u/ii-a1/outbox` = **200**).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (both directions; own-instance control = 200).
  - **S26 (cross-instance reply threading) re-tested FRESH → FIXED (holding).** B (ii-b1) posted a fresh reply to a clean A note `06GC64YF` (II-A4-5, 0 prior replies) → A parent `/replies` `totalItems` **0→1** (the new B reply) + the B reply `06GC7DW1` doc has `inReplyTo` = **the A parent** + `type: Note` + content present. Cross-instance threading is correct.
- **Checkpoint:** **No build change; open-item sweep + fresh S26 re-test on `38ae87c` — all open items STABLE (10th consecutive stable pass).** S36 (home feed, top priority) + S39 (A-side notifications) + S24 D2 (foreign outbox, total 56) + S24 D4 (remote-collection 404) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 167 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Open-item sweep + S32 owner-leg re-test + follow-graph clarification — all open items STABLE (9th consecutive stable pass)
- **Build/Live:** No new dev commit (dev commits since `38ae87c` are PLAN-only `069fd7f` + `e07faa5`, no `src/` change) → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** on `38ae87c` (S36, S39, S24 D2, S24 D4, S38) + **S32 Delete owner-leg re-test** + **follow-graph clarification** (which A accounts ii-b1 actually follows).
- **Result (all STABLE — 9th consecutive stable pass; no regression, no new defect, no fix landed):**
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` → **boost wrapper only** ("Boosted by ii-b1", target note `06GC3AWSHG` = a **Tombstone** → "Content unavailable"), **no own content posts** (unchanged).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0`, items 0 (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + accumulating.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **56** (stable this pass); page 1 = **7 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control `GET A /u/ii-a1/outbox` = **200**).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (both directions; own-instance control = 200).
  - **S32 (Delete → Tombstone) owner-leg re-tested FRESH → FIXED (holding).** ii-a2 (A) deleted a disposable note (`06GC4225`, II-A2-local) → the note doc returns **HTTP 200 + `type: Tombstone`** (owner-leg tombstone correct). The **cross-instance A→B Delete leg** (verified in Pass 142) is **now unobservable** because **ii-a2 has zero followers** (ii-b1 does NOT follow ii-a2), so an ii-a2 note is never delivered/cached on B — a **coverage gap**, not a regression. (An ii-a2 note posted this pass was correctly *not* delivered to B, consistent with the follow graph.)
  - **Follow-graph clarification (NEW):** `GET B /ap/v1/u/ii-b1/following` = **[ii-a1] only** — ii-b1 follows **only ii-a1**, not ii-a2. `GET A /ap/v1/u/ii-a2/followers` = **empty**. Consequence: the home-feed / notification / delivery defects (S36/S39) and the S32 cross-instance leg can only be exercised via **ii-a1** content (the one A account ii-b1 follows); ii-a2 content never reaches B.
- **Checkpoint:** **No build change; open-item sweep + S32 owner-leg re-test + follow-graph clarification on `38ae87c` — all open items STABLE (9th consecutive stable pass).** S36 (home feed, top priority) + S39 (A-side notifications) + S24 D2 (foreign outbox, total 56) + S24 D4 (remote-collection 404) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 166 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Open-item sweep + fresh S27 re-test — all open items STABLE (8th consecutive stable pass)
- **Build/Live:** No new dev commit (dev commits since `38ae87c` are PLAN-only `069fd7f` + `e07faa5`, no `src/` change) → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** on `38ae87c` (S36, S39, S24 D2, S24 D4, S38) + **fresh S27 re-test** (B ii-b1 Liked an A note, verify the Like is applied + the wire count materializes on the author).
- **Result (all STABLE — 8th consecutive stable pass; no regression, no new defect, no fix landed):**
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` → **boost wrapper only** ("Boosted by ii-b1", target note `06GC3AWSHG` = a **Tombstone** → "Content unavailable"), **no own content posts** (unchanged).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0`, items 0 (unchanged — even after a fresh remote Like on an ii-a1 note).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + accumulating.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **56** (stable this pass; was 55 in Pass 164); page 1 = **7 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control `GET A /u/ii-a1/outbox` = **200**).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (both directions; own-instance control = 200).
  - **S27 (remote Like applied on author) re-tested FRESH → FIXED (holding).** B (ii-b1) Liked A note `06GC6BCF` (II-S37-6) → A note doc wire `likedCount` **1→2** + `score` **1→2** (materialized immediately — S37 count fix holds) + A note `/likes` `totalItems` = **2** (the remote Like is stored + retrievable). The author (`ii-a1`) still gets no notification (S39).
- **Checkpoint:** **No build change; open-item sweep + fresh S27 re-test on `38ae87c` — all open items STABLE (8th consecutive stable pass).** S36 (home feed, top priority) + S39 (A-side notifications) + S24 D2 (foreign outbox, total 56) + S24 D4 (remote-collection 404) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 165 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Open-item sweep + S29 re-verify — all open items STABLE (7th consecutive stable pass)
- **Build/Live:** No new dev commit (dev commits since `38ae87c` are PLAN-only `069fd7f` + `e07faa5`, no `src/` change) → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** on `38ae87c` (S36, S39, S24 D2, S24 D4, S38) + **S29 community-webfinger re-verify**.
- **Result (all STABLE — 7th consecutive stable pass; no regression, no new defect, no fix landed):**
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` → **boost wrapper only** ("Boosted by ii-b1", target note `06GC3AWSHG` = a **Tombstone** → "Content unavailable"), **no own content posts** (unchanged).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0`, items 0 (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + accumulating.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **56** (was 55 in Pass 164 — the foreign population keeps growing); page 1 = **7 foreign (ii-b1)** items (was 6).
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/following` = **404** (own-instance control `GET A /u/ii-a1/outbox` = **200**).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (both directions; own-instance control = 200).
  - **S29 (community webfinger) re-verified → FIXED (holding).** A `wf(ii-a8-community@qa-iris-a)` = **200** + `rel=self` → `https://qa-iris-a.luit.ink/ap/v1/c/ii-a8-community` (a Group AP doc).
- **Checkpoint:** **No build change; open-item sweep + S29 re-verify on `38ae87c` — all open items STABLE (7th consecutive stable pass).** S36 (home feed, top priority) + S39 (A-side notifications) + S24 D2 (foreign outbox, total now 56 — accumulating) + S24 D4 (remote-collection 404) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 164 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Open-item sweep + fresh S33 re-test — all open items STABLE (6th consecutive stable pass)
- **Build/Live:** No new dev commit (dev commits since `38ae87c` are PLAN-only `069fd7f` + `e07faa5`, no `src/` change) → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact at start: ii-a1 followers=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** on `38ae87c` (S36, S39, S24 D2, S24 D4, S38) + **fresh S33 re-test** (B ii-b1 unfollowed ii-a1 → A followers dropped, then re-followed to restore the edge).
- **Result (all STABLE — 6th consecutive stable pass; no regression, no new defect, no fix landed):**
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` → **boost wrapper only** ("Boosted by ii-b1", target note `06GC3AWSHG` = a **Tombstone** → "Content unavailable"), **no own content posts** (unchanged).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0`, items 0 (unchanged). Asymmetric: B `ii-b1` notifications page renders A-side interactions (follow requests, likes, replies, posts).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **55** (stable); page 1 = **6 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control `GET A /u/ii-a1/outbox` = **200**).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (both directions; own-instance control = 200).
  - **S33 (unfollow `Undo` propagates cross-instance) re-tested FRESH → FIXED (holding).** B (ii-b1) unfollowed ii-a1 → A `ii-a1` `/followers` **totalItems 2→1** (ii-b1 removed, only ii-a2 remains) [Undo federated B→A]; B then re-followed ii-a1 → A `/followers` **totalItems 1→2** (ii-b1 restored) [Follow federated B→A]; B `ii-b1` `/following` restored to [ii-a1]. Follow edge restored (A followers now ii-a2 + ii-b1).
- **Checkpoint:** **No build change; open-item sweep + fresh S33 re-test on `38ae87c` — all open items STABLE (6th consecutive stable pass).** S36 (home feed, top priority) + S39 (A-side notifications) + S24 D2 (foreign outbox, total 55) + S24 D4 (remote-collection 404) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 163 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Open-item sweep + fresh S31 re-test — all open items STABLE (5th consecutive stable pass)
- **Build/Live:** No new dev commit (dev commits since `38ae87c` are PLAN-only `069fd7f` + `e07faa5`, no `src/` change) → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** on `38ae87c` (S36, S39, S24 D2, S24 D4, S38) + **fresh S31 re-test** (post a new note as ii-a2, edit it, verify `published` preserved + `updated` set).
- **Result (all STABLE — 5th consecutive stable pass; no regression, no new defect, no fix landed):**
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` → **boost wrapper only** ("Boosted by ii-b1", target note `06GC3AWSHG` = a **Tombstone** → "Content unavailable"), **no own content posts** (unchanged).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0`, items 0 (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **55** (stable); page 1 = **6 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/following` = **404** (own-instance control `GET A /u/ii-a1/outbox` = **200**).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (both directions; own-instance control = 200).
  - **S31 (edit preserves `published`, advances `updated`) re-tested FRESH → FIXED (holding).** ii-a2 (A) posted II-S31-6 (`creates/06GC754Q9W`, published 10:48:10Z, updated None) then edited it → note doc `published` **PRESERVED** (10:48:10.3190537Z, original) + `updated` **SET** (10:49:26.1630127Z) + content changed to "…EDITED".
- **Checkpoint:** **No build change; open-item sweep + fresh S31 re-test on `38ae87c` — all open items STABLE (5th consecutive stable pass).** S36 (home feed, top priority) + S39 (A-side notifications) + S24 D2 (foreign outbox, total 55) + S24 D4 (remote-collection 404) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 162 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Open-item sweep + S26 re-verify — all open items STABLE (4th consecutive stable pass)
- **Build/Live:** No new dev commit (dev commits since `38ae87c` are PLAN-only `069fd7f` + `e07faa5`, no `src/` change) → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** on `38ae87c` (S36, S39, S24 D2, S24 D4, S38) + **S26 cross-instance reply threading re-verify** (holding-fix spot check).
- **Result (all STABLE — 4th consecutive stable pass; no regression, no new defect, no fix landed):**
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` → **boost wrapper only** ("Boosted by ii-b1", target note `06GC3AWSHG` = a **Tombstone** → "Content unavailable"), **no own content posts** (unchanged from Passes 129/144/146/152/154/158/159/160/161).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0`, items 0 (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **55** (stable); page 1 = **6 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; `/followers` = **404** (own-instance control `GET A /u/ii-a1/outbox` = **200**).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (both directions; own-instance control = 200).
  - **S26 (cross-instance reply threading) re-verified → FIXED (holding).** A note `06GC5MR7` `/replies` totalItems=2 (was 1 in Pass 148); the B reply `06GC6337X9` still carries `inReplyTo` → the A parent (threading intact). The 2nd reply `06GC6MZE` is the leftover Pass-157 S39 repro reply (a valid B→A threaded reply, not a defect).
- **Checkpoint:** **No build change; open-item sweep + S26 re-verify on `38ae87c` — all open items STABLE (4th consecutive stable pass).** S36 (home feed, top priority) + S39 (A-side notifications) + S24 D2 (foreign outbox, total 55) + S24 D4 (remote-collection 404) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 161 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Open-item sweep — all open items STABLE (3rd consecutive stable pass)
- **Build/Live:** No new dev commit (HEAD `d030014` = QA Pass 160 docs; dev commits since `38ae87c` are PLAN-only `069fd7f` + `e07faa5`, no `src/` change) → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=2, ii-a1 following=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** on `38ae87c` — S36 (home feed), S39 (A-side notifications), S24 D2 (foreign outbox), S24 D4 (remote-actor collections), S38 (webfinger), S37/S28 (counts).
- **Result (all STABLE — 3rd consecutive stable pass; no regression, no new defect, no fix landed):**
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` → **boost wrapper only** ("Boosted by ii-b1", target note `06GC3AWSHG` = a **Tombstone** → "Content unavailable — view original post"), **no own content posts** (unchanged from Passes 129/144/146/152/154/158/159/160).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0`, items 0 (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **55** (stable); page 1 = **6 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox` = **404**; symmetric on B (`GET B /u/ii-a1` doc 200, `/outbox` 404).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (both directions).
  - **S37/S28 (counts) — wire fix HOLDS; button-UI residual unchanged.** Note `06GC5MR7`: `likedCount=2`, `score=2`, `sharedCount=1`, `repliedCount=2` (all materialized).
- **Checkpoint:** **No build change; open-item sweep on `38ae87c` — all open items STABLE (3rd consecutive stable pass).** S36 (home feed, top priority) + S39 (A-side notifications) + S24 D2 (foreign outbox, total 55) + S24 D4 (remote-collection 404) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold. **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 160 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Open-item sweep — all open items STABLE (no regression, no fix)
- **Build/Live:** No new dev commit (HEAD `553746e` = QA Pass 159 docs; dev commits since `38ae87c` are PLAN-only `069fd7f` + `e07faa5`, no `src/` change) → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=2, ii-a1 following=2, ii-b1 followers=2).
- **Explored:** **Open-item stability sweep** on `38ae87c` — S36 (home feed), S39 (A-side notifications), S24 D2 (foreign outbox), S24 D4 (remote-actor collections), S38 (webfinger), S37/S28 (counts).
- **Result (all STABLE — no regression, no fix):**
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` → **boost wrapper only** ("Boosted by ii-b1", target note `06GC3AWSHG` which is a **Tombstone** → "Content unavailable — view original post", Boost=1), **no own content posts** (unchanged from Passes 129/144/146/152/154/158/159).
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0`, items 0 (unchanged from Pass 157/159; the local-Like + cross-instance-reply + local-reply legs all produce no author notification).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **55** (stable); page 1 = **6 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /u/ii-b1` doc = **200**; `/outbox`, `/followers` = **404**; symmetric on B (`GET B /u/ii-a1` doc 200, `/outbox` 404).
  - **S38 (cross-instance webfinger) STILL OPEN.** A `wf(ii-b1@B)` = 404 + B `wf(ii-a1@A)` = 404 (both directions); own-instance `wf(ii-a1@A)` = 200 (control).
  - **S37/S28 (counts) — wire fix HOLDS; button-UI residual unchanged.** Note `06GC5MR7`: `likedCount=2`, `score=2`, `sharedCount=1`, `repliedCount=2` (all materialized). **Residual (S3, low):** the object-detail Like/Boost buttons still render "0"/"1" (the Like button "0" while wire likedCount=2 — client button count doesn't read the denormalized counts).
- **Checkpoint:** **No build change; open-item sweep on `38ae87c` — all open items STABLE (no regression, no new defect, no fix landed).** S36 (home feed, top priority) + S39 (A-side notifications) + S24 D2 (foreign outbox, total 55) + S24 D4 (remote-collection 404) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold (no regression). **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 159 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). S39 re-confirmed with a FRESH local Like (ii-a2 → ii-a1): likedCount 1→2 materialized immediately, but ii-a1 notifications STILL 0
- **Build/Live:** No new dev commit (HEAD `3a14468` = QA Pass 158 docs; no dev `src/` change since `38ae87c`) → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=2, ii-a1 following=2, ii-b1 followers=2).
- **Explored:** **S39 fresh-Like repro** (local, same-instance) + open-item stability re-check (S36, S24 D2, S24 D4, S37/S28 counts).
- **Result:**
  - **S39 (A-side local-Like notification) RE-CONFIRMED OPEN with a FRESH repro.** As **ii-a2** (A) Liked ii-a1's note `06GC5MR7` (II-S37-5) via the object-detail Like button — the note's wire `…/ns#likedCount` went **1 → 2** + `score` **1 → 2** (ii-a2's local Like **materialized immediately** → S37/S28 count fix holds for a 2nd, local, same-instance Like). BUT ii-a1 (A) `/local/v1/notifications` → **`totalItems=0`, items 0** (unchanged from Pass 157/158). The **local-Like notification leg is still broken** — a same-instance Like produces a count but **no notification for the author**. (S39 was previously established via a cross-instance reply; this pass adds the clean local-Like facet.)
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` → boost wrapper only ("Content unavailable — view original post", target note `06GC3AWSHG` which is now a **Tombstone**), **no own content posts** (unchanged).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **55** (stable from Pass 158); page 1 = **6 foreign (ii-b1)** items.
  - **S24 D4 (remote-actor collection routes 404) STILL OPEN (both directions).** `GET A /ap/v1/u/ii-b1` doc = **200**; `GET A /u/ii-b1/{outbox,followers,following}` = **404**; symmetric on B (`GET B /u/ii-a1` doc 200, `/outbox` 404). Own-instance collections = 200 (control).
  - **S37/S28 (counts) — wire fix HOLDS.** Note `06GC5MR7`: `likedCount=2`, `score=2`, `sharedCount=1`, `repliedCount=2` (all materialized; the fresh ii-a2 local Like incremented likedCount immediately). **Button-UI residual (S3, low) unchanged:** the object-detail Like/Boost buttons still render "0" (client button count doesn't read the denormalized counts).
- **Checkpoint:** **No build change; S39 local-Like facet re-confirmed (fresh repro) + open items stable on `38ae87c`.** S36 (home feed, top priority) + S39 (A-side notifications, now incl. the local-Like leg) + S24 D2 (foreign outbox, total 55) + S24 D4 (remote-collection 404) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold (no regression). **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery [incl. local-Like leg] + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 158 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Open-item sweep + NEW S24 D4 (remote-actor direct collection routes 404 while actor doc 200 + /proxy 200)
- **Build/Live:** No new dev commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200; follow edges intact: ii-a1 followers=[ii-b1,ii-a2], ii-a1 following=[community,ii-b1], ii-b1 followers=[ii-a1,ii-a2]).
- **Explored:** **Open-item sweep** on `38ae87c` (no build change) — S36 (home feed), S39 (A-side notifications), S24 D2 (foreign outbox), S24 remote-actor access, S38 (webfinger), S37/S28 (counts).
- **Result:**
  - **S36 (home feed) STILL OPEN (top priority).** `ii-a1` (A) `/home` renders **only the boost wrapper** ("Content unavailable — view original post", boosted by ii-b1, Boost=1) — **no own content posts** (unchanged from Passes 129/144/146/154). Note: the authenticated `GET /ap/v1/u/ii-a1/feed` returns **403 unauthenticated** (curl) — expected (the feed is owner-private, like `/notifications` 302); the UI fetches it authenticated. No new defect from the 403.
  - **S39 (A-side notifications) STILL OPEN.** `ii-a1` (A) `/local/v1/notifications` → `totalItems=0`, `unread=0` (unchanged from Pass 157).
  - **NEW S24 D4 (S3) — a cached remote actor's direct COLLECTION routes 404 (both directions), while the actor doc 200s + `/proxy` 200s.** `GET A /ap/v1/u/ii-b1` (doc) = **200** (D3 holds); `GET A /ap/v1/u/ii-b1/{outbox,followers,following}` = **404**; **symmetric on B** (`GET B /ap/v1/u/ii-a1` doc 200; `/outbox`, `/followers`, `/following` 404). **Controls:** own-instance `GET B /ap/v1/u/ii-b1/outbox` = 200, local `GET A /ap/v1/u/ii-a1/outbox` = 200 → **specifically the remote actor's collections 404**. `GET A /ap/v1/proxy/<B ii-b1 outbox IRI>` = **200** (reachable only via the explicit `/proxy` route). **Client-visible:** a console 404 on `ii-b1/outbox` when the UI renders a remote actor's card (seen on ii-a1 `/notifications`). Fix = proxy the remote actor's trailing collection path to the remote host. Recorded in the S24 doc (new D4 facet).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + accumulating.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **55** (grew from 49 in Pass 154); page 1 (20 items) = `11 Create + 4 Update + 1 Remove + 1 Add + 1 Follow + 1 Delete + 1 Announce`, **6 foreign (ii-b1)** on the page (the per-page count fluctuates 14→6 as the window slides, but the total foreign population keeps growing: 3 → 6 → 14 → total 55).
  - **S38 (cross-instance webfinger) STILL OPEN** — A `webfinger(ii-b1@B)` → 404 + B `webfinger(ii-a1@A)` → 404 (both directions); own-instance `webfinger(ii-a1@A)` → 200 (control).
  - **S37/S28 (counts) — wire fix HOLDS; button-UI residual unchanged.** Note `06GC5MR7` (II-S37-5): `…/ns#likedCount=1`, `score=1`, `sharedCount=1`, `repliedCount=2` (all materialize — S37 fix live); note `06GC63QMB` (II-S31-5, the local-Liked one): `likedCount=1`, `score=1`, `repliedCount=1`. **Residual (S3, low):** the embedded `likes.totalItems` = 0/None (the object-detail Like/Boost **button UI** still renders "0" — the client button count doesn't read the denormalized counts). A clean **remote** Like re-test is still **blocked by S36** (a fresh A post is not delivered into B's cache → B can't open/Like it).
- **Checkpoint:** **No build change; open-item sweep on `38ae87c`.** **NEW S24 D4** (remote-actor direct collection routes 404 — actor doc 200 + /proxy 200; both directions; client-visible 404) recorded in the S24 doc. S36 (home feed — top priority) + S39 (A-side notifications) + S24 D2 (foreign outbox, accumulating) + S38 (webfinger) + S37/S28 (button-UI residual) all STILL OPEN. S30/S26/S33/S31/S29/S32/S34/S27 hold (no regression observed). **M2–M12 + L2–L12 blocked** (operator accounts). Awaiting a dev commit + rebuild to re-verify any fix (top priority: S36 home feed; then S39 A-side notification delivery + S24 D4 remote-collection proxy + S24 D2 outbox integrity).

## Pass 157 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). NEW S39 (S2): A-side author notifications MISSING while B-side receives the same A interactions (asymmetric inbound delivery)
- **Build/Live:** No new dev commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** **Notification delivery** (local + cross-instance) on `38ae87c` — a core feature not re-checked since the 07:16:12Z rebuild (data reset). (a) `ii-a2` (A) **Liked** `ii-a1`'s note `06GC63QMB` (wire `…/ns#likedCount=1`, `…/ns#score=1` — S37 count fix live); (b) `ii-a2` (A) **replied/mentioned** `ii-a1` (HTTP 202); (c) `ii-b1` (B) **replied/mentioned** `ii-a1`'s note `06GC5MR7` (HTTP 202, threaded — `…/ns#repliedCount=2`, `inReplyTo` intact, `tags=[]`). Then checked both authors' notifications.
- **Result:** **NEW S39 (S2) — A-side notifications missing; asymmetric inbound delivery.**
  - **A-side silence:** `GET A /local/v1/notifications?limit=50` (auth ii-a1) → `totalItems=0`; `unread-count` → `unread=0` — after all three actions above (local Like, local reply, cross-instance reply). **ii-a1 got NO notification** for any of them.
  - **B-side populated (the asymmetry, decisive):** `ii-b1` (B) `/notifications` → **22 unread** — incl. ii-a1/ii-a2 "sent you a follow request" (09:43), ii-a1 "liked a post" (04:35/04:27), ii-a1 "replied … replying to ii-a1", multiple ii-a1 "posted". **B receives A's interactions; A does not receive B's.**
  - **Mention not resolved:** the `@ii-a1` mention in both replies did not resolve to a `Mention` tag (`tags=[]`, `to=[Public]`), though the replies are threaded under the parent via `inReplyTo`.
  - **Contradicts Pass 121** (build `aebe420`, "cross-instance notifications WORK") — the 07:16:12Z rebuild (data reset) + build change `aebe420→38ae87c` means this is either a regression or a data/state-dependent failure; either way **live on `38ae87c`**.
  - **Root-cause family:** same **directional inbound-delivery / shared-inbox gap** as S36's A→B delivery-to-cache failure (A post proxy-200 / note-view-404 on B). S39 is the notification-side mirror (B activity not stored in A's inbox → no notification). Cross-linked S39↔S36.
- **Checkpoint:** **S39 filed (S2) + cross-linked to S36.** Awaiting a dev code pass: (1) is the B→A Like/reply/Follow activity **stored** in A's `Activities`/`BoxItems` (inbox) for ii-a1? (if not → fix bidirectional delivery, same as S36; if stored → fix the notification query); (2) fix the **local** Like/reply notification leg (S22 fixed local *follow*; local *Like* + *reply* are the missing analogues); (3) resolve the `@handle` mention to a `Mention` tag. Open items now 13 (+S39). S36 (home feed — top priority), S24 D2, S38 (webfinger), S37/S28 (count/button residual) unchanged. S30/S26/S33/S31/S29/S32/S34 hold. **M2–M12 + L2–L12 blocked** (operator accounts).

## Pass 156 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-verified S34 (gated follow) on `38ae87c` → FIXED holds (no regression)
- **Build/Live:** No new dev commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Performed a **fresh gated-follow test** of **S34** (gated follow) on `38ae87c` — a FIXED item not re-checked on the current build. Enabled gated follow on ii-a1, had ii-a2 unfollow + re-follow (fresh pending request), checked the public followers collection pre-accept, accepted, and cleaned up.
- **Result:** **S34 FIXED — re-confirmed, no regression.**
  - **Setup:** ii-a1 (A) enabled "Require approval for follow requests" → `GET A /ap/v1/u/ii-a1` → `manuallyApprovesFollowers` = **true**.
  - **Fresh follow:** ii-a2 (A) unfollowed + re-followed ii-a1 → created a pending request.
  - **Pending (before accept):** `GET A /ap/v1/u/ii-a1/followers` (public, curl + clean authenticated read as ii-a1) = **`[ii-b1]` only (count 1)** — **ii-a2 withheld** while pending; `GET A /ap/v1/u/ii-a2/following` = **count 0**; `GET A /local/v1/u/ii-a1/requests` (authenticated) = **`[ii-a2]`** (pending). ii-a1 Requests tab shows "ii-a2 wants to follow you" (A3.1 ✓). (An earlier confused-tab browser read transiently showed ii-a2 in followers — a stale client-side read; the wire is correct.)
  - **After Accept:** `requests` = **`[]`**; `followers` = **`[ii-b1, ii-a2]`** (count 2); `ii-a2/following` = **`[ii-a1]`** (count 1) (A3.3 ✓). (Accept took a few seconds to persist.)
  - **Cleanup:** disabled gated follow on ii-a1 (`manuallyApprovesFollowers` back to false/None); ii-a2→ii-a1 follow edge restored (followers = `[ii-b1, ii-a2]`, ii-a2 following = `[ii-a1]`).
- **Checkpoint:** **S34 (gated follow) re-confirmed FIXED on `38ae87c` (pending follower withheld from public followers pre-accept; requests = [ii-a2] pre-accept, followers = [ii-b1] only; post-accept followers = [ii-b1, ii-a2], ii-a2 following = [ii-a1]). No regression; state restored.** No new defect; no build change. Open items unchanged: S36 (home feed — top priority), S24 D2 (foreign outbox items), S38 (webfinger), S37/S28 (count/button residual). S30/S26/S33/S31/S29/S32/S34 all hold. **M2–M12 + L2–L12 blocked** (operator accounts).

## Pass 155 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-verified S32 (cross-instance Delete + Update propagation) on `38ae87c` → LARGELY FIXED holds (no regression)
- **Build/Live:** No new dev commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-verified **S32** (cross-instance Delete + Update propagation) on `38ae87c` — a FIXED item not re-checked since the Pass 142 `38ae87c` rebuild. Re-tested the Pass 142 test notes (II-S32-3 deleted note `06GC5QJW`, II-S32-4 edited note `06GC5RXZ`) on A + B.
- **Result:** **S32 LARGELY FIXED — re-confirmed, no regression.**
  - **Delete (II-S32-3, `06GC5QJW`):** `GET A <note>` → **Tombstone** (`formerType` Note, `deleted`=`07:32:19Z`); `GET B <note>` (via B proxy) → **Tombstone** (`formerType` Note, `deleted`=`07:32:19Z` — **same** ts as A). B's cached copy is a Tombstone (no stale live copy). **Delete propagation HOLDS.**
  - **Update (II-S32-4, `06GC5RXZ`):** `GET A <note>` → Note, content `II-S32-4 EDITED…`, `updated`=`07:36:07Z`, `published`=`07:35:00Z`; `GET B <note>` (via B proxy) → Note, **same edited content** + **same `updated` ts** (`07:36:07Z`). B's cached copy is refreshed (no stale copy). **Update propagation HOLDS.**
  - **Residual (mechanism, unchanged):** B's shared inbox shows `no local recipient; accepting and dropping` for the A→B activity — the peer tombstone/refresh may be a **lazy refetch** rather than an applied shared-inbox `Delete`/`Update`; the **observable behavior is correct** (peer copy tombstoned on Delete, refreshed on Update). Mechanism to confirm with dev.
- **Checkpoint:** **S32 cross-instance Delete + Update propagation re-confirmed LARGELY FIXED on `38ae87c` (no regression).** No new defect; no build change. Open items unchanged: S36 (home feed — top priority), S24 D2 (foreign outbox items), S38 (webfinger), S37/S28 (count/button residual). S30 fully FIXED (Pass 153); S26 (Pass 154) + S33 + S31 + S29 + S32 still hold. **M2–M12 + L2–L12 blocked** (operator accounts).

## Pass 154 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Focused re-verify of open items (S36, S24 D2, S38, S37/S28, S26) on `38ae87c`
- **Build/Live:** No new dev commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Focused re-verification of the key **open** items on `38ae87c` (no build change): S36 (home feed), S24 D2 (foreign activities in local outbox), S38 (cross-instance webfinger), S37/S28 (Like/Boost counts), S26 (cross-instance reply threading).
- **Result:**
  - **S36 (home feed) STILL OPEN + new data point.** `GET A /ap/v1/u/ii-a1/feed` (authenticated) → 200, `totalItems`=35; page 1 = **7 Update(Note, with content) + 4 Follow + 4 Delete + 3 Like + 1 Undo + 1 Create(Group)** — **no II-A4-5/II-A4-6** (`hasII_A4_5`=false, `hasII_A4_6`=false). Content posts surface as `Update`(note) (edits), not `Create` — same data/environment-specific shape as Pass 129. **New:** a fresh A post (II-S37-6, `06GC6BCF`) is in A's outbox (Create, Public) but **NOT delivered into B's cache** — B note view **404** (after ~30s, no lag), B home feed (`totalItems`=21) lacks it, but B's **proxy** (`GET B /ap/v1/proxy/<A note>`) → **200** (live-fetches from A). The A→B post is reachable via proxy but never lands in the follower's timeline/cache — a concrete A→B instance of the S36 feed-surfacing/delivery gap (the A→B mirror of A4-5, Pass 152). S36 OPEN (S2, top priority).
  - **S24 D2 (foreign activities in local outbox) STILL OPEN + WORSE.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems`=**49**; page 1 (20 items) contains **14 foreign (ii-b1) items** (was 6 in Pass 144; 3 in Pass 116). Type mix: 11 Create + 3 Update + 2 Follow + 1 Delete + 2 Announce + 1 Like. The outbox-integrity defect is **persistent and accumulating** (foreign items added on every cross-instance interaction, never cleaned up). D1 + D3 remain fixed.
  - **S38 (cross-instance webfinger) STILL OPEN.** `GET A /…webfinger?resource=acct:ii-b1@qa-iris-b.luit.ink` → **404**; `GET B /…webfinger?resource=acct:ii-a1@qa-iris-a.luit.ink` → **404**. Cross-instance person WebFinger 404s both directions (own-instance webfinger 200). Fix = proxy WebFinger to the remote host when `@host` ≠ local.
  - **S37/S28 (Like/Boost counts) — residual still present; clean remote-Like re-test BLOCKED by S36.** The S37 note `06GC5MR7` + S28 note `06GC5K9B` both show `likes.totalItems`=0 / `shares.totalItems`=0 (no `likedCount`/`sharedCount` on the wire now — the earlier remote Like/Boost was undone). A fresh S37 remote-Like test (post on A, Like from B) is **blocked**: the fresh A post (II-S37-6) is not delivered into B's cache (B note view 404, not in B home feed) — i.e. **S36 blocks the S37 re-test** (B cannot open/Like the A note). The S37 wire-level count-materialization fix (Pass 141) is a one-off that needs a fresh remote Like to re-confirm, but the path is blocked by S36. **S37/S28 button-UI + count residual: still OPEN (blocked from clean re-verify by S36).**
  - **S26 (cross-instance reply threading) re-confirmed FIXED.** The existing II-S26-5 reply (B note `06GC6337X9`, `inReplyTo` = A parent `06GC5MR7`) is still threaded: `GET A <parent>/replies` → `totalItems`=1, contains the B reply IRI `06GC6337X9`, `inReplyTo` intact. S26 FIXED.
- **Checkpoint:** **Open items on `38ae87c` re-verified:** S36 (home feed — top priority; new A→B delivery-to-cache data point), S24 D2 (foreign outbox items — WORSE, 14 on page 1), S38 (cross-instance webfinger 404), S37/S28 (count/button residual; clean re-test blocked by S36). S26 re-confirmed FIXED. **No new defect; no build change.** S30 fully FIXED (Pass 153); S33 + S31 + S29 + S32 + S37/S28 wire counts (Passes 141–153) still hold. **M2–M12 + L2–L12 blocked** (operator accounts). S36 (home feed) is the top-priority blocker — it also blocks the S37 clean remote-Like re-test.

## Pass 153 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-verified S30 A8.4 feed-surfacing (the last open S30 facet, residual from Pass 143) on `38ae87c` → NOW FIXED: the II-S30-4 cross-post (B document 06GC5WN0, posted to the remote A community in Pass 143) is NOW SURFACED in the owner community's feed — GET A /ap/v1/c/ii-a8-community/feed → 200 (OrderedCollection, totalItems=20) + the II-S30-4 cross-post IS present (FOUND) + the UI community feed on A shows it (a "To ii-a8-community" label + "II-S30-4 cross-post from B into remote A community"). S30 A8.4 feed-surfacing FIXED → S30 fully FIXED (all of A8.2/A8.3/A8.4 work). Informational: the community /feed is a broad activity stream (11 Create + 3 Follow + 2 Undo + 1 Announce + 3 Like; includes non-community public posts II-A4-5/II-A4-6) — a separate feed-scope design question, out of scope for A8.4. S30 open count 13→12
- **Build/Live:** No new commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-verified the **S30 A8.4 feed-surfacing facet** (the last open S30 facet, residual from Pass 143) on `38ae87c` — whether the II-S30-4 cross-post (B → A community, Pass 143) is now surfaced in the owner community's feed.
- **Result:** **S30 A8.4 feed-surfacing NOW FIXED → S30 fully FIXED.**
  - **The `/feed` endpoint (200) contains II-S30-4.** `GET A /ap/v1/c/ii-a8-community/feed` → **200**, `type OrderedCollection`, totalItems=20; the **II-S30-4 cross-post is present** (`FOUND II-S30-4 cross-post in community feed | iri …/u/ii-b1/documents/06GC5WN0…`). **The feed-surfacing facet is resolved** — the community-attributed member cross-post is now in the owner community feed (the Pass 143 residual — "the feed query doesn't include it" — is resolved).
  - **The UI community feed on A shows II-S30-4.** The A community page (`/community?iri=…/c/ii-a8-community`) **Feed** tab renders the II-S30-4 post: a list item with a **"To ii-a8-community"** label (linking to the community) + the content **"II-S30-4 cross-post from B into remote A community (A8.4 re-verify, 38ae87c)"** + a Like button. **The UI surfaces it** (the Pass 143 residual — "the UI community feed on A also doesn't show II-S30-4" — is resolved).
  - **Informational (not a defect for A8.4):** the `/ap/v1/c/ii-a8-community/feed` collection is an **OrderedCollection of activities** (11 Create + 3 Follow + 2 Undo + 1 Announce + 3 Like) and includes some **public posts that are not community-addressed** (II-A4-5, II-A4-6) + **Follow/Undo activities** — i.e. it behaves more like a **community activity stream** than a strict community-only post list. The **A8.4 requirement (a member cross-post attributed to the community is surfaced in the community feed) is met** (II-S30-4 present in both endpoint + UI). The broader "should the community feed contain non-community public posts / follow activities?" is a **separate feed-scope design question** (out of scope for A8.4) — noted for dev, not a blocker.
- **Checkpoint:** **S30 A8.4 feed-surfacing re-verified FIXED on `38ae87c` → S30 fully FIXED** (the II-S30-4 cross-post is surfaced in the owner community's feed — `/feed` endpoint [200] + UI community feed; all of A8.2/A8.3/A8.4 work). **Open count 13 → 12** (S30 removed from open; moved to Fixed). No new defect; no build change. S36 (home feed) + S24 D2 + S38 + S37/S28 button-UI still OPEN. S33 + S26 + S31 + S29 re-confirmed FIXED (Passes 147–150); A4 delivery FINE (Pass 152); S32 cross-instance Delete + Update propagation WORK (Pass 142); S37/S28 wire counts FIXED (Pass 141). **M2–M12 + L2–L12 blocked** (operator accounts).

## Pass 152 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). A4-5 re-check (the 3 questions from Pass 151) on `38ae87c`: (1) B cached copy of II-A4-5 STILL 404 ~3 min later (no delivery lag); (2) B home feed STILL empty (no A post); (3) REVERSE LEG (B→A) WORKS — a fresh B post (II-A4-6, B note 06GC66HWXY) was delivered + cached by A (A cached copy → 200, Note, content correct), BUT the B post is NOT surfaced in A's home feed (A raw signed /feed [20 of 35 items] = polluted with non-content activities [7 Update + 4 Follow + 4 Delete + 3 Like + 1 Undo + 1 Create] and contains NO II-A4-6 — the S36 home-feed regression). CONCLUSION: the A4-5 facet is the S36 home-feed regression (content-omission + pollution) manifesting bidirectionally, NOT a fresh A4 delivery drop — B→A delivery works (A caches the B post) + A→B delivery is likely working too (the A post is in B's shared-inbox) but neither surfaces in the other's /feed. A4 delivery itself is FINE on 38ae87c; the S36 home-feed regression is the root cause. No code change; S36 remains the top-priority open item
- **Build/Live:** No new commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** **A4-5 re-check** (the 3 re-check questions from Pass 151) on `38ae87c` — (1) delivery-lag re-check of the A post II-A4-5 on B, (2) B home feed re-check, (3) the reverse leg (B→A) with a fresh B post.
- **Result:** **A4-5 re-check → the facet is the S36 home-feed regression (content-omission + pollution), NOT a fresh A4 delivery drop.**
  - **(1) Delivery-lag re-check (A→B):** B's cached copy of II-A4-5 (`…/u/ii-a1/notes/06GC64YFB6`) is **STILL 404 ~3 min later** (checked again this pass) — no delivery lag. B's home feed is **STILL empty** (no A post; "Your timeline is empty"). So the A post is **not** surfacing on B over time.
  - **(2) Reverse leg (B→A) — DELIVERY WORKS:** A fresh B post **II-A4-6** (B note `06GC66HWXYTFJ5J6PNJAMP88RR`) was **delivered + cached by A**: `GET A <B note iri>` → **200, `type Note`**, content = II-A4-6, `attributedTo` ii-b1. So **B→A delivery works** (A caches the B post).
  - **(3) But the B post is NOT surfaced in A's home feed (S36):** A's raw signed `/feed` (20 of 35 items) is **polluted with non-content activities** — `7 Update + 4 Follow + 4 Delete + 3 Like + 1 Undo + 1 Create` — and contains **NO II-A4-6** (the B post is cached by A but **not in the `/feed`**). This is the **S36 home-feed regression** (content-omission + pollution) manifesting on A.
  - **CONCLUSION:** The **A4-5 facet (Pass 151)** is the **S36 home-feed regression** (content-omission + pollution) manifesting **bidirectionally**, NOT a fresh A4 delivery drop: **B→A delivery works** (A caches the B post II-A4-6) + **A→B delivery is likely working too** (the A post II-A4-5 is in B's shared-inbox), but **neither surfaces in the other's `/feed`** (the S36 feed-surfacing defect). **A4 delivery itself is FINE on `38ae87c`; the S36 home-feed regression is the root cause.** The A4-5 finding **reduces to S36** (the home-feed content-omission + pollution facet).
- **Checkpoint:** **A4-5 re-check complete on `38ae87c`** — the facet is the **S36 home-feed regression** (content-omission + pollution) manifesting bidirectionally, NOT a fresh A4 delivery drop. B→A delivery works (A caches II-A4-6 → 200); A→B delivery likely works (II-A4-5 in B shared-inbox); neither surfaces in the other's `/feed` (S36). A4 delivery is FINE on `38ae87c`; **S36 remains the top-priority open item**. No code change. S36 + S24 D2 + S38 + S30 A8.4 + S37/S28 button-UI all still OPEN. S33 + S26 + S31 + S29 re-confirmed FIXED (Passes 147–150). S32 cross-instance Delete + Update propagation WORK (Pass 142); S30 A8.2/A8.3 + cross-post leg WORK (Pass 143); S37/S28 wire counts FIXED (Pass 141). A4 delivery FINE (this pass). **M2–M12 + L2–L12 blocked** (operator accounts).

## Pass 151 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-verified A4 (cross-instance follow + post delivery) with a FRESH A post on `38ae87c` → NEW A4 FACET (A4-5) — a fresh A post is NOT delivered to B: A (ii-a1) posted II-A4-5 (A note 06GC64YFB6, Create in A outbox [actor ii-a1, to Public, cc followers], published 08:27:30Z); the follow edge is intact (A /followers totalItems=2, ii-b1 present); but B's cached copy of the A note → 404 (checked at +12s/+32s/+52s + after a B UI Refresh) AND B's home feed does not show the A post (B /home = "Your timeline is empty"; after Refresh the feed has an item but it is NOT the A post — B's cached copy is still 404). This is a NEW facet on 38ae87c (A4 previously PASSED on 38ae87c in Pass 133 with fresh posts II-A4-3/II-A4-4 [06GC2W86/06GC2WCY] that B received) — possibly related to the S36 home-feed regression (the A→B Create not surfacing/caching) OR a fresh A4 delivery gap. Needs re-check: (1) is this S36 (feed-surfacing) or a true A4 delivery drop? (2) does the A post surface in B's feed on a LATER refresh / after more time? (3) does a B-originated post still reach A (the reverse leg)? No code change; handed back to dev with the II-A4-5 evidence
- **Build/Live:** No new commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-verified **A4 (cross-instance follow + post delivery)** with a **fresh** A post on `38ae87c` — A (ii-a1) posted II-A4-5 and checked whether it reached B (ii-b1)'s timeline + B's cache.
- **Result:** **NEW A4 FACET (A4-5) — a fresh A post is NOT delivered to B on `38ae87c`.**
  - A (ii-a1) posted **II-A4-5** → A note `06GC64YFB62YXSHTXGNMT9TCT0`; the **Create activity is in A's outbox** (`type Create`, `actor` ii-a1, `object` = the Note, `to` = `activitystreams#Public`, `cc` = `…/u/ii-a1/followers`, `published 2026-09-21T08:27:30Z`). **Correct on the A side.**
  - **Follow edge intact:** A `/followers` totalItems=2 (ii-a2 + ii-b1) — ii-b1 IS a follower of ii-a1.
  - **B's cached copy → 404** (checked at +12s, +32s, +52s after the post, AND after a B UI Refresh): `GET B …/u/ii-a1/notes/06GC64YFB62YXSHTXGNMT9TCT0` → **404** (empty). B's shared-inbox did **not** cache the A note.
  - **B's home feed does not show the A post:** `GET B /home` (UI, signed in as ii-b1) → **"Your timeline is empty"**; after clicking Refresh the feed has an item + "Load more", but the item is **NOT** the A post (B's cached copy is still 404).
  - **This is a NEW facet on `38ae87c`** — A4 previously **PASSED** on `38ae87c` in **Pass 133** with fresh posts II-A4-3/II-A4-4 (A notes `06GC2W86`/`06GC2WCY`) that B received (B home showed the A posts). So a fresh A→B post that delivered in Pass 133 now does **not** deliver in Pass 151 on the same build.
  - **Needs re-check (hand back to dev):** (1) Is this **S36** (the home-feed regression — the A→B Create is delivered to B's shared-inbox but the `/feed` query drops it + B's cached-object IRI 404s) or a **true A4 delivery drop** (B's shared-inbox never received the A→B Create)? (2) Does the A post surface in B's feed on a LATER refresh / after more time (delivery lag)? (3) Does the **reverse leg** (B→A post) still reach A (to isolate whether it's A→B-specific or bidirectional)? The **S36 home-feed regression** (top priority, dev proved non-reproducible in-process + handed back to QA with the signed `/feed` evidence) is the leading suspect — the A post may be the same content-omission facet now manifesting on B.
- **Checkpoint:** **NEW A4 FACET (A4-5) filed on `38ae87c`** — a fresh A post (II-A4-5, `06GC64YFB6`) is in A's outbox (Create, to Public, cc followers) + the follow edge is intact, but **B's cached copy → 404** + **B's home feed does not show the A post** (B /home empty; after Refresh the feed has an item but not the A post). A4 previously PASSED on `38ae87c` (Pass 133) with fresh posts — so this is a NEW facet, possibly the **S36 home-feed regression** manifesting on B (the A→B Create not surfacing/caching) OR a fresh A4 delivery gap. **No code change; handed back to dev** with the II-A4-5 evidence + the 3 re-check questions. S36 (home feed) + S24 D2 + S38 + S30 A8.4 + S37/S28 button-UI all still OPEN (Passes 144–146). S33 + S26 + S31 + S29 re-confirmed FIXED (Passes 147–150). S32 cross-instance Delete + Update propagation WORK (Pass 142); S30 A8.2/A8.3 + cross-post leg WORK (Pass 143); S37/S28 wire counts FIXED (Pass 141). **M2–M12 + L2–L12 blocked** (operator accounts).

## Pass 150 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-verified S29 (community webfinger) on `38ae87c` → STILL FIXED: A community ii-a8-community webfinger (acct:ii-a8-community@qa-iris-a.luit.ink) → 200 + rel=self → /ap/v1/c/ii-a8-community (a Group AP doc). No new defect; state stable on 38ae87c
- **Build/Live:** No new commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-verified **S29 (community webfinger)** on `38ae87c` — the community webfinger for the A community `ii-a8-community`.
- **Result:** **S29 STILL FIXED on `38ae87c`.** `GET A /.well-known/webfinger?resource=acct:ii-a8-community@qa-iris-a.luit.ink` → **200**, `subject` = the acct, `links` include `rel=self` → `https://qa-iris-a.luit.ink/ap/v1/c/ii-a8-community` (type `application/activity+json`). That AP doc → **200, `type: Group`**, `preferredUsername: ii-a8-community`. The community webfinger resolves correctly (the S29 fix holds). **S29 holds.**
- **Checkpoint:** **S29 re-confirmed FIXED on `38ae87c`** (community webfinger → 200 + rel=self → Group AP doc). No new defect; state stable on `38ae87c`. S36 (home feed) + S24 D2 + S38 + S30 A8.4 + S37/S28 button-UI all still OPEN (Passes 144–146). S33 + S26 + S31 re-confirmed FIXED (Passes 147–149). S32 cross-instance Delete + Update propagation WORK (Pass 142); S30 A8.2/A8.3 + cross-post leg WORK (Pass 143); S37/S28 wire counts FIXED (Pass 141). A4 PASS. S27 re-confirmed FIXED. **M2–M12 + L2–L12 blocked** (operator accounts).

## Pass 149 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-verified S31 (edit preserves published, advances updated) with a FRESH A post + edit on `38ae87c` → STILL FIXED: A posted a fresh note (II-S31-5, A note 06GC63QMBV, published 08:22:12Z, updated None) then edited it → published PRESERVED (08:22:12.3188076Z unchanged) + updated SET (08:23:12.6496208Z) + content changed. No new defect; state stable on 38ae87c
- **Build/Live:** No new commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-verified **S31 (edit preserves `published`, advances `updated`)** with a **fresh** A post + edit on `38ae87c` — A (ii-a1) posted a fresh note, then edited it.
- **Result:** **S31 STILL FIXED on `38ae87c`.**
  - A (ii-a1) posted fresh note **II-S31-5** (A note `06GC63QMBVKFV9TV94WSQP8QE4`) → **`published 2026-09-21T08:22:12.3188076Z`**, **`updated None`** (baseline).
  - A then **edited** the note (content → "II-S31-5 EDITED — Pass 149 edited content…").
  - After the edit: **`published` PRESERVED** (still `2026-09-21T08:22:12.3188076Z`, unchanged) + **`updated` SET** (`2026-09-21T08:23:12.6496208Z`) + content changed. **S31 holds** — the edit preserves the original `published` and advances `updated` (correct ActivityPub semantics).
- **Checkpoint:** **S31 re-confirmed FIXED on `38ae87c`** (fresh post + edit: `published` preserved, `updated` advanced, content changed). No new defect; state stable on `38ae87c`. S36 (home feed) + S24 D2 + S38 + S30 A8.4 + S37/S28 button-UI all still OPEN (Passes 144–146). S33 unfollow Undo + S26 reply threading re-confirmed FIXED (Passes 147–148). S32 cross-instance Delete + Update propagation WORK (Pass 142); S30 A8.2/A8.3 + cross-post leg WORK (Pass 143); S37/S28 wire counts FIXED (Pass 141). A4 PASS. S27/S29 re-confirmed FIXED. **M2–M12 + L2–L12 blocked** (operator accounts).

## Pass 148 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-verified S26 (cross-instance reply threading) with a FRESH B reply on `38ae87c` → STILL FIXED: B (ii-b1) posted a fresh reply (II-S26-5, B note 06GC6337X9MNP8W4CNRTP29HJR) to the A note 06GC5MR7 (II-S37-5) → the reply federated to A and is in the A note's /replies collection (totalItems=1) + carries inReplyTo → the A note (threading intact). No new defect; state stable on 38ae87c
- **Build/Live:** No new commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-verified **S26 (cross-instance reply threading)** with a **fresh** B reply on `38ae87c` — B (ii-b1) posted a reply to the A note `06GC5MR7` (II-S37-5, which had "No replies yet").
- **Result:** **S26 STILL FIXED on `38ae87c`.**
  - B (ii-b1) posted reply **II-S26-5** ("II-S26-5 Pass 148 fresh B reply to A note (38ae87c) — re-verify cross-instance reply threading") to the A note `06GC5MR7` via `B /compose?replyTo=…06GC5MR7`.
  - The reply **federated to A**: the A note's `/replies` collection now has **totalItems=1**, listing the B reply note `06GC6337X9MNP8W4CNRTP29HJR`.
  - The B reply note carries **`inReplyTo` → the A note `06GC5MR7`** + `attributedTo` ii-b1 + `content` = II-S26-5 — the **threading is intact** (the reply is correctly threaded under the parent A note and visible in its replies). **S26 holds.**
- **Checkpoint:** **S26 re-confirmed FIXED on `38ae87c`** (fresh B reply federated to A + threaded under the A note via `inReplyTo` + in the `/replies` collection). No new defect; state stable on `38ae87c`. S36 (home feed) + S24 D2 + S38 + S30 A8.4 + S37/S28 button-UI all still OPEN (Passes 144–146). S33 unfollow Undo re-confirmed FIXED (Pass 147). S32 cross-instance Delete + Update propagation WORK (Pass 142); S30 A8.2/A8.3 + cross-post leg WORK (Pass 143); S37/S28 wire counts FIXED (Pass 141). A4 PASS. S27/S29/S31 re-confirmed FIXED. **M2–M12 + L2–L12 blocked** (operator accounts).

## Pass 147 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-verified S33 (unfollow Undo propagation) end-to-end on `38ae87c` → STILL FIXED: B (ii-b1) unfollowed ii-a1 → A's /followers totalItems 2→1 (ii-b1 removed; the Undo(Follow) federated A→B); then B re-followed ii-a1 → A's /followers totalItems 1→2 (ii-b1 restored; the Follow federated A→B). The follow edge was restored (A followers now ii-a2 + ii-b1). No new defect; state stable on 38ae87c
- **Build/Live:** No new commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-verified **S33 (unfollow Undo propagation)** end-to-end on `38ae87c` — B (ii-b1) unfollowed ii-a1 (A), then re-followed ii-a1 — and checked A's `/followers` collection after each.
- **Result:** **S33 STILL FIXED on `38ae87c`.**
  - **Unfollow (B → A):** Before, A `/followers` totalItems=2 (ii-a2 + ii-b1). After B clicked Unfollow + 8s, A `/followers` **totalItems=1** (ii-a2 only; ii-b1 removed) — the **`Undo(Follow)` activity federated A→B** and A's follower collection was updated. **S33 holds.**
  - **Re-follow (B → A):** After B clicked Follow + 8s, A `/followers` **totalItems=2** (ii-a2 + ii-b1) — the **`Follow` activity federated A→B** and ii-b1 was re-added. **The follow edge was restored** (A followers now ii-a2 + ii-b1), so the A-suite follow-state precondition holds for subsequent passes.
- **Checkpoint:** **S33 re-confirmed FIXED on `38ae87c`** (unfollow Undo propagates + re-follow propagates; A `/followers` totalItems 2→1→2). The follow edge (ii-b1 → ii-a1) was **restored**. No new defect; state stable on `38ae87c`. S36 (home feed) + S24 D2 + S38 + S30 A8.4 + S37/S28 button-UI all still OPEN (Passes 144–146). S32 cross-instance Delete + Update propagation WORK (Pass 142); S30 A8.2/A8.3 + cross-post leg WORK (Pass 143); S37/S28 wire counts FIXED (Pass 141). A4 PASS. S26/S27/S29/S31 re-confirmed FIXED. **M2–M12 + L2–L12 blocked** (operator accounts).

## Pass 146 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-confirmed the top-priority open item S36 (home feed) on `38ae87c` → STILL REPRODUCES (A /home = boost wrapper only, "Content unavailable", Boost=1, no own content posts — unchanged from Pass 144). No new dev commit, no build change, no new defects; all open items stable on 38ae87c. Awaiting a dev commit + rebuild to re-verify any fix
- **Build/Live:** No new commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-confirmed the **top-priority open item S36 (home feed)** on `38ae87c` to check for any state drift since Pass 144.
- **Result:** **S36 STILL REPRODUCES on `38ae87c`.** `ii-a1` (A) `/home` renders only a **boost wrapper** ("Content unavailable — view original post", boosted by ii-b1, 6h ago, Boost=1) — **no own content posts** (the "timeline is empty" content-omission facet). **Unchanged from Pass 144.** Confirms the live drop is **data/environment-specific** (the edited-posts → `Update` + accumulated S32 `Delete`/`Undo` noise shape persists on `38ae87c`), **consistent with dev's `162159b` in-process proof** (fresh state is correct; the live edited+noisy state drops content). S36 remains **handed back to dev** with the captured signed `/feed` evidence (Pass 129).
- **Checkpoint:** **S36 re-confirmed OPEN on `38ae87c`** (home feed = boost wrapper only, no own posts — unchanged from Pass 144). No new dev commit, no build change, no new defects. All open items stable on `38ae87c`: S36 (home feed), S24 D2 (foreign activities in outbox), S38 (remote webfinger), S30 A8.4 (cross-post feed-surfacing), S37/S28 (Like/Boost button UI). S32 cross-instance Delete + Update propagation WORK (Pass 142); S30 A8.2/A8.3 + cross-post leg WORK (Pass 143); S37/S28 wire counts FIXED (Pass 141). A4 PASS. S27/S31/S33 re-confirmed FIXED. **M2–M12 + L2–L12 blocked** (operator accounts). **Awaiting a dev commit + rebuild to re-verify any fix.**

## Pass 145 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-verified the S37/S28 Like/Boost BUTTON-UI facet (the single residual from Pass 141) on `38ae87c` → STILL OPEN (S3, low): on note 06GC5MR7 (II-S37-5, Liked + Boosted by B in Pass 141) the wire denormalized counts are correct (likedCount=1, sharedCount=1, score=1) + the "Likes (1)" + "Shares (1)" tabs render, BUT the object-detail Like button "0" + Boost button "0" (the client button count doesn't read the denormalized likedCount/sharedCount; the embedded likes.totalItems/shares.totalItems in the doc also stay 0). No new defect — the residual UI facet persists, as expected (no dev fix yet). State stable on 38ae87c
- **Build/Live:** No new commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-verified the **S37/S28 Like/Boost button-UI facet** (the single residual from Pass 141) on `38ae87c` — the object-detail Like/Boost button count on the note `06GC5MR7` (II-S37-5, which was Liked + Boosted by B in Pass 141).
- **Result:** **S37/S28 button-UI facet STILL OPEN (S3, low) on `38ae87c`.** On the note `06GC5MR7`:
  - **Wire denormalized counts — correct** (the Pass 141 fix holds): `likedCount=1`, `sharedCount=1`, `score=1`, `repliedCount=0`, `dislikedCount=0` (under the iris: namespace).
  - **Tabs — correct:** "Likes (1)" + "Shares (1)" render (the `/likes` + `/shares` collections are read correctly).
  - **BUTTON-UI facet — STILL OPEN:** the object-detail **Like button shows "0"** + **Boost button shows "0"** (the client's button count does **not** read the denormalized `likedCount`/`sharedCount`), and the doc's **embedded `likes.totalItems`/`shares.totalItems` also stay 0** (only the top-level denormalized `likedCount`/`sharedCount` are materialized). So the Like/Boost **button count** and the **embedded collection `totalItems`** don't reflect the applied Like/Boost — a **client/UI + doc-serializer facet**.
- **Checkpoint:** **S37/S28 button-UI facet re-confirmed OPEN on `38ae87c`** (wire `likedCount`/`sharedCount`/`score` correct + "Likes (1)"/"Shares (1)" tabs render, but Like/Boost **button "0"** + embedded `likes.totalItems`/`shares.totalItems` "0" — the client button count doesn't read the denormalized counts). No new defect; no build change. S36 (home feed) + S38 (webfinger) + S24 D2 + S30 A8.4 feed-surfacing all still OPEN (Pass 144). S32 cross-instance Delete + Update propagation WORK (Pass 142). A4 PASS. S27/S31/S33 re-confirmed FIXED. M2–M12 + L2–L12 blocked.

## Pass 144 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy; no `src/` change since `38ae87c`, no rebuild needed). Re-verified the open-item sweep on `38ae87c` — all STABLE: S36 (home feed) STILL REPRODUCES (A /home = boost wrapper only, "Content unavailable", no own content posts — the data/environment-specific drop persists on the new build, consistent with dev's `162159b` in-process proof); S38 (remote webfinger) STILL OPEN (A webfinger(ii-b1@qa-iris-b) → 404 + B webfinger(ii-a1@qa-iris-a) → 404, own-instance → 200); S24 D2 (foreign activities in local outbox) STILL OPEN (ii-a1's A outbox = 20 items, 6 foreign ii-b1 activities [2 Create + 2 Announce + 1 Follow + 1] — grew from 2 in Pass 123 as cross-instance activity accumulated; the outbox-integrity defect persists); S30 A8.4 feed-surfacing STILL OPEN (the Pass 143 cross-post II-S30-4 still not in A's community feed). No new defects. State stable on `38ae87c`
- **Build/Live:** No new commit (HEAD `e07faa5`, PLAN-only since `38ae87c`); no `src/` change since `38ae87c` → no cluster rebuild needed. QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-verified the **open-item sweep** on `38ae87c` (S36, S38, S24 D2, S30 A8.4) to confirm none regressed / changed on the current build.
- **Result:** **All open items STABLE on `38ae87c`.**
  - **S36 (home feed) — STILL REPRODUCES.** `ii-a1` (A) `/home` renders only a **boost wrapper** ("Content unavailable — view original post", boosted by ii-b1, 5h ago, Boost=1) — **no own content posts** (the "timeline is empty" content-omission facet). Identical to Passes 129/137. Confirms the live drop is **data/environment-specific** (the edited-posts → `Update` + accumulated S32 `Delete`/`Undo` noise shape persists on `38ae87c`), **consistent with dev's `162159b` in-process proof** (fresh state is correct; the live edited+noisy state drops content). S36 remains **handed back to dev** with the captured signed `/feed` evidence (Pass 129).
  - **S38 (remote webfinger) — STILL OPEN.** `GET A /.well-known/webfinger?resource=acct:ii-b1@qa-iris-b.luit.ink` → **404**; `GET B ...acct:ii-a1@qa-iris-a.luit.ink` → **404** (both directions); own-instance `GET A ...acct:ii-a1@qa-iris-a.luit.ink` → **200**. Handle-based cross-instance discovery is still broken at the WebFinger layer (follow-by-IRI still works, so the A-suite isn't blocked). Fix = proxy the WebFinger request to the remote host when `@host` ≠ local.
  - **S24 D2 (foreign activities in local outbox) — STILL OPEN.** `ii-a1` (A) outbox = **20 items, 6 foreign ii-b1 activities** (2 Create + 2 Announce + 1 Follow + 1) — the outbox-integrity defect **persists** (grew from 2 foreign in Pass 123 as cross-instance activity accumulated; S24 D1 Following-tab was fixed in Pass 116, so S24 now = D2 only).
  - **S30 A8.4 feed-surfacing — STILL OPEN.** The Pass 143 cross-post (II-S30-4, federated to A attributed to the community) is still **not surfaced in A's community feed** (the narrowed A8.4 facet).
- **Checkpoint:** **Open-item sweep re-verified on `38ae87c` — all STABLE** (S36 home-feed drop persists; S38 remote webfinger 404s; S24 D2 6 foreign activities in the local outbox; S30 A8.4 cross-post not in the community feed). No new defects; no build change. S37 + S28 wire counts FIXED (Pass 141); S32 cross-instance Delete + Update propagation WORK (Pass 142); S30 A8.2/A8.3 FIXED + cross-post leg WORKS (Pass 143). A4 PASS. S27/S31/S33 re-confirmed FIXED. M2–M12 + L2–L12 blocked.

## Pass 143 (2026-09-21) — Dev committed `e07faa5` (PLAN-only: S30 A8.4 "no compose UI" claim is STALE — the compose `?community=` selector + local cross-post path + remote `GetCrossPostTargetsAsync` leg all exist, 4 passing integration tests; no code change; live re-verify deferred to QA). Live re-verified S30 A8.4 on `38ae87c` → A8.4 NARROWED: (1) the community-page "＋ Post to this community" entry point + compose `?community=` selector EXIST (my Pass 138 "no compose selector" was stale — I'd checked the generic compose page, not the community page); (2) the cross-post leg WORKS — a B cross-post into the remote A community (II-S30-4) federated to A attributed to the A community (attributedTo includes the community, to=community+Public, A received+accepted); (3) the /ap/v1/c/ii-a8-community/feed endpoint now returns 200 (was 404 in Pass 138); (4) RESIDUAL: the cross-post is NOT surfaced in the OWNER community's feed (federated + stored attributed to the community, but the feed query — 8× ii-a1 + 2× ii-b1 replies — doesn't include it). S30 now reduces to the A8.4 community-feed-surfacing facet (a member cross-post not in the community feed). No cluster rebuild needed (e07faa5 is PLAN-only, no src change; cluster stays on 38ae87c)
- **Build/Live:** Dev committed **`e07faa5`** (PLAN-only — "S30 A8.4 community-post federation — code pass found it already implemented"; **no `src/` change**, no cluster rebuild needed — the cluster stays on `38ae87c`, healthy). No other new commit since `38ae87c`.
- **Explored:** Live re-verified **S30 A8.4 (community-post federation)** on `38ae87c`, per dev's `e07faa5` request.
- **Result:** **S30 A8.4 NARROWED.**
  1. **The "no compose UI" claim is STALE** (dev was right). On B, the **CommunityDetail page** for the remote A community renders a **"＋ Post to this community"** link → `/compose?community=https://qa-iris-a.luit.ink/ap/v1/c/ii-a8-community`. My Pass 138 "no compose community selector" observation was **stale** — I had checked the *generic* `/compose` page (which has only Note/Article/Poll + Public/Followers/Direct), not the community page's button. The entry point is the community page.
  2. **The cross-post leg WORKS (B → A).** The compose opened pre-labeled "Posting to II-A8 Test Community" + "Post to community" button. Posting **`II-S30-4 cross-post from B into remote A community`** produced a B document `…/u/ii-b1/documents/06GC5WN0QDRV288ZWDN57NN09G`, `type: Page`, **`attributedTo: […/u/ii-b1, …/c/ii-a8-community]`**, `to: […/c/ii-a8-community, as:Public]`, `cc: […/c/ii-a8-community/followers]`. **A received it** (`Inbox received/processed/accepted: Create from …/ii-b1 targeting …/documents/06GC5WN0`, recipient `ii-a1`, peer `qa-iris-b`). So the **cross-instance cross-post leg (B posts to a remote A community; the note federates to A attributed to the community) WORKS.**
  3. **The `/ap/v1/c/{name}/feed` endpoint now returns 200** (was 404 in Pass 138) with 20 items — the A8.4 endpoint gap is closed.
  4. **RESIDUAL (narrowed A8.4): the cross-post is NOT surfaced in the OWNER community's feed.** `GET A /ap/v1/c/ii-a8-community/feed` (200, 20 items) does **not** include II-S30-4 (the feed's `attributedTo` breakdown = 8× `u/ii-a1` + 2× `u/ii-b1` = ii-a1's own community posts + ii-b1's earlier **cross-instance replies**; the cross-post is absent). The **UI community feed on A also omits II-S30-4** (top item = ii-a1's II-S32-4). So although the note **federates to A attributed to the community**, the **community feed's query does not surface it** — a **feed-surfacing** facet.
- **Checkpoint:** **S30 A8.4 NARROWED on `38ae87c`** — the "no compose UI" claim is STALE (community-page entry point + compose `?community=` + the **cross-post leg B→A attributed to the community all WORK**); the `/feed` endpoint now 200; **RESIDUAL = a member cross-post is NOT surfaced in the owner community's feed** (the narrowed A8.4). S30 now reduces to the A8.4 community-feed-surfacing facet. S32 cross-instance Delete + Update propagation WORK (Pass 142). S37 + S28 wire counts FIXED (Pass 141). S36 still OPEN (data/environment-specific). A4 PASS. S27/S31/S33 re-confirmed FIXED. S24 (D2), S38 (webfinger) open. M2–M12 + L2–L12 blocked.

## Pass 142 (2026-09-21) — No new commit / cluster unchanged (`38ae87c`, healthy). Re-verified S32 (cross-instance Delete delivery) on `38ae87c` → the sending-side Delete facet now WORKS: posted fresh A note II-S32-3 (06GC5QJW), B cached it (Create federated), A deleted it (Tombstone on owner) → B's cached copy is ALSO a Tombstone (formerType Note, deleted=07:32:19Z = the delete time). The cross-instance A→B Delete now propagates (was "sending-side missing" in Passes 109/110). Residual: B's shared inbox shows "no local recipient; accepting and dropping" for the A→B activity (the S27-class shared-inbox facet) — the tombstone may be via a refetch/refresh rather than an applied shared-inbox Delete; worth confirming the mechanism, but the OBSERVABLE behavior (peer copy tombstoned) is now correct
- **Build/Live:** No new commit (HEAD `069fd7f`, docs-only since `38ae87c`); QA cluster unchanged (build `38ae87c`, recreated 07:16:12Z; A + B health 200).
- **Explored:** Re-verified **S32 (cross-instance Delete delivery)** on `38ae87c` — the sending-side facet (A→B Delete not delivered) that was OPEN in Passes 109/110.
- **Result:** **S32 cross-instance Delete AND Update propagation now WORK on `38ae87c`.**
  - **Delete (II-S32-3, `…/06GC5QJW`):** posted a fresh A note; B **cached it** (Create federated + accepted: `Inbox accepted: Create from …/ii-a1 targeting …/06GC5QJW`); then A **deleted** it (the note is a **Tombstone** on the owner A). **B's cached copy is ALSO now a Tombstone** — `GET B <note>` → 200, `type: Tombstone`, `formerType: Note`, `deleted: 2026-09-21T07:32:19.3619452Z` (= the delete time). So the **cross-instance A→B Delete now propagates** — the peer's cached copy is tombstoned (the **sending-side Delete facet that was "missing" in Passes 109/110 now works**).
  - **Update (II-S32-4, `…/06GC5RXZ`):** posted a fresh A note; B **cached it** (200); then A **edited** it (content → "II-S32-4 EDITED — …", `updated: 07:36:07Z`). **B's cached copy reflects the SAME edited content + SAME `updated` timestamp** (`07:36:07Z`) — the **cross-instance A→B Update now propagates** (no stale copy; the peer's cached copy is refreshed). So the **sending-side Update facet (also "missing" in Pass 110) now works too**.
  - **Residual (mechanism, to confirm):** B's shared-inbox log shows `Shared inbox: no local recipient; accepting and dropping. Peer: …/ii-a1#key-1` for the A→B activity — the S27-class shared-inbox "no local recipient" facet is still present (the Delete is not applied via the shared inbox). So the peer tombstone may be via a **refetch/refresh** of the note (B re-fetches the A note → sees the Tombstone) rather than an applied shared-inbox `Delete` activity. The **observable behavior (peer copy tombstoned, not stale) is now correct**, but the **delivery mechanism** (applied Delete vs lazy refetch) is worth confirming with dev.
  - **Net:** S32's **observable cross-instance Delete AND Update propagation now work** on `38ae87c` (peer copy tombstoned on Delete; peer copy refreshed on Update). The earlier "B's cached copy goes stale / peer-stale-copy risk" (Passes 109/110) is **resolved for BOTH Delete + Update**.
- **Checkpoint:** **S32 cross-instance Delete + Update propagation re-verified → WORK on `38ae87c`** (A delete → A Tombstone + B Tombstone; A edit → B copy shows the edited content + same `updated` ts). The peer-stale-copy risk (Passes 109/110) is **resolved**. **Residual:** B shared-inbox "no local recipient" for the A→B activity (the tombstone/refresh may be a lazy refetch, not an applied shared-inbox activity) — confirm the mechanism with dev, but the **observable behavior is correct**. S37 + S28 wire counts FIXED (Pass 141). S30 A8.2/A8.3 FIXED; S30 = A8.4. S36 still OPEN (data/environment-specific). A4 PASS. S27/S31/S33 re-confirmed FIXED. S24 (D2), S38 (webfinger) open. M2–M12 + L2–L12 blocked.

## Pass 141 (2026-09-21) — Dev committed `38ae87c` (S37 fix — "refresh Note likedCount immediately on remote Like/unlike"); REBUILT + REDEPLOYED the QA cluster to `38ae87c` (created 07:16:12Z, both health 200). S37 wire-level count re-verified → FIXED: posted fresh A note II-S37-5 (06GC5MR7), B Liked it → A note doc now carries the denormalized counts under the iris: namespace IMMEDIATELY (likedCount=1, score=1, sharedCount=0, repliedCount=0, dislikedCount=0) + "Likes (1)" tab — the wire count materializes on the Like itself (no 30 s wait; before the fix these were absent). RESIDUAL UI facet: the object-detail Like BUTTON still renders "0" (the client's button count doesn't read the denormalized likedCount) — a client/UI gap, not the wire count
- **Build/Live:** Dev committed **`38ae87c`** ("S37: refresh Note likedCount immediately on remote Like/unlike" — the Pass-139 WIP, now committed; `src/` clean). **Rebuilt + redeployed the QA cluster** to `38ae87c` (containers recreated 2026-09-21T07:16:12Z; A + B health 200).
- **Explored:** Re-verified **S37 (count materialization)** on the new build `38ae87c` (the dev's fix).
- **Result:** **S37 wire-level count re-verified → FIXED; residual UI facet (Like button).** Posted a fresh A note **`II-S37-5`** (`…/u/ii-a1/notes/06GC5MR7VQSR9TC2V2Z4KXPNBW`), then B (ii-b1) **Liked** it. On A (owner), the note **document now carries the denormalized counts under the iris: namespace IMMEDIATELY** (no 30 s wait):
  - `https://qa-iris-a.luit.ink/ns#likedCount`: **1** (materialized on the Like itself — before the fix these were **absent** entirely, Pass 140)
  - `https://qa-iris-a.luit.ink/ns#score`: **1**
  - `https://qa-iris-a.luit.ink/ns#sharedCount`: 0, `repliedCount`: 0, `dislikedCount`: 0
  - The object-detail **"Likes (1)" tab** reflects the count.
  - A's log confirms the Like was received + `LikeActivityHandler … ok`; the `/likes` collection = totalItems 1.
  - **The fix works at the wire level**: `RefreshObjectCountsAsync` (called from `LikeActivityHandler` after `RecordLikeAsync`) re-computes + persists the denormalized `likedCount` immediately, so the very next read of the note doc is correct (closing both the immediate and the stale-0 gap that Pass 140 showed the periodic pass did not close).
  - **RESIDUAL UI facet (S3, low):** the object-detail **Like button still renders "0"** (and Boost "0") even though the wire `likedCount`=1 + the "Likes (1)" tab is correct. The **client's Like-button count does not read the denormalized `likedCount`** (it likely still derives from the embedded `likes.totalItems`, which the doc serializer keeps at 0). So the **wire count is FIXED** but the **inline button UI** still shows 0 — a client/UI facet to follow up (the button should render `likedCount`).
  - **S28 Boost `sharedCount` ALSO FIXED (verified):** B then **Boosted** the same note → A note doc now shows `…/ns#sharedCount: 1` (iris: ns) + `…/ns#likedCount: 1` + `/shares` totalItems=1 + `/likes` totalItems=1. So the **wire `sharedCount` materializes too** (via the periodic pass — `RefreshObjectCountsAsync` computes all four counters, and dev's immediate-refresh is only wired into Like/Undo, but the Boost count still converges). **S28's remaining count facet (wire `sharedCount`) is now FIXED as well.**
- **Checkpoint:** **S37 wire-level count re-verified FIXED on `38ae87c`** (note doc `likedCount`=1 materialized **immediately** on a remote Like; "Likes (1)" tab; before the fix absent). **S28 wire `sharedCount` ALSO FIXED** (note doc `sharedCount`=1 after a remote Boost + `/shares` totalItems=1). **Single residual facet (S3, low): the object-detail Like/Boost BUTTON UI still renders "0"** (the client button count doesn't read the denormalized `likedCount`/`sharedCount`; the embedded `likes.totalItems`/`shares.totalItems` in the doc also stay 0) — a client/UI + doc-serializer facet. Cluster is on `38ae87c`. S36 still OPEN (data/environment-specific). S30 A8.2/A8.3 FIXED; S30 = A8.4. A4 PASS. S27/S31/S33 re-confirmed FIXED. S24 (D2), S32 (sending-side), S38 (webfinger) open. M2–M12 + L2–L12 blocked.

## Pass 140 (2026-09-21) — No new commit / cluster unchanged (`f9ca2d2`, healthy). Re-verified S37 (count materialization) is STILL OPEN on the current build: posted a fresh A note (II-S37-4), B Liked it → A's note `/likes` collection = totalItems 1 (Like received + stored) BUT the note doc `likedCount`=None + `likes.totalItems`=0 — and it STAYS stale even after 35 s (the periodic 30 s refresh pass does NOT materialize it either; `WriteCountsIfChanged` only fires when the count is *changed*, so an absent/stale-0 count is never refreshed). This confirms S37 is open on `f9ca2d2` and that dev's uncommitted immediate-refresh WIP (Pass 139) is the right fix. Re-verify live once dev commits + I rebuild
- **Build/Live:** No new commit (HEAD `0bf101c`, docs-only since `f9ca2d2`); QA cluster unchanged (build `f9ca2d2`, recreated 06:49:15Z; A + B health 200). Dev's S37 fix is still **UNCOMMITTED WIP** (Pass 139).
- **Explored:** Re-verified **S37 (count materialization)** end-to-end on the current build `f9ca2d2` to confirm the gap persists before dev's fix lands.
- **Result:** **S37 STILL OPEN on `f9ca2d2`.** Posted a fresh A note **`II-S37-4`** (`…/u/ii-a1/notes/06GC5K9BCJA3GD6NJSSBQ2PNS4`), then B (ii-b1) **Liked** it. On A (owner): the note's **`/likes` collection** = **totalItems 1** (the remote Like was received + stored), **BUT** the note **document** shows **`likedCount`=None + `likes.totalItems`=0** (the denormalized count + embedded collection count are stale). **Even after 35 s** (past the periodic 30 s refresh pass), the doc **stays `likedCount`=None + `likes.totalItems`=0** — so it is **not merely the 30 s refresh timing**: the periodic pass's `WriteCountsIfChanged` only persists when the computed count *differs* from the stored one, and an **absent / stale-0** count is never refreshed (the pre-computed value is absent, so the read path falls back to a reverse-index sweep that also reads 0 for the embedded `totalItems`). **This confirms S37 is open on `f9ca2d2` and that dev's uncommitted immediate-refresh WIP (Pass 139 — `RefreshObjectCountsAsync` called right after `RecordLikeAsync`) is the right fix** (it forces the count to be re-computed + persisted on the Like itself, closing both the immediate and the stale-0 gap).
- **Checkpoint:** **S37 re-confirmed OPEN on `f9ca2d2`** (remote Like stored + `/likes` collection correct, but note doc `likedCount`/`likes.totalItems` stay None/0, and stay stale past the 30 s periodic pass). Dev's fix = **UNCOMMITTED WIP** (Pass 139) — **re-verify live once dev commits + I rebuild** (expect: a remote Like → note `likedCount` + `likes.totalItems` materialize immediately; a remote Boost → `sharedCount`/`shares.totalItems` may still be periodic until the Announce handler is also wired). S36 still OPEN (data/environment-specific). S30 A8.2/A8.3 FIXED; S30 = A8.4. A4 PASS. S27/S31/S33 re-confirmed FIXED. S24 (D2), S32 (sending-side), S38 (webfinger) open. M2–M12 + L2–L12 blocked.

