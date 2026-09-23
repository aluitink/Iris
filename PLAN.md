# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN

## NEW

## OPEN-QA

S58 | OPEN-QA | dev1 | surface prod health degraded (dead letters, 40/4537 signable) in UI

## CLOSED

S50 | CLOSED | dev2 | home feed omits author's own cross-post Page (PASS: Feed_OwnCrossPostPage_SurfacesInHomeFeed + 109 feed/crosspost tests green)

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
