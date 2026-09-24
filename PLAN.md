# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN

S90 | OPEN-QA | dev1 | Mark edited posts: show "edited" label on objects that were updated via Update
S92 | OPEN | - | Lemmy interop: iris user follows lemmy community; posts reach iris home feed

## CLOSED

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
S69 | CLOSED | qa | direct note not federated via UI /outbox (fix: local inbox leg + Note type preservation) — PASS: 3/3 S69 tests + live direct note post (HTTP 202) on qa stack
S68 | CLOSED | qa | poll not stored locally on remote (IRI 404; no vote UI) — PASS: attributedTo rewrite fix verified live on qa stack

S67 | CLOSED | qa | cross-instance mention: notify + search by mention (PASS: live qa-iris-a/b)
S64 | CLOSED | qa | community Article post now in community feed (PASS: live qa-iris-a)

S63 | CLOSED | qa | health check reports signable gap + signable_actors data (PASS: live qa-iris-a)










