# 042 — Local post delivery and author inbox semantics

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The client had to send a new post without enumerating the author's follower set. In ActivityPub, there is no general-purpose "post to my outbox" API; the outbox is a read collection, not a write target.

This meant the author-facing write path had to route to the server that owns the author, so the server could decide how to distribute the post and record it in the correct local collection.

## Decision

A client-originated post is delivered to the author's own inbox, not directly to followers.

The local post flow is:

- the client builds a `Create` with an embedded `Note`
- the activity is signed as the author
- the message is sent to `actorId.InboxOf()`
- the server records the post in the author's outbox or local recipient scope depending on the target recipient
- the server later performs the appropriate federation flow to local followers and remote peers

This keeps the client honest: it does not know or own the server-side follower graph, and it is not expected to fan out the post itself.

## Alternatives considered

### 1. Deliver directly to followers

This is not viable because the client does not own the follower set and cannot reliably enumerate it. The server is the authoritative place to know who follows the author.

### 2. Deliver to the outbox endpoint

Outboxes are read collections, not write targets. A remote recipient cannot use the outbox as a delivery endpoint in the same way it can use an inbox.

### 3. Embed a link instead of the full object

That would force a second fetch on delivery and make idempotent retries less robust. Embedding the object avoids unnecessary remote reads and keeps the create operation self-contained.

## Consequences

- The client can post without needing follower membership state.
- The server remains the single authority for outbox ownership and distribution policy.
- Retries remain deterministic because the activity id is derived from content plus actor identity.
- Future federation steps can add true outbox fan-out without changing the client contract.

## Code alignment

The current client/server implementation follows this pattern:

- `PostNoteAsync` builds a signed `Create` for the author inbox
- the create activity embeds the note body and metadata
- the server records the note in the relevant local collection

This is the correct boundary for author-owned content in a federated system.
