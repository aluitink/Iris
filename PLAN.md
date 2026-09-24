# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN-QA


## OPEN

S124 | OPEN | agent-a | When the user posts, the feed flip completely to only the users content. The posts should be a combination of users posts/boosts/likes and followed actors posts/boosts/likes ordered by publish time.
S125 | OPEN | - | Home: signed-in Local tab shows instance public timeline (now authless-only)
S126 | OPEN | - | Directory People: sort (most active/newest) + host filter to find accounts
S127 | OPEN | - | Compose: post language selector; language shown on post card + in feed
## CLOSED

S121 | CLOSED | qa | Media alt text: render author alt on img (gallery+lightbox) — PASS: live qa-iris-a (wasm t1q33tso4m) s116snd profile shows S118 post with img alt="S118-QA-ALT-TEXT-TEST: A red circle on a blue field" (author alt, not filename); 0 console errors
S119 | CLOSED | qa | Send a DM from a profile: "Message" button on ActorDetail (next to Follow, hidden for self/Group) -> /compose?dmTo=<actorIri>; compose reads dmTo (LoadDmRecipientAsync: forces Visibility=direct + disabled, seeds Content "@handle ", "Messaging X" header); Messages empty-state "Find someone to message" -> /directory — PASS: live qa-iris-a (wasm d8xp549r0o) s116rcv on s116snd profile sees Message btn, click -> /compose?dmTo=..s116snd, header "Messaging s116snd — direct", content "@s116snd", visibility=direct+disabled; fresh acct s119qa /messages empty-state "Find someone to message" -> /directory; 0 console errors

S120 | CLOSED | qa | DM inbox: UI-posted Direct messages don't appear in /messages (received or sent) — PASS: live qa-iris-a (build fcfd6302) s116rcv /messages shows received DM from s116snd (create 06GDASKRST5DDV19MYZ2FXV7JM), s116snd /messages shows sent DM to s116rcv; 0 console errors both sides

S118 | CLOSED | qa | Media alt text: compose lets you set alt text on image attachments; serialized to Image alt — PASS (alt round-trips to public doc; blank alt dropped; 0 console errors)

S117 | CLOSED | qa | Mention autocomplete: @ in compose shows matching accounts — PASS: live qa-iris-a (wasm q165eix1rz) type @s117 -> popover "s117target | S117 Target Name", click -> "@s117target" in DOM + popover closed; hashtag #s117 accept still works; post persisted w/ mention link; 0 console errors
S115 | CLOSED | qa | Hashtag timeline: #tag in a post links to /tag/{tag} listing posts — PASS: live qa-iris-a (wasm q165eix1rz) #s115qatag href=/tag/%23s115qatag, timeline "1 post(s)" + card, empty tag "No posts… yet", 0 console errors
S114 | CLOSED | qa | Edit/Delete own posts from feed card — PASS: live qa-iris-a (wasm kzz9lgxzn4) own post shows .engagement-more menu w/ Edit+Delete; Edit deep-link -> prefilled editor + Save; Delete confirm -> tombstone "Deleted post"; 0 console errors
S111 | CLOSED | qa | Bookmarks: save posts to a bookmarks collection; add Bookmark button + profile Bookmarks tab — PASS: live qa-iris-a bookmarked post persists aria-pressed=true after reload; Bookmarks tab renders full post card w/ engagement bar; unbookmark clears; 0 console errors

