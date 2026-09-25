# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN-QA
S135 | OPEN-QA | dev1 | Appearance: Reduce motion + Larger text (accessibility) — dev1 verified: reduce-motion root class disables transitions; text-larger root class scales font; both persisted across reload; 0 console errors; 1502 passed

## OPEN
S134 | OPEN | - | Notification-row per-actor Mute/Block: notification rows attributed to an actor (Like/Follow/Mention/Boost) have no quick moderation — add a small Mute + Block action on the row (hidden for follow-request rows and self), calling ILocalModerationClient.MuteAsync / IActivityPubClient.BlockAsync. Verify: a Like notification from actor X shows Mute → click mutes X (appears in Settings→Muted, X's posts vanish from home); a Block action blocks X; 0 console errors.
## CLOSED

S127 | CLOSED | qa | Compose: post language selector; language shown on post card + in feed — PASS: live qa-iris-a Spanish post shows "es" badge on feed card + object page; 0 console errors
S133 | CLOSED | qa | Post visibility indicator: Followers-only/Direct badge — PASS: live qa-iris-a followers-only post → "Followers-only" badge on profile+object page; DM → "Direct" badge + "To s116rcv"; public post → no badge; 0 console errors
S130 | CLOSED | qa | Notifications Likes: remove redundant Liked wrapper frame — PASS: live qa-iris-a (fresh build) s116snd sees s116rcv like notification in Likes tab → post card renders directly with author/time/content/engagement buttons, no "Liked" header or wrapper frame; 0 console errors

S132 | CLOSED | qa | Messages folded into Home as 4th tab — PASS: live qa-iris-a (fresh build) /messages redirects to /home with Messages tab active; FeedBar has Posts/Local/Communities/Messages tabs; MessagesPanel shows DM list with All/Received/Sent sub-filters + Mark all as read; s116snd sees 3 DMs (Sent to s119qa, Received from s119qa, Sent to s116rcv); 0 console errors
S131 | CLOSED | qa | compose?ReplyTo renders raw markup — PASS: live qa-iris-a (fresh build) compose?replyTo=<HTML note> renders preview with clickable @s116snd mention link + "S119-QA-DM-TEST-1" text (not raw HTML); HTML detected via IsPreRenderedHtmlContent, rendered as MarkupString; 0 console errors
S129 | CLOSED | qa | Notifications follows: track accept/decline state — PASS: live qa-iris-a (fresh build) s116snd (manuallyApprovesFollowers=true) sees s116rcv follow request in /notifications; Accept → "✓ Follow accepted" (edge Kind 0 created, Kind 17 removed); Decline → "✗ Follow declined" (Kind 17 removed, no Follow edge); 0 console errors
S128 | CLOSED | qa | Directory infinite scroll loads last page repeatedly — PASS: live qa-iris-a (fresh build 1905c2f0) /ap/v1/search?type=Actor&limit=20 all 4 pages return totalItems=63, next links carry type=Actor (page 1→offset=20, page 2→offset=40, page 3→offset=60, page 4→NONE); UI "All known" scroll loads 55 cards, no sentinel after last page, no repeated content; 0 console errors
S126 | CLOSED | qa | Directory People: sort (default/name) + host filter to find accounts — PASS: live qa-iris-a (fresh WASM) /directory "All known" shows Sort by (Default/Name) + Filter by host (All hosts/mastodon.social/qa-iris-a.luit.ink/qa-iris-b.luit.ink/qa-lemmy.luit.ink); Name sort alphabetical (ab, alice, gnomon, hunt89, ii-a1...); host filter qa-iris-b shows only 7 iris-b accounts (ii-b1, probep, s56qa, s56qav, s67bob, s68qa, s68vb); filters hidden in "This instance" mode; 0 console errors
S125 | CLOSED | qa | Home: signed-in Local tab shows instance public timeline (now authless-only) — PASS: live qa-iris-a Local tab fetches /ap/v1/public/feed, shows posts from s116rcv (S124-TEST-POST-FROM-RCV), s116snd (S118-QA-ALT-TEXT-POST, S119-QA-DM-REPLY-1, S116-QA-DM-ROUNDTRIP-2); 1 console error (404 on S119 DM note, unrelated); 0 S125-related errors
S124 | CLOSED | qa | Feed flip after post — FALSE POSITIVE (stale WASM cache, same root cause as S122): live qa-iris-a (wasm t1q33tso4m) s116rcv follows s116snd, posts S124-TEST-POST-FROM-RCV; feed shows both s116rcv's post (newest) + s116snd's S118 post (1h ago), ordered by publish time; server logs confirm 4 items built (Create=4); 0 console errors
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


