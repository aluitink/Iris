# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN



## NEW

S72 | NEW | - | Report(Flag) button state not restored on reload: _reportedAuthorIri in-memory only; server dedup OK
S73 | NEW | - | community join state lost on nav: /communities shows Join, AP ns#followersCount=2, Members lists user

## CLOSED

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

S58 | CLOSED | dev1 | admin dashboard surfaces dead letters + signable-actor gap (PASS: /admin clean)

S50 | CLOSED | dev2 | home feed omits author's own cross-post Page (PASS: live feed shows cross-post)

S57 | CLOSED | dev2 | cross-post to local community dropped by shared-inbox (PASS: fed tests green)

S54 | CLOSED | dev1 | input buttons disabled while typing (oninput binding)
S52 | CLOSED | dev1 | community post ignored post type (Article/Poll)
S51 | CLOSED | dev1 | community edit stale updated timestamp
S49 | CLOSED | dev1 | community feed empty after posting (creator not a follower)
S47 | CLOSED | dev1 | community edit form silent stale submission
S53 | CLOSED | dev2 | community-scoped search returned 0 (Pass 345: search + feed return content)
S55 | CLOSED | dev1 | post edit UI stale (Pass 344: fresh nav + save render current note content)
S44 | CLOSED | dev1 | post More-options Mute no-op
S56 | CLOSED | dev2 | anonymous remote actor showed Posts (0) (Pass: anon proxy follow renders post)