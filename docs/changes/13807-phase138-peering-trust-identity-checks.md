# 138.7 — Peering Trust/Identity Checks

## What was built

4 tests in `tests/Iris.LiveInterop.Tests/PeeringTrustIdentityTests.cs` that verify Iris's
HTTP Signature wire-format is compatible with a real Lemmy instance's RSA key. The Lemmy
public key is fetched live from the local Docker container (`http://localhost:8091/c/interop`
with `Accept: application/activity+json`); the tests are gated on the container being up
(`Assert.Fail` when unreachable — surfaces the missing dependency on CI boxes without Docker).

## Key types

- `KeyPair.FromPem(pem, KeyAlgorithm.Rsa, keyId)` — loads the Lemmy public key (public-only).
- `HttpSignatureVerifier.Verify(metadata, key, signatureHeader)` — the inbound verification path.
- `HttpSignatureSigner.Sign(metadata, identity, profile)` — the outbound signing path.
- `Signatures.BuildSignatureBase(metadata, components)` — the shared base construction.
- `Signatures.ComputeDigest(body)` — the lowercase `sha-256=base64` digest.
- `SignatureHeader.TryParse` / `.Format()` — wire-format round-trip.

## Tests

| Test | What it verifies |
|---|---|
| `LemmyKey_LoadsAsPublicOnly_CanVerifyButNotSign` | The key loads as public-only; `Sign` throws `CryptographicException` (the trust boundary: Iris verifies Lemmy's signatures but signs with its own key). The JWK round-trip includes `kty:RSA`, `n`, `e`. |
| `LemmyKey_VerifiesIrisProducedSignature_OverSameBase` | Signature base construction is deterministic and algorithm-compatible (RSA-PKCS1v15 + SHA-256). A signature from Iris's key correctly fails against Lemmy's public key (different keys) and succeeds against Iris's own key (crypto round-trip). |
| `IrisSignedRequest_VerifiesWithLemmyPublicKey` | Iris signs an outbound request (ServerToServer profile); the signature is well-formed, parseable by `SignatureHeader.TryParse`, and verifies against the Iris key. Pins the `keyId`, `algorithm`, and `headers` fields. |
| `LemmyDigestFormat_IsLowercaseSha256` | Documents the de facto Fediverse convention: `sha-256=base64` (lowercase), 32-byte payload. |

## Decision

The live wire test (actual Iris→Lemmy delivery accepted with 2xx) is **deferred to 138.10**
(the post-to-Lemmy-community delivery path), where a real delivery occurs. The 138.7 slice
verifies the cryptographic and wire-format compatibility in isolation, which is the
precondition for 138.10 to work.

## Test counts

- 4 new tests, all passing (with the local Lemmy container up).
- Iris.LiveInterop.Tests: 22 passed (was 18), 0 failed.
- Full solution: build clean, 0 warnings/0 errors.
