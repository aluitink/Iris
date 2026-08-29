# 044 — Default RSA key generation and PEM public-key format

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The project needed a default, interoperable signing key layout that worked for common ActivityPub servers and clients while still remaining compatible with the server's own PEM-based key loading model.

The initial design was constrained by real-world compatibility: Mastodon and similar servers commonly use RSA-2048 with classic PEM-encoded public keys, while the project also needed an explicit method to distinguish RSA from EC when the private key is loaded from PEM.

## Decision

The default generated actor key is RSA-2048.

The public key is served as PEM in the actor document using a PKIX public-key representation (`-----BEGIN PUBLIC KEY-----`). The server also accepts PKCS#1 RSA public-key PEM in the import path for compatibility with real-world interop.

The key algorithm is still explicitly carried as a separate extension field (`keyAlgorithm`), so the loader can distinguish RSA from EC when the private-key payload is encoded as the same PKCS#8 PEM form.

## Alternatives considered

### 1. Default to EC P-256 only

This is valid, but it does not match the broader real-world server baseline. RSA remains the most common default and gives the best interoperability story for the current phase.

### 2. Serve JWK only for the public key

This is standard in some contexts, but the project needs a PEM-based interoperability path that matches integration targets already consuming PEM and avoids extra conversion layers.

### 3. Infer the key algorithm from the PEM header alone

This is not reliable because both RSA and EC private keys can be exported as PKCS#8 PEM, which are structurally similar and require explicit metadata.

## Consequences

- Default actor keys interoperate better with the broader federation ecosystem.
- PEM remains the primary format for both the private and public halves of the actor key material.
- The explicit `keyAlgorithm` field remains necessary for correct private-key decoding.
- Real-world PKCS#1 public-key imports continue to work without forcing a full JWK conversion step.

## Code alignment

The current implementation reflects the decision:

- RSA-2048 is the default generated key
- public keys are exposed as PEM on the actor document
- private-key loading retains explicit algorithm disambiguation
- PKCS#1 public-key import is supported for interoperability

This keeps the federation surface aligned with real implementations while preserving the project's own PEM-based loading model.
