# 028 — Two-sided follow lifecycle and provisional follower state

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

A follow is not a one-way edge in the abstract. In ActivityPub there are two actors involved: the followed party and the follower. The followed party records the directed edge immediately when it receives the Follow, but the follower itself does not finalize its own local follow relationship until it receives the followed actor's response.

The implementation is centered on [src/Iris.Server/FollowActivityHandler.cs](../../src/Iris.Server/FollowActivityHandler.cs) and the follow-response handlers in [src/Iris.Server/AcceptActivityHandler.cs](../../src/Iris.Server/AcceptActivityHandler.cs) and [src/Iris.Server/RejectActivityHandler.cs](../../src/Iris.Server/RejectActivityHandler.cs).

## Decision

Iris models the follow handshake as a two-sided state machine:

- On the followed side, `FollowActivityHandler` records the directed edge `follower -> recipient` immediately when the recipient is a local actor or community.
- The same handler schedules an `Accept` back to the follower's inbox.
- On the follower side, the follow is provisional. The follower's local edge is not made authoritative until it receives the `Accept`.
- If the followed side sends a `Reject`, the follower's provisional edge is removed.

This produces the correct ActivityPub semantics without letting the follower side own the follow state of the followed side. Each instance remains authoritative for the local follow edges it directly owns.

## Alternatives considered

### 1. Finalize the edge immediately on both sides

This would make the follower side authoritative too early. A remote follow is not meant to be treated as final until the target actor accepts it, and doing so would produce false-positive social state.

### 2. Require the sender to approve all follow requests locally before recording the edge

This would work only for a local moderation model. It would not match the normal federation flow where the followed side owns the approval decision and the follower side only completes its own state after the response.

### 3. Let the followed side keep the follow edge but never schedule a response

This would make follow requests non-interactive and would block the standard ActivityPub approval/rejection cycle.

## Consequences

- The followed side can reveal the relationship quickly while the remote follower may still be pending.
- The follower side retains a clean provisional-to-final transition.
- `Accept` and `Reject` responses are authoritative at the point they are received by the follower instance.
- The system remains compatible with the standard ActivityPub pattern of follow → accept/reject.

## Code alignment

The current implementation matches the decision:

- `FollowActivityHandler` records the server-side edge when the recipient is local and delivers the `Accept`.
- `AcceptActivityHandler` records the follower-side `follower -> target` edge.
- `RejectActivityHandler` removes the provisional edge when the target rejects.
- The code uses the delivery recipient as the authoritative target when resolving the local actor or community that received the follow.

This design keeps the two instances’ view of follow state consistent while preserving the causal order of the handshake.
