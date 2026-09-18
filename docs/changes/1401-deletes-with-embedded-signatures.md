# 140.1 — Deletes with embedded signatures

**Status:** FIXED. RFC 9421 Deletes with a body-embedded W3C `RsaSignature2017` proof now verify via the
same embedded-proof fallback the legacy path already had. +4 tests.

## Bar

> A Delete delivered with an embedded signature (the actor's own key, in the body) must be accepted
> even when the actor's HTTP endpoint is 410 Gone and the header signature can no longer be
> resolved/verified against a live key.

## Background

When an actor is deleted, fediverse servers send a `Delete` activity whose `object` is the actor's
own IRI. Two things happen at the same time:

1. The actor's HTTP endpoint goes **410 Gone**, so the **header** signing key (resolved by fetching the
   actor document) can no longer be resolved.
2. To survive that, the sender embeds a **W3C Linked Data `RsaSignature2017` proof** in the activity
   body (the `signature` field), signed by the actor's own key. This proof is self-contained and
   verifies without reaching the (now-Gone) actor document.

Mastodon 4.5+ signs these Deletes with an **RFC 9421** HTTP header signature **and** the embedded body
proof. The header signature is what the receiver normally validates; the embedded proof is the
fallback for when the actor is gone.

## The bug

`HttpSignatureValidator` had **two** validation paths:

- The **legacy** (draft-cavage-03) path (`ValidateAsync`) already had the embedded-proof fallback
  (`TryVerifyEmbeddedProofAsync`), called at both rejection points: an unresolvable key and a
  failed cryptographic verification.
- The **RFC 9421** path (`ValidateRfc9421Async`) — the one modern Mastodon uses — did **not**. It
  rejected the Delete when the header key was unresolvable (actor 410 Gone) or the header signature
  failed verification, **without** checking the embedded proof in the body.

So a legitimate Delete from a deleted actor, signed in the modern RFC 9421 form, was silently
rejected and the local tombstone was never applied.

## The fix

`src/Iris.Server/Security/HttpSignatureValidator.cs` — `ValidateRfc9421Async` now calls
`TryVerifyEmbeddedProofAsync` at the same two rejection points the legacy path uses:

1. **Unresolvable header key** (the `key is null` branch) — before logging/rejecting, check the
   embedded proof.
2. **Failed header verification** (the `!isValid` branch, after the F-21 key-rotation re-resolve) —
   before logging/rejecting, check the embedded proof.

The fallback verifies the embedded proof against the **creator** key (resolved from the proof's
`creator` IRI) and requires the creator's owning actor to be present in the persistence store (the
same guard the legacy path uses). On a successful embedded-proof verification it returns
`IsValid: true`; otherwise it falls through to the original rejection. The embedded-proof code path
(`EmbeddedSignatureVerifier`) is unchanged — it was already correct and unit-tested in
`Iris.Core.Tests`.

## Verification

### New tests (`tests/Iris.Server.Tests/Rfc9421EmbeddedSignatureDeleteTests.cs`)

A focused validator-level test (no network) that crafts an RFC 9421 `Signature` + `Signature-Input`
request whose header key is unresolvable (or resolvable-but-fails) and whose body carries an embedded
`RsaSignature2017` proof, then asserts the validation result:

| Test | Header key | Embedded proof | Expected |
|------|-----------|----------------|----------|
| `Rfc9421_UnresolvableHeaderKey_ValidEmbeddedProof_IsAccepted` | unresolvable | valid | **accepted** (the 140.1 fix) |
| `Rfc9421_UnresolvableHeaderKey_NoEmbeddedProof_IsRejected` | unresolvable | absent | rejected |
| `Rfc9421_UnresolvableHeaderKey_TamperedEmbeddedProof_IsRejected` | unresolvable | tampered | rejected (fallback must not over-accept) |
| `Rfc9421_HeaderSignatureFails_ValidEmbeddedProof_IsAccepted` | resolvable, bogus sig | valid | **accepted** (second call site) |

The two positive tests were confirmed to **fail** with the fix reverted (via `git stash`) and pass
with it — proving they exercise the change, not just the pre-existing legacy behavior.

### Build / tests

- `dotnet build` — 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test tests/Iris.Server.Tests --filter "Category!=Slow"` — green: **1323** passing
  (1319 baseline + 4 new).
- `dotnet test tests/Iris.Core.Tests` — green: 467 passing (includes the unchanged
  `EmbeddedSignatureTests`).

## Files

- `src/Iris.Server/Security/HttpSignatureValidator.cs` — `ValidateRfc9421Async`: embedded-proof
  fallback at the unresolvable-key and verification-failed rejection points (mirrors the legacy path).
- `tests/Iris.Server.Tests/Rfc9421EmbeddedSignatureDeleteTests.cs` — new; 4 tests above.
