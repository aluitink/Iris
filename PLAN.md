# PLAN

Ledger for the agent loop. One line per item. Rules: docs/PROTOCOL.md.

## GOAL

- Iris: .NET ActivityPub client + server. Fediverse interop with Lemmy and Mastodon must work end-to-end.
- Quality bar: `dotnet test` green, live-verified on a dev stack before an item leaves OPEN-QA.
- This file is the only shared working doc. Keep it under the caps in docs/PROTOCOL.md.

## OPEN

S50 | OPEN-QA | dev2 | cross-instance community post uses documents/ IRI -> 404 on remote
S53 | OPEN-QA | dev2 | community-scoped search returns 0 (in-memory feed substring match; remote 404)
S55 | OPEN-QA | dev1 | post edit UI stale (CONFIRMED Pass 343: feed serves stale embedded Create after Update; delete clean)

## NEW

S56 | OPEN-QA | dev2 | remote actor profile shows Posts (0) though their posts appear in the timeline

## CLOSED

S54 | CLOSED | dev1 | input buttons disabled while typing (oninput binding)
S52 | CLOSED | dev1 | community post ignored post type (Article/Poll)
S51 | CLOSED | dev1 | community edit stale updated timestamp
S49 | CLOSED | dev1 | community feed empty after posting (creator not a follower)
S47 | CLOSED | dev1 | community edit form silent stale submission
S44 | CLOSED | dev1 | post More-options Mute no-op
