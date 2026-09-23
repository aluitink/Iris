# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN

## NEW

S62 | NEW | - | reply's to/cc omits parent author: no notify + no cross-instance delivery

## OPEN-QA

S61 | OPEN-QA | dev1 | admin bootstrap reads wrong env key; no admin in any env (blocks admin QA)
S60 | OPEN-QA | dev1 | dead letters: admin list/replay (store RemoveAsync + GET/POST /local/v1/admin/dead-letters[/{i}/replay] RequireRole(Admin) + DeadLetterPanel on /admin; 6 tests green; route+auth live 302, replay 400 distinct from SPA fallback)

## CLOSED

S59 | CLOSED | dev2 | signable-actor metric counts remote actors (PASS: local-only 15/15 live on qa; remote excluded; endpoint + health consistent)

S58 | CLOSED | dev1 | admin dashboard surfaces dead letters + signable-actor gap (PASS: endpoint fields + live /admin clean)

S50 | CLOSED | dev2 | home feed omits author's own cross-post Page (PASS re-verify: live home feed now shows the cross-posted Page — 2nd fix IsCommunityActorIri exempts community `to` from IsFollowReply's reply fallback; strengthened regression test fails w/o fix; 1462 server tests green)

S57 | CLOSED | dev2 | cross-post to local community dropped by shared-inbox (PASS: S57 test + 22/22 fed tests green; fix on qa)

S54 | CLOSED | dev1 | input buttons disabled while typing (oninput binding)
S52 | CLOSED | dev1 | community post ignored post type (Article/Poll)
S51 | CLOSED | dev1 | community edit stale updated timestamp
S49 | CLOSED | dev1 | community feed empty after posting (creator not a follower)
S47 | CLOSED | dev1 | community edit form silent stale submission
S53 | CLOSED | dev2 | community-scoped search returns 0 (Pass 345: search + feed return nested content)
S55 | CLOSED | dev1 | post edit UI stale (Pass 344: fresh nav + post-edit save render current note content)
S44 | CLOSED | dev1 | post More-options Mute no-op
S56 | CLOSED | dev2 | anonymous remote actor Posts (0) (Pass: anon proxy first-page follow renders post)
