# 73.6 — Poll voting (server endpoint + client + interactive UI)

## Summary

Completes the poll feature (create + view + vote). Adds a server-side vote
endpoint, a client `VoteAsync`, and interactive poll options in the UI.

## Changes

### Server: `POST /local/v1/u/{handle}/votes/{**pollIri}`

- **Route**: registered on the `/local/v1` group (not `/ap/v1` — a poll vote
  is a local, non-federated write, not an ActivityStreams activity).
- **Auth**: Basic auth or cookie auth (same pattern as `LocalMuteHandler`).
- **Body**: `{"option": <zero-based index>}`.
- **Logic**: fetches the stored object, verifies it is a poll (has a `poll`
  extension or `options` array), checks it is not expired, records the voter
  in `poll.voters`, increments the option's vote count, stores the updated
  object. Returns 200 with the updated poll data.
- **Errors**: 401 (unauthorized), 400 (bad option index / not a poll), 404
  (poll not found), 409 (poll expired).
- **Idempotency**: re-voting the same option is a no-op (returns current
  poll data without re-incrementing).

### Client: `ILocalModerationClient.VoteAsync`

- `VoteAsync(Iri actorId, Iri pollIri, int optionIndex, CancellationToken ct)`.
- POSTs JSON `{"option": <index>}` to the local vote endpoint.
- Uses the same handler resolution pattern as other local-moderation calls
  (Basic auth handler or cookie-auth passthrough).

### Core: `IriExtensions.GetPollDataFromJson`

- Parses the server's poll-vote response body into a `PollData` record.
- Used by the UI to update the poll display after a vote.

### UI: `ObjectView.razor`

- Poll options are now clickable when signed in, the poll is not expired, and
  the user has not already voted.
- Clicking calls `VotePollAsync(optionIndex)` which invokes
  `Session.LocalModeration.VoteAsync(...)`.
- On success: shows a check mark on the selected option, updates the
  bars/counts from the server response, and shows "You voted" in the footer.
- Both poll rendering sections (Create-embedded and bare object) are updated.

### CSS: `app.css`

- `.object-poll-option-votable` — pointer cursor + hover highlight.
- `.object-poll-option-selected` — stronger bar background for the chosen option.
- `.object-poll-option-check` — check mark style.
- `.object-poll-voted` — "You voted" indicator style.

## Verification

- Build: 0 warn / 0 err. Full suite: 1665 passed, 0 failed, 17 skipped.
- Live (Playwright on `:8088`):
  - Posted a poll (2 options: C#, Rust) via compose (HTTP 202).
  - Navigated to the poll's object page; both options rendered with 0 votes.
  - Clicked "C#" → UI updated: C# = 1, Rust = 0, "You voted" shown, total = 1.
  - Server state confirmed: `poll.voters = [actor IRI]`, `votesCount = 1`,
    `totalVotes = 1`.
  - Re-clicked "C#" → no change (idempotent, still 1 vote).

## Design decision

Poll voting is a **local, non-federated** operation (like mutes/relays), not
an ActivityStreams activity. There is no AS2 `Vote` type; Mastodon uses a
dedicated REST endpoint. Iris follows the same pattern: a local
`/local/v1` endpoint that records the vote on the stored object. The vote is
not propagated to remote instances (the poll object's `voters` array is
authoritative on the home instance; remote instances that hold a federated
copy of the poll will not see individual votes — this matches Mastodon's
behavior where votes are local to the instance that hosts the poll).
