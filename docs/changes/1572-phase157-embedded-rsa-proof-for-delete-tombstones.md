# Phase 157.2 — Embedded RsaSignature2017 Proof for Delete/Tombstone Deliveries

## Problem

Mastodon (and other fediverse servers) deliver **Delete** activities for actor tombstones
using a **dual-signature** scheme:

1. A standard **HTTP header signature** (draft-cavage-03 or RFC 9421), and
2. A **W3C Linked Data `RsaSignature2017` proof** embedded in the activity JSON body
   under the `signature` field.

When the actor is deleted, their HTTP endpoint returns **410 Gone**. The HTTP header
signature can no longer be verified because the signing key is no longer resolvable
(the actor document is gone). The embedded proof does **not** include the public key —
it only has the `creator` (key IRI) — so it is only verifiable if the key is already
cached or stored locally.

Two issues surfaced in production:

1. **Key resolution storm**: every Delete from a 410 actor triggered an outbound
   signed GET to fetch the actor document (which returned 410). Because the key cache
   does not cache negative results, the same 410 fetch was repeated on every retry,
   consuming CPU and network.
2. **Unverifiable proofs**: for actors we've never interacted with (no cached key,
   no local store record), the embedded proof cannot be verified.

## Solution

### `EmbeddedSignatureVerifier` (new — `src/Iris.Core/Signing/EmbeddedSignatureVerifier.cs`)

A static verifier for W3C Linked Data `RsaSignature2017` proofs embedded in an
ActivityStreams activity body:

- **`HasEmbeddedProof(byte[] body)`** — checks for a `signature` field with
  `type: "RsaSignature2017"`.
- **`ExtractCreator(byte[] body)`** — extracts the `creator` (key IRI) from the proof.
- **`Verify(byte[] body, ISigningKey key)`** — computes the signature base
  `SHA256(options) || SHA256(document)` where:
  - `options` = the `signature` object minus `signatureValue`, serialized as JSON
  - `document` = the activity JSON minus the `signature` field, serialized as JSON

  and verifies via `ISigningKey.Verify` (RSA-SHA256 / PKCS#1 v1.5).

  Also checks the optional `expires` field (rejects expired proofs).

### `HttpSignatureValidator` (modified — `src/Iris.Server/Security/HttpSignatureValidator.cs`)

Three new behaviors:

1. **Delete short-circuit** (before key resolution): for `Delete` activities, the
   validator checks the local actor store **first**. If the actor is not in the store,
   the Delete is dropped immediately (`isValid: false`) — no key resolution, no
   outbound fetch. This eliminates the 410-fetch storm. If the actor **is** in the
   store, the stored actor's `publicKey` is extracted and used to verify the embedded
   proof; if the proof verifies, the result is `isValid: true` (the inbox handler then
   removes the actor from the store).

2. **Embedded proof fallback** (on key resolution failure, non-Delete activities):
   when the HTTP header key cannot be resolved, the validator checks for an embedded
   `RsaSignature2017` proof in the body, resolves the key from the proof's `creator`,
   and verifies it.

3. **Embedded proof fallback** (on cryptographic failure, non-Delete activities):
   when the HTTP header signature fails verification, the same embedded-proof check is
   attempted.

`IActorStore` is wired in via `IPersistenceProvider.Actors` (DI).

## Tests

`tests/Iris.Core.Tests/Signing/EmbeddedSignatureTests.cs` (new — 8 tests):

- `Verify_ValidEmbeddedProof_ReturnsTrue` — happy path.
- `Verify_TamperedBody_ReturnsFalse` — body tampering detection.
- `Verify_WrongKey_ReturnsFalse` — wrong key rejection.
- `Verify_NoSignatureField_ReturnsFalse` — no proof present.
- `Verify_WrongType_ReturnsFalse` — non-RsaSignature2017 type rejected.
- `Verify_ExpiredProof_ReturnsFalse` — expired proof rejected.
- `Verify_MalformedJson_ReturnsFalse` — malformed JSON rejected.
- `ExtractCreator_NoProof_ReturnsNull` — no creator when no proof.

All 8 tests pass (no .NET 10 VSTest testhost hang — the embedded-proof path does not
invoke `SignatureInputHeader.TryParse`).

## Files changed

| File | Action |
|------|--------|
| `src/Iris.Core/Signing/EmbeddedSignatureVerifier.cs` | new |
| `src/Iris.Server/Security/HttpSignatureValidator.cs` | modified |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | modified (DI wiring) |
| `tests/Iris.Core.Tests/Signing/EmbeddedSignatureTests.cs` | new |

## Verification

- Build: 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `Iris.Core.Tests`: 465 passed, 7 skipped (pre-existing testhost-hang skips), 0 failed.
- Deployed to `irisweb-iris-web-1`; service healthy.
- Live verification: Delete deliveries from 410 actors are now **dropped** (actor not
  in local store) with **zero** outbound key-resolution fetches. CPU and memory are
  stable under Delete load (previously CPU climbed with each Delete due to repeated
  410 fetches).
- Log line for the drop: `Delete dropped: actor {Iri} not in local store on {Method} {Path}`.
- Log line for a verified Delete: `Delete verified against store for actor {Iri} on {Method} {Path}`.
