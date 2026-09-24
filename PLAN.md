# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN

S97 | OPEN-QA | dev2 | Lemmy post Replies tab empty though header shows N comments (S43 skips walk) — FIXED: server ObjectRepliesAsync now accepts a ?iri= override (GET /ap/v1/…/replies?iri={foreign}) so a stored FOREIGN parent's reply edges (Edges Kind=3 keyed by the foreign IRI) are served instead of 404ing on the reconstructed local parent IRI; client ObjectDetail S43 gate branches: local/remote-Iris objects use the direct /replies walk, non-Iris remote parents call LoadForeignRepliesAsync which reads {home}/ap/v1/object/replies?iri={foreign} same-origin (no proxy) and parses orderedItems (IRI-string array) + resolves each note. Root cause: the object-document catch-all rebuilt a LOCAL parent IRI, so the foreign parent's /replies 404'd while iris:repliedCount (same GetRepliesAsync) was correct -> count/list disagreed. Tests (extended ForeignObjectDocumentEndpointTests): ForeignParent_Replies_ByExplicitIri_ServesRecordedReplies (200+r1) + ForeignParent_Replies_ByPath_Returns404. Full suite 1521/1496 pass/25 skip. LIVE dev2 (fresh WASM isma4yqeva): GET ?iri=...post/2 -> 200 orderedItems=4 (was 404); UI /object?iri=...post/2 Replies tab renders all 4 s94dev replies (was "No replies yet"); header "1 comments" is Lemmy's native commentCount (the 1 that landed on Lemmy) from the live-proxied doc, distinct from the tab. Note: browser WASM is content-hashed + cachebusted, but a hard reload was needed to drop the pre-fix cached bundle
S98 | OPEN-QA | dev1 | Communities page local-only; no cross-instance community discovery — FIXED (client-only): added a fourth "All known" tab to Communities.razor that loads the federated community surface. The "All on this instance" tab is unchanged (SearchAsync Type=Actor LocalOnly=true -> local Groups via OfType<Group>()); the new "All known" tab issues SearchAsync Type=Actor LocalOnly=false, which the server (S30 A8.2) resolves to the actor-store scan PLUS the community-store's cached remote Groups merged in; after OfType<Group>() that yields this instance's local communities (local Groups live in the same ActorEntity table the actor-store search scans) UNION the cached remote community Groups the instance has seen during federation. Root cause: the page only ever called LocalOnly=true, so cached remote community Groups (persisted by RemoteCommunityPersister on follow/interaction) were never surfaced anywhere in the Communities directory. Tests: none added — the server contract the tab consumes (localOnly=false Actor search surfaces cached remote Group; localOnly=true excludes it) is already covered by GlobalSearchServiceTests.Search_AllKnownCommunities_SurfacesCachedRemoteGroup_NotLocal; full suite 1494 pass/0 fail/25 skip. LIVE dev1: followed dev1-lemmy community pa93interop (Join -> Leave toggled, RemoteCommunityPersister cached it); Communities "All known" tab now lists pa93interop (remote) ALONGSIDE local dev1 communities s21dev1-test/s21fix/s21fix2/s21uiverify, while "All on this instance" tab does NOT list pa93interop (local-only) — cross-instance community discovery now works
S99 | OPEN | - | No theme/dark-mode setting in Settings (no Appearance section)
S96 | OPEN | - | Search local-only; add cross-instance post search over followed remote outboxes
S100 | OPEN | - | Lemmy site-deref of Iris instance fails: value too long varchar(20); duplicate reply Create 400

## CLOSED

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
S73 | CLOSED | qa | community join state lost on nav — PASS: live qa-iris-a join 3 communities, navigate away/back, all show Leave
S71 | CLOSED | qa | community ns#followersCount reads Follow not CommunityFollower; AP doc 1 vs 5 — PASS: live qa-iris-a AP doc ns#followersCount=5, /followers=5, UI Members(5)
S66 | CLOSED | qa | federated boost count wrong on receiving instance — NOT REPRODUCED: qa live B-boost of A note shows ns#sharedCount=1 on home A (count correct; likely misread of top-level vs ns# key)
S70 | CLOSED | qa | note edit not federated: shared inbox dropped Update (owner remote) + audience was collection (fix: fan out to local followers + merge followers into cc) — PASS: 12/12 shared inbox tests + live dev1 verification











