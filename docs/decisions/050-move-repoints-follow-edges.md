# 050 — `Move` re-points local follow edges and invalidates stale federation caches

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

ActivityPub `Move` is the migration signal for an actor that has changed identity. The server needed to interpret a `Move` as a relationship-update event rather than as a no-op or an unrelated actor event.

Without this behavior, a migrated actor would retain stale follow edges pointing at the old IRI, and remote peers would continue resolving the old identity for follow state and key lookups.

## Decision

When the server receives a `Move` activity, it re-points local follow edges whose target matches the moved actor's old IRI to the new IRI.

The logic works by target, not by recipient, because a `Move` is delivered to the mover's followers, not to the moving actor itself. The handler therefore:

- looks up local follow edges targeting the old IRI
- re-points them to the new IRI for people and communities
- keeps remote follower state alone, because remote instances own their own follow maps

The handler also invalidates the moving actor's outbound cache entries for the old actor document and key resolution so the next fetch resolves the new identity rather than stale state.

## Alternatives considered

### 1. Ignore `Move` activity types

This leaves outdated follow state behind and creates stale references to the old identity.

### 2. Gate the handler on the recipient acting as the moving actor

This is incorrect for `Move` semantics: the message is delivered to followers, not to the moving actor itself.

### 3. Re-point only the actor store and leave follow edges untouched

This would update the identity but not the local relationship graph, and would still preserve stale follow data.

## Consequences

- Follow-state remains correct after an identity move.
- Local community follow relationships also update correctly.
- The server avoids reusing stale remote key/document caches after a move.
- The implementation keeps the relationship graph consistent with the actor's new IRI without requiring a full graph rewrite.

## Code alignment

The implementation reflects the decision:

- `MoveActivityHandler` enumerates local follow edges by the old actor IRI
- person followers and community follows are both re-pointed
- stale document and key cache entries are invalidated
- the handler is registered via the server's activity handler list

This closes the final Wave 1 federation gap and keeps local follow relationships aligned with actor migration.
