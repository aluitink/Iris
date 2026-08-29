# 027 — Inbound key reconstruction and federation identity

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

When Iris receives a signed inbound ActivityPub request, it must verify the signature against the sender's actual public key. The verification flow cannot rely on a local `IKeyStore` because the sender is remote and its key lives in a remote actor document. The same need appears when an instance must present an actor identity for outbound fetches: the instance must have an explicit signing identity, not a best-effort surrogate.

The current code is in [src/Iris.Server/RemoteInboundKeyResolver.cs](../../src/Iris.Server/RemoteInboundKeyResolver.cs), [src/Iris.Server/IrisActorDocumentFetcher.cs](../../src/Iris.Server/IrisActorDocumentFetcher.cs), and [src/Iris.Server/ActivityPubServerExtensions.cs](../../src/Iris.Server/ActivityPubServerExtensions.cs).

## Decision

Iris separates the two concerns cleanly:

1. Inbound signature verification resolves a remote actor's key by fetching that actor's public document and reading its `publicKey` material.
2. Outbound federation uses an explicit `ActivityPubServerOptions.InstanceActorId`, which is the local actor the instance signs as when making fetches or delivering automation.

The inbound path reconstructs a public-only signing key from the remote actor document. It normalizes both JWK and PEM representations to a uniform JWK cache entry, then reconstructs a verification key when needed. The key cache is intentionally keyed by the key IRI, while the actor document cache is keyed by the actor IRI.

## Alternatives considered

### 1. Use the local `IKeyStore` for all remote signature verification

This was rejected because remote senders are not local actors, and their keys are not owned by this instance. A local-key-only lookup never resolves the sender correctly and would silently create a false sense of verification.

### 2. Fetch the remote actor document on every verification

This works but is unnecessarily expensive and defeats the design of the server-side caches. Reusing the document cache and then the key cache matches the read-through pattern used elsewhere in the project.

### 3. Make outbound signing opportunistic without a configured instance actor

This leaves the server unable to act as a trusted federation participant when it needs to fetch remote documents or sign background work. The current design treats that as a deliberate configuration requirement, not as an accidental edge case.

## Consequences

- Remote signature validation is based on the sender's actual actor document.
- The project keeps a clean distinction between local-key identity and remote-key verification.
- Server setup is explicit: without `InstanceActorId`, remote fetches cannot sign and the environment degrades safely.
- The key resolution pipeline can be cached and reused across multiple signatures without re-fetching the same actor document repeatedly.

## Code alignment

The current implementation reflects this design:

- `RemoteInboundKeyResolver.ResolveAsync()` reads the key from `RemoteKeyCache`, and a miss triggers a fetch of the actor document followed by extraction of `publicKey`.
- `FetchJwkAsync()` accepts either a JWK object or a `publicKeyPem` string and normalizes it to JWK so the cache remains uniform.
- `IrisActorDocumentFetcher` reads the remote actor document through `RemoteActorCache` before serving it to callers.
- `ActivityPubServerExtensions.AddActivityPubServer()` registers `NoopActorDocumentFetcher` when `InstanceActorId` is not configured, which is the safe degradation mode.
- The server signs outbound fetches as `ActivityPubServerOptions.InstanceActorId` when it is present.

This is the correct federation boundary: a remote key is verified by remote identity, while local automated actions are signed by an explicit local actor identity.
