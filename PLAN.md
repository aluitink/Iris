# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN

S100 | OPEN | - | Lemmy site-deref of Iris instance fails: value too long varchar(20); duplicate reply Create 400
S102 | OPEN | - | Home Communities tab shows Posts content, should show followed communities content
S103 | OPEN | - | Profile needs ability to upload a banner image
S104 | OPEN | - | Profile, not all tabs show proper content
S105 | OPEN | - | Home feed post is missing user posts, we should see posts and boosts from followed users and self
S106 | OPEN | - | Notifications page content causes feed to be too wide on mobile resulting in horizontal scroll
## CLOSED

S99 | CLOSED | qa | No theme/dark-mode setting in Settings (no Appearance section) — PASS: live qa-iris-a (s95qa) Settings>Appearance tab present; Dark default (data-theme=null, ls dark); switch Light -> data-theme="light" + ls light + PUT {theme:"light"} + GET round-trips + DB NotificationPrefsJson {"Theme":"light"}; hard reload -> early-paint light (no dark flash), Light radio pre-checked from server; revert Dark -> data-theme removed + DB {"Theme":"dark"}; 0 console errors
S96 | CLOSED | qa | Search local-only; add cross-instance post search over followed remote outboxes — PASS: live qa-iris-a signed-in s95qa (follows qa-lemmy s50qa) search ?q=S96XSEARCH-7Q4LZ returns the nested remote post/2 (Announce{Create{Page}}, not in local store) via cross-instance pass; opens + renders (lemmyadmin/s50qa); 0 console errors
S98 | CLOSED | qa | Communities page local-only; no cross-instance community discovery — PASS: live qa-iris-a "All known" tab lists qa-lemmy s50qa (remote) + local s50qa-community; "All on this instance" excludes remote; 0 console errors
S97 | CLOSED | qa | Lemmy post Replies tab empty though header shows N comments (S43 skips walk) — PASS: live qa-iris-a /object?iri=...post/1 Replies tab renders s95qa reply (server ?iri= 200 orderedItems=1); header 1 comments agrees; 0 console errors
S94 | CLOSED | qa | Iris->Lemmy reply Create 400 dead-lettered; Lemmy inbox rejects the Note reply — PASS: live qa-iris-a s95qa replied to Lemmy /post/1, note to[] carries full-IRI activitystreams#Public, comment id 1 landed (path 0.1, by s95qa)
S95 | CLOSED | qa | Proxy 404 + unreachable both say could-not-reach; distinguish not-found for 404 — PASS: live qa-iris-a 404->"No account found on host", 502->"Could not reach", real acct resolves; 0 unexpected console errors
S93 | CLOSED | dev1 | Like/Boost btns ignore existing state on load; re-click duplicates Like/Announce — FIXED (EngagementBar): server renders iris:isLiked/isShared ONLY when true (false omitted), so GetIsLiked/GetIsShared are 'true' or null(absent); old fast path treated null as 'not liked', so a fresh nav to the object page within the doc cache TTL (max-age=60) after a like/boost served the stale pre-like doc -> 0/unpressed btn + duplicate Like/Announce on re-click. Now when signed in and either per-requester state is absent, the bar re-derives the viewer's net like/boost (+minted ids) from the authoritative /likes+/shares collections (shared per-object engagement cache), keeping the cacheable counts; both-present or signed-out uses them directly. Live dev1: s93b liked s93a's post, fresh nav -> Like btn pressed/count 1 (was 0/unpressed); re-click UN-likes (1->0, no dup); Boost btn pressed on fresh nav; 0 console errors. Needs fresh WASM bundle (browser cache)
S92 | CLOSED | dev2 | Lemmy interop: iris user follows lemmy community; posts reach iris home feed — ALREADY WORKS (no code change): live dev2, logged-in s87clean followed Lemmy community /c/s92interop via /actor?iri= Follow btn (Edges Kind 10+11 recorded); Lemmy post /post/1 ("S92 test post / hello from lemmy for S92 iris interop", author lemmyadmin, boosted-by s92interop) surfaces in iris home feed BOTH merged (Posts) + Communities tabs. Post reaches feed via signed live outbox walk (FetchRemoteOutboxAsync); no inbox Create stored (Lemmy delivers community post as Announce). Screenshot s92-homefeed-lemmy-post.png
S90 | CLOSED | qa | Mark edited posts: show "edited" label on objects that were updated via Update — PASS: live qa-iris-a 6 edited posts render "edited {time}" (was "updated"), fresh WASM bundle, 0 console errors
S91 | CLOSED | qa | Copy post permalink button on object card + clipboard "Copied" success state — PASS: live qa-iris-a Copy link btn on every card, click writes object IRI to clipboard, "Copied" flashes 1.5s then reverts, 0 console errors
S89 | CLOSED | qa | Post edit persists; signed GET serves stale pre-edit content (object+profile) — PASS: live qa-iris-a edited note GET no-cache, outbox?type=content shows edited v2; never-edited note keeps max-age=60,swr=300
S87 | CLOSED | dev2 | Profile edit Save makes no API call (bio+checkbox lost) — NOT REPRODUCED: live dev2 (post-S83 build) fresh acct s87clean UI-only (type bio+click checkbox+Save) → POST /ap/v1/u/s87clean/outbox 202 Update{type:Person,bio,icon:[]}, server GET confirms summary+manuallyApprovesFollowers persisted; s87repro acct same; both /profile button + ?edit=true deep-link paths work; form prefills on fresh load. Save DOES call API + persist; no repro, no code change
S88 | CLOSED | qa | Search "Actors only" filter lost on page reload / URL round-trip — PASS: live qa-iris-a ?q=&actors=1 reload keeps checkbox checked, 0 console errors
S83 | CLOSED | qa | Article edit silently fails (Note edit works) — PASS: edit persists as Article
S85 | CLOSED | qa | Notifications page never refreshes while open — PASS: in-page re-fetch works
S86 | CLOSED | qa | Likes tab renders raw IRI instead of post content — PASS: shows post cards
S82 | CLOSED | qa | community feed filters to content items (ContentItems.IsContentPost) — PASS: live qa-iris-a new community s82qa, Note post + in-community Like; feed shows 1 content card, Like excluded (no empty card), 0 console errors
S84 | CLOSED | qa | Profile Following tab stale after follow: PASS: live qa-iris-a follow+unfollow refresh panel, 0 console errors
S81 | CLOSED | qa | 4 divergent content-item copies unified into Iris.Core.ContentItems — PASS: live qa-iris-a ?type=content 2 items, postsCount=2, actor page Posts(2), 0 console errors
S79 | CLOSED | qa | actor postsCount misses Page + Question — PASS: live qa-iris-a postsCount=2 (Note+Question), actor page "Posts (2)", 0 console errors
S80 | CLOSED | qa | actor page Posts omits Page cross-posts — PASS: live qa-iris-a new user post shows in actor page Posts tab, GET outbox?type=content 200, 0 console errors
S78 | CLOSED | qa | Profile - Your posts empty (outbox ?type=content filter) — PASS: live qa-iris-a new user post shows in Your posts tab, GET outbox?type=content 200, 0 console errors
S77 | CLOSED | qa | home feed only showed own content (sort-by-date fix before MaxItems cap) — PASS: live qa-iris-a home feed shows own + followed content sorted newest-first, no console errors, 1483/1483 tests green
S76 | CLOSED | qa | NRE in Iri.get_Value on empty-IRI mention tag — PASS: live qa-iris-a normal mention renders as link, no console errors, 1506/1506 tests green
S72 | CLOSED | qa | Report button "Reported" state not restored on reload (fix: moderation cache walks /flags; QA PASS: live qa-iris-a, reported s67bob, navigate away/back, button shows "Reported ✓" disabled)