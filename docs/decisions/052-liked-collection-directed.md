# 052 — `liked` collection is directed and local-only for remote likers

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The server needed to model the `liked` collection as an ActivityPub actor property without confusing it with a global object store or a remote-actor feed. The earlier implementation had no data structure for this concept, and even the collection route itself was absent.

The design needed to answer two questions clearly:

1. What does the `liked` collection represent?
2. When should a remote like be recorded locally?

## Decision

The `liked` collection is directed: it lists the objects liked by a specific actor, not an aggregate list of all likes on the platform.

The underlying store therefore keys by liker IRI, with each entry representing the set of objects that actor has liked. A local actor's `liked` collection is served from that set, and a remote liker's `Like` is not recorded locally unless the liker is itself a local actor.

A `Like` delivered to a local community still contributes to the community feed by recording it in each local member's outbox, but that is a community-feed propagation path, not a local `liked` collection write for the remote actor.

## Alternatives considered

### 1. Record every incoming `Like` in a global store by object IRI

This would blur the distinction between the actor's own likes and the platform-wide likes, and would make the actor's `liked` collection impossible to represent correctly.

### 2. Record remote likers locally as if they were local actors

This would create incorrect local state and would misrepresent who actually owns the `liked` collection.

### 3. Omit the `liked` collection entirely

This leaves a major ActivityPub and federation surface unimplemented and prevents a local actor from exposing the objects they liked.

## Consequences

- The `liked` collection is a true actor-centered list.
- Local actor state remains properly scoped to the local identity.
- Remote actors are not incorrectly treated as local members of the platform.
- Community feed propagation remains separate from personal `liked` storage.

## Code alignment

The implementation reflects the decision:

- `ILikeStore` is keyed by liker IRI and stores liked-object IRIs
- `LikedOf()` identifies the actor's `liked` collection
- `LikeActivityHandler` records only local likers in the local `liked` collection
- the collection is served as a plain ordered collection of object IRIs

This keeps the actor identity model accurate while still allowing community feed propagation for content-like events.
