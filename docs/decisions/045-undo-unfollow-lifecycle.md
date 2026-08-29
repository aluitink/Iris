# 045 — `Undo` is the inverse of `Follow`

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The system needed a first-class inverse for a follow relationship. A bare edge removal without a semantic ActivityStreams wrapper would not match the federation model and would leave the follow lifecycle incomplete.

The `Undo` activity is the proper inverse primitive in ActivityPub and should be used when a local actor no longer wishes to follow a target.

## Decision

An unfollow is represented as an `Undo` of the original `Follow` activity.

When processing the `Undo`:

- the handler resolves the referenced `Follow` activity
- it reads the target actor or community from that original activity
- if the recipient is a local actor, it removes the local follow edge
- if the target is a local community, it removes the local community follow entry
- if the referenced object is missing or not a `Follow`, the operation is a no-op

This preserves the distinction between the social edge and the outbox record while doing the semantic inverse of the follow relationship.

## Alternatives considered

### 1. Remove the follow edge directly without `Undo`

This would not preserve the standard ActivityPub semantics and would leave the edge lifecycle inconsistent with the rest of the federation model.

### 2. Treat `Undo` as a generic no-op on local data

This would make the follow lifecycle impossible to reason about consistently and would miss the required inverse operation for federated behavior.

### 3. Remove both edge and outbox record in one step

This is too broad. The semantic inverse is the follow edge itself; the outbox record is a separate concern and may remain for later cleanup or inspection.

## Consequences

- Follow lifecycles remain compatible with ActivityPub semantics.
- Local edge state changes require a single, clear inverse operation.
- Future follow and unfollow flows can rely on the same deterministic ID pattern and handler semantics.
- The server remains precise about what is being undone: the edge, not a broader outbox artifact.

## Code alignment

The current implementation reflects the decision:

- `Undo` is a first-class activity type for follow inversion
- the local edge is removed when the recipient is local
- non-`Follow` or missing referenced objects are ignored safely

This is the correct semantic model for follow lifecycle management.
