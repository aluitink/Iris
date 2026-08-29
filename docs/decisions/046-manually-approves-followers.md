# 046 — `manuallyApprovesFollowers` defers acceptance without dropping follow state

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

Some ActivityPub actors intentionally require manual approval before accepting follows. The project needed to represent that state without forcing a moderation UI to appear in the same slice as the core follow-handling logic.

The challenge was to preserve the follow edge for eventual delivery while suppressing the auto-generated `Accept` until an operator explicitly responds.

## Decision

A person actor may carry `manuallyApprovesFollowers` in its extension data.

When a follow arrives for that person:

- the follow edge is still recorded
- the system does not auto-schedule an `Accept`
- the operator later sends an explicit `Accept` or `Reject`

The flag is person-specific. Communities continue to auto-accept follows by default.

## Alternatives considered

### 1. Drop the follow edge until approval arrives

This would break the integration path by removing the follow relationship before a human decision is made.

### 2. Auto-accept in all cases

This ignores the explicit moderation semantics of the field and violates the actor's declared mode.

### 3. Add a dedicated moderation API in the same slice

This would be broader than the core behavior and is intentionally deferred; the state and explicit response path are the required minimum.

## Consequences

- Local follow state remains available for content delivery decisions.
- Remote senders can observe the actor's moderation mode through the public actor document.
- A human operator can explicitly accept or reject a pending follow without adding a full moderation subsystem.
- The server remains compatible with both auto-accept and manual-approval modes.

## Code alignment

The implementation follows the decision:

- follow edges are stored even when auto-accept is suppressed
- the actor document exposes the flag when enabled
- explicit `Accept` or `Reject` delivery remains the operator-driven path

This is the minimal behavior needed to support manual approval while preserving the underlying federation data model.
