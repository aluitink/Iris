# 034 — Announce propagation and handler registration correctness

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The project had two distinct problems in the announcement and activity-handler pipeline:

1. The handler list used `TryAddSingleton`, which silently dropped later `IActivityHandler` registrations and meant the follow-response handlers were never reachable.
2. An `Announce` must be treated as a broadcast to local followers, not as a passive store-only operation.

The implementation is centered on [src/Iris.Server/ActivityPubServerExtensions.cs](../../src/Iris.Server/ActivityPubServerExtensions.cs), [src/Iris.Server/AnnounceActivityHandler.cs](../../src/Iris.Server/AnnounceActivityHandler.cs), and the `IInboxProcessor` pipeline.

## Decision

Iris uses an open list of activity handlers and records local follower propagation in the announce handler.

Specifically:

- Each handler is registered with `AddSingleton<IActivityHandler, X>()` so the `IEnumerable<IActivityHandler>` pipeline includes every concrete implementation.
- `AnnounceActivityHandler` records the announce in the recipient's outbox when the recipient is a local actor.
- It then propagates the announce to each local follower's inbox, signed as the announcer, so the follower instance verifies against the announcer's actual key.
- The propagated copy reuses the original announce IRI to keep delivery idempotent and prevent duplicate local records.

## Alternatives considered

### 1. Keep `TryAddSingleton` for the handler list

This silently masked later registrations and left the follow lifecycle partly dead. It was a purity problem in DI registration, not a business-logic issue.

### 2. Treat `Announce` as a store-only event

This would prevent local follower feeds from seeing boosts and would make the system drift away from the ActivityPub model of a re-share propagating through follower graphs.

### 3. Propagate to all followers, including remote ones, from the same instance

This is not the current in-scope model. Remote followers are the remote instance's job, and local propagation is the verifiable first step.

## Consequences

- The follow/accept/reject pipeline is discoverable and executes consistently.
- `Announce` works as a propagation event for local follower delivery.
- The activity pipeline is extensible for future handlers without silent drops.
- Re-delivery of the same announce remains deduplicated at the IRI level.

## Code alignment

The current implementation reflects the decision:

- `AddActivityPubServer()` registers the handlers by `AddSingleton`, not `TryAddSingleton`.
- `AnnounceActivityHandler` saves the announce to the recipient's outbox and re-sends it to each local follower.
- The handler includes the per-actor signing path by passing the announcer as the acting actor to `DeliverToActorAsync`.

This is the correct architecture for an extensible handler pipeline and for local boost propagation.
