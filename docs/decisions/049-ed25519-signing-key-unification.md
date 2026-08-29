# 049 — Ed25519 support via a shared signing-key abstraction

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The project needed to support Ed25519 verification and signing in the same verification pipeline used for RSA and EC keys. The runtime had no BCL `Ed25519` implementation, and the project also needed a consistent API surface for both identity and verification without multiplying key-specific code paths across the codebase.

The key risk was a fragmented crypto stack: every signing or verification call site would need its own `switch` on key type, and the system would drift into incompatible code paths for the same operation.

## Decision

Iris uses a common `ISigningKey` abstraction for all signing-capable keys and implements it for both `KeyPair` and the new `Ed25519Key` type.

The crypto stack therefore uses a single interface for:

- algorithm metadata
- signing and verification
- JWK/public-key export
- PEM round-tripping
- thumbprint generation

This lets the pipeline treat RSA, EC, and Ed25519 keys uniformly while preserving the concrete implementation needed for each algorithm.

## Alternatives considered

### 1. Add a separate `Ed25519KeyPair` path and special-case every call site

This would duplicate verification logic and create a split mental model for the same system behavior.

### 2. Keep the old `KeyPair` model and extend it to include Ed25519-specific branches

This would couple the generic identity layer to a concrete algorithm implementation and would further entrench the key-type switch logic.

### 3. Use a runtime-specific BCL-only path where available and accept Ed25519 gaps elsewhere

This would leave the system incompatible with real-world peers that sign with Ed25519 and is not acceptable for federation.

## Consequences

- The message-signing pipeline is algorithm-agnostic at the call sites.
- Ed25519 keys can be loaded from JWK and PEM and verified the same way as RSA or EC keys.
- The implementation remains extensible for future key algorithms without new pipeline-wide rewrites.
- One NuGet dependency (BouncyCastle) is introduced specifically to cover the missing runtime capability.

## Code alignment

The design is reflected in the implementation:

- `ISigningKey` is the shared contract
- `KeyPair` and `Ed25519Key` both implement it
- `KeyPem.Load(...)` dispatches by algorithm
- `RemoteInboundKeyResolver` and the HTTP signature verifier consume the abstraction rather than the concrete type

This was the necessary unification step to close the Ed25519 interoperability gap without splitting the federation pipeline into parallel code paths.
