# 029 — Per-actor delivery signing via X-Iris-Actor

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

Not every outbound federation event is signed by the instance actor. Some actions are performed by a specific local person or community, and a remote instance validates the signature against the actor that created the activity. If the server signs the request with the wrong identity, the remote side rejects the payload even if the syntax is otherwise valid.

This design is implemented in [src/Iris.Client/SigningHandler.cs](../../src/Iris.Client/SigningHandler.cs) and [src/Iris.Server/DeliveryWorker.cs](../../src/Iris.Server/DeliveryWorker.cs).

## Decision

Each delivery job carries an optional `ActorIri` describing the local actor who performed the automated action. The delivery worker writes that actor into the outbound request as the `X-Iris-Actor` header, and the signing handler treats that header as a per-request override of the default client identity.

The effective rules are:

- If a delivery job has an `ActorIri`, it is signed as that actor.
- If no acting actor is present, the server falls back to `ActivityPubServerOptions.InstanceActorId`.
- The same request pipeline is reused; no separate per-request client or ad-hoc signing stack is created.

This keeps the signing logic centralized while allowing actor-specific actions like Accept, Reject, and Announce to carry the correct cryptographic identity.

## Alternatives considered

### 1. Always sign as the instance actor

This was simpler, but it is wrong for remote validation. A receiver fetches the activity's `actor` document and verifies the signature against that actor's `publicKey`. If the instance actor signs an event performed by another local actor, the signature does not match the activity's claimed actor and is rejected.

### 2. Build a new client per delivery with a different identity

This would work, but it duplicates the pipeline and makes the delivery worker more complex than necessary. The project already has a single signing boundary in `SigningHandler` that understands `X-Iris-Actor`, so the per-request override is the cleaner seam.

### 3. Store the acting actor in the activity payload and let receivers infer it later

That would be semantically wrong. The signature is a proof tied to the actor's private key, so the sending side must sign with the actual private key of the actor being claimed.

## Consequences

- remote validation is accurate and consistent with the actor declared in the activity;
- the server can represent local actions as the correct identity without losing the instance-actor fallback for system-level automation;
- the delivery pipeline remains efficient: one worker, one signed client, and per-request key selection by header override.

## Code alignment

The current implementation matches the decision exactly:

- `SigningHandler.ResolveIdentity()` reads `X-Iris-Actor` first, then falls back to the handler's `ActorId`.
- `IKeyProvider.TryGetIdentity(actorIri, out ...)` resolves the key for that actor and signs the request.
- `DeliveryWorker.DeliverAsAsync()` adds `X-Iris-Actor` when `DeliveryJob.ActorIri` is present.
- `DeliveryWorker.ExecuteAsync()` still builds a client signed as the instance actor and uses the per-request header override for localized delivery identity.

This design makes remote federation validation deterministic and prevents actor mismatch errors during Accept/Reject and content propagation.
