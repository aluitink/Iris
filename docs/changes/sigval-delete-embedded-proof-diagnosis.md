# Delete Activity Signature Verification Diagnosis

## Summary

Investigated the Delete activity 401 rejections reported in the Signature Validation Review. Found that the rejections are **correct behavior** — the W3C `RsaSignature2017` embedded proof format does not embed the public key, so verification is impossible when the actor's key cannot be resolved (e.g., the actor is 410 Gone and the key is not in the cache).

## Findings

### Delete Activity Body Structure

Captured live Delete activity bodies from mastodon.social (via temporary diagnostic logging). The structure is:

```json
{
  "@context": ["https://www.w3.org/ns/activitystreams", "https://w3id.org/security/v1"],
  "id": "https://mastodon.social/ap/users/115514855148586326#delete",
  "type": "Delete",
  "actor": "https://mastodon.social/ap/users/115514855148586326",
  "to": ["https://www.w3.org/ns/activitystreams#Public"],
  "object": "https://mastodon.social/ap/users/115514855148586326",
  "signature": {
    "type": "RsaSignature2017",
    "creator": "https://mastodon.social/ap/users/115514855148586326#main-key",
    "created": "2026-09-19T03:59:49Z",
    "expires": "2026-09-21T03:59:49Z",
    "signatureValue": "EbWJBjaDxqlw9oUmlnb+..."
  }
}
```

Key observations:
- The `signature` field is a W3C `RsaSignature2017` proof.
- The `creator` is the key IRI (same as the `keyId` in the HTTP signature header).
- **No `publicKey` field** — the proof does not embed the public key.
- The `signatureValue` is `Base64(RSA-SHA256(SHA256(options) || SHA256(document)))`.

### Mastodon's Implementation

Examined Mastodon's `ActivityPub::LinkedDataSignature#verify_actor!`:

```ruby
keypair = Keypair.from_keyid(creator_uri)
keypair = ActivityPub::FetchRemoteKeyService.new.call(creator_uri) if keypair&.public_key.blank?
return if keypair.nil? || !keypair.usable? || keypair.type != 'rsa'
```

Mastodon resolves the key via:
1. `Keypair.from_keyid` — a **local database lookup** (`find_by(uri: uri)`).
2. `FetchRemoteKeyService` — a remote fetch of the actor document (fails for 410 Gone).

The same limitation applies: the key must be cached locally or resolvable remotely. When the actor is deleted and the key is not in the local DB, verification fails.

### Iris's Current Behavior

Iris's `TryVerifyEmbeddedProofAsync` resolves the key via `RemoteInboundKeyResolver.ResolveAsync`, which checks the `RemoteKeyCache` (1h fresh + 1h stale, in-memory) first, then attempts a fresh fetch of the actor document. When the actor is deleted (410 Gone), the fetch fails and the key is only available if it's still in the cache.

**Iris's current behavior (rejecting Delete activities when the key can't be resolved) is correct** and matches both the ActivityPub spec and Mastodon's implementation.

### Root Cause of the 401 Rejections

The 106 Delete rejections in the last 2 hours are all from actors that:
1. Were deleted (410 Gone).
2. Have no key in the `RemoteKeyCache` (the key expired from the 2h TTL, or was never cached because the actor was never previously interacted with).

This is a **known limitation of the ActivityPub federation model**, not an Iris bug.

## Potential Future Improvement

To enable verification of Delete activities from previously-interacted actors (even after deletion), Iris could **persist remote actor keypairs to the database** (similar to Mastodon's `Keypair` table). This would:
- Retain keys across cache evictions and restarts.
- Enable verification of embedded proofs for actors that were previously interacted with.
- Require a larger architectural change (new DB table, migration, keypair persistence on fetch, keypair lookup in the resolver).

This is logged as a future product decision, not a defect.

## Diagnostic Cleanup

All temporary diagnostic logging (TEMP-DEL) has been removed from `HttpSignatureValidator.cs`. The clean build passes with 0 warnings, 0 errors.

## Test Results

- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test`: 1346 passed, 0 failed, 25 skipped.
