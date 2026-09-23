# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN

## NEW

S69 | NEW | - | cross-instance direct note not federated to recipient (remote IRI not found, no notification)
S68 | NEW | - | Poll federation: remote home/search miss Question Object; remote vote not federated back (origin stays 0)
S67 | NEW | qa | cross-instance mention local path + no notification; remote search misses federated mention (FAIL: live s58qa->s56qa; mention post missing from s56qa home + no mention notification)
S66 | NEW | qa | federated boost count shows 0 on receiving instance (Shares tab + notification correct, only count wrong) (REPRODUCED: origin boost=1, remote home boost=2)
S65 | NEW | qa | federated posts from followed remote users not in home feed (accessible via IRI) (FAIL: live s58qa->s56qa; s56qa post still missing from s58qa home)
S64 | NEW | - | community Article post not appearing in community feed (post visible on author profile)
S63 | NEW | - | prod /ap/v1/health degraded: 13 dead letters + 40/4547 actors unresolvable

## OPEN-QA

## CLOSED

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