S112 | CLOSED | qa | Media lightbox: clicking a post image opens full-screen overlay with prev/next nav — PASS: live qa-iris-a created S112-QA-IMAGE-POST (1x1 PNG via compose); clicked .media-gallery-item img in home feed -> .lightbox-overlay opens w/ Close btn (×), URL stayed /home (no object-page nav); Close dismissed overlay (0 elements); same on profile Your-posts tab; deployed /css/app.css .media-gallery-item z-index:1 confirmed live; 0 console errors
S113 | CLOSED | dev2 | CW rendering: posts with contentWarning show CW banner + hidden content behind Show button — VERIFIED (already implemented): live dev2 CW text post shows .object-sensitive banner (summary "S113-CW-TEST" + "This content may be sensitive." + Show btn), content blur(8px) until Show (filter none after); CW+media post media-gallery-wrap blur(8px) until Show; 0 console errors
S110 | CLOSED | qa | Home feed made one /flags call per ObjectView (N posts = N calls) — PASS: live qa-iris-a fresh WASM (23f09rii0f) home feed 6 posts (4 own s106qview + 2 foreign s105follow) -> exactly 1 /flags + 1 /blocks + 1 /mutes (single cached WalkModerationAsync, 2min TTL), not N; initial 2-4 /flags was stale browser-cached OLD wasm (i0f4v109nu, now 404) until cache cleared; 0 console errors
S109 | CLOSED | qa | Video in feed streams redirects to object page on play click instead of playing in-place — PASS: live qa-iris-a video post in home feed; .media-player computed z-index:1/position:relative, .object-card-link (stretched) z-index:0/absolute; clicked video center -> URL stayed /home (no object-page nav); served app.css .media-player in z-index:1 group, stale .object-media gone; 0 console errors
S107 | CLOSED | qa | Copied links for a post should generate a front end link instead of an Activity Pub object link — PASS: live qa-iris-a Copy link on 2 posts copies front-end /object?iri= URL (not AP IRI), matches stretched "Open post" href, opens object view; 0 console errors
S106 | CLOSED | qa | Notifications page too wide on mobile (horizontal scroll) — PASS: live qa-iris-a 390px no h-scroll (docScrollW=390), long display name ellipsized, .notif-card-wrapper min-width:0; desktop 1280px unchanged; 0 console errors
S105 | CLOSED | qa | Home feed post is missing user posts; profile "Your posts" tab should show only own posts — PASS: live qa-iris-a (s105qa follows s105follow; s105qa own post S105-QA-OWN-POST-3MVW8 + s105follow posts S105-FOLLOW-POST-7XK9Z pre-follow & S105-FOLLOW-POST-2-AFTERFOLLOW-9TQP4 post-follow). Server GET /ap/v1/u/s105qa/outbox?type=content totalItems=1 (own Create only, attributedTo s105qa); unfiltered outbox keeps followed/foreign (Follow + own Create, s105follow posts federate via inbox); Profile "Your posts" tab = 1 listitem (own post only, no s105follow posts); home feed shows all 3 (own+followed, not regressed); 0 console errors.
S108 | CLOSED | qa | Profile "Your posts" pages through tons of content; outbox should be lean (only actor's objects) — PASS: same fix as S105 (identical OutboxItemIsAuthoredBy predicate on ?type=content/ ?type=reply). Live qa-iris-a: ?type=content returns only actor-authored content (totalItems=1, own post), profile "Your posts" tab no longer pages through followed/foreign posts; unfiltered outbox unchanged. 0 console errors.
S104 | CLOSED | qa | Profile tabs — Replies tab empty for users whose replies sit past PagedCollection's 3-page top-up cap — PASS: live qa-iris-a (s101hunt2) Replies tab renders 2 replies w/ 'In reply to' context; server ?type=reply totalItems=2 (excludes 7 non-reply outbox items), first link carries ?type=reply (pagination preserves filter); ?type=content totalItems=4 unchanged; unfiltered=9; 0 console errors
S103 | CLOSED | qa | Profile banner image upload — PASS: live qa-iris-a (s101hunt2) edit form has Banner section; upload PNG -> preview + Change/Remove banner; Save persists AS image to actor doc (/ap/v1/u/s101hunt2 image=media URL), header renders it, media served 200 image/png; full-reload re-entry shows saved preview + Remove; Remove+Save clears image (null/absent); 0 console errors
S100 | CLOSED | qa | Lemmy site-deref of Iris instance fails: value too long varchar(20); duplicate reply Create 400 — PASS: live qa-iris-a (s101hunt2) follow s50qa -> Lemmy site row 'iris-qa-iris-a.luit.' (len 20, fits varchar(20)), no 'value too long' in logs; reply to /post/1 landed (comment id 2, single copy, person resolved); residual 400 is Lemmy's own announce re-insert (community::announce::receive -> insert_received_activity) = Lemmy-side quirk, comment lands; 0 console errors
S102 | CLOSED | qa | Home Communities tab showed Posts content (own posts) — PASS: live qa-iris-a (s101hunt2) own personal post S102QA-PERSONAL-4K7M shows in Posts tab (?source=people) but NOT in Communities tab (shows "No community posts yet" empty state, ?source=communities); network only ?source=people/?source=communities, no unfiltered /feed follow-up (server `first` link now carries ?source= -> client fast path); 0 console errors
S99 | CLOSED | qa | No theme/dark-mode setting in Settings (no Appearance section) — PASS: live qa-iris-a (s95qa) Settings>Appearance tab present; Dark default (data-theme=null, ls dark); switch Light -> data-theme="light" + ls light + PUT {theme:"light"} + GET round-trips + DB NotificationPrefsJson {"Theme":"light"}; hard reload -> early-paint light (no dark flash), Light radio pre-checked from server; revert Dark -> data-theme removed + DB {"Theme":"dark"}; 0 console errors
S96 | CLOSED | qa | Search local-only; add cross-instance post search over followed remote outboxes — PASS: live qa-iris-a signed-in s95qa (follows qa-lemmy s50qa) search ?q=S96XSEARCH-7Q4LZ returns the nested remote post/2 (Announce{Create{Page}}, not in local store) via cross-instance pass; opens + renders (lemmyadmin/s50qa); 0 console errors
S98 | CLOSED | qa | Communities page local-only; no cross-instance community discovery — PASS: live qa-iris-a "All known" tab lists qa-lemmy s50qa (remote) + local s50qa-community; "All on this instance" excludes remote; 0 console errors
S97 | CLOSED | qa | Lemmy post Replies tab empty though header shows N comments (S43 skips walk) — PASS: live qa-iris-a /object?iri=...post/1 Replies tab renders s95qa reply (server ?iri= 200 orderedItems=1); header 1 comments agrees; 0 console errors
S94 | CLOSED | qa | Iris->Lemmy reply Create 400 dead-lettered; Lemmy inbox rejects the Note reply — PASS: live qa-iris-a s95qa replied to Lemmy /post/1, note to[] carries full-IRI activitystreams#Public, comment id 1 landed (path 0.1, by s95qa)

