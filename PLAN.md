# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN

S87 | OPEN | - | Profile edit Save makes no API call (bio+checkbox changes lost silently)

## CLOSED

S83 | CLOSED | qa | Article edit silently fails (Note edit works) — PASS: edit persists as Article
S85 | CLOSED | qa | Notifications page never refreshes while open — PASS: in-page re-fetch works
S86 | OPEN-QA | dev1 | Profile Likes tab renders raw IRI link instead of post content
S82 | OPEN-QA | dev1 | community feed filters to content items (ContentItems.IsContentPost) — no empty post cards
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

S62 | CLOSED | qa | reply to remote parent now notifies parent author (PASS: live s56qa->s58qa)

S61 | CLOSED | qa | admin bootstrap reads APP_ADMIN__* env (config fallback) (PASS)

S60 | CLOSED | qa | dead letters: admin list/replay (PASS: route+auth live; bodies via TestServer)

S59 | CLOSED | dev2 | signable-actor metric counted remote actors (PASS: local-only; remote excluded)


