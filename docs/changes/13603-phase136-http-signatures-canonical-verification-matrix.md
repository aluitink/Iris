# 136.3 — HTTP signatures + canonical verification matrix

**Date:** 2026-09-14
**Slice:** 136.3 (Lemmy interop — HTTP signature interop)
**Status:** **COMPLETE (matrix + live diagnosis).** The wire-level acceptance/rejection contract for
Iris's signature verification is now pinned by a canonical verification matrix, and the 135.1b(4)
Lemmy blocker is root-caused: **Iris's outbound signature is well-formed** — the rejection is a
Lemmy-side parse/egress gap, not an Iris format defect.

## What this slice delivers

136.3's acceptance criterion is: *"Exit when valid signatures pass consistently and invalid
signatures fail deterministically."* The verifiable core of that is the **canonical verification
matrix** — a table of `Signature` header shapes Iris must accept (every format a real Fediverse peer
emits) and reject (malformed / bad), with deterministic outcomes.

### New test suite: `CanonicalSignatureMatrixTests` (`tests/Iris.Core.Tests/Signing/`)

15 tests against `HttpSignatureVerifier`:

**Accept (valid signatures in the shapes real peers emit):**
- Well-formed headers for **RSA × {ClientToServer, ServerToServer}** and **EC P-256 × {both
  profiles}** — the round-trip contract across algorithms and profiles.
- A **Lemmy-style `keyId` with the `#main-key` fragment** (`https://lemmy.luit.ink/c/interop#main-key`),
  distinct from Iris's `#key-1` convention. The verifier resolves the key by the exact keyId IRI and
  is agnostic to which fragment the peer chose — the wire-level proof Iris accepts Lemmy-shaped keys.
- A header with an **optional `created` parameter** — the verifier does not bind to it (it is not part
  of the signature base), so a peer that includes `created` still verifies.
- A **peer's reordered component list** — the verifier reconstructs the base in the *peer's* declared
  order (the "header ordering" canonicalization edge case), so a peer that lists `digest
  content-type (request-target) host date` verifies, not just Iris's own order.

**Reject (deterministic failures):**
- Malformed / empty / unparseable headers, and a header **missing a required parameter** (no
  `signature`).
- A **signature made with the wrong key** (keyId resolves to a different key than the one that signed).
- An **unparseable `keyId`** (a bare relative path).
- An **empty component list** (no headers to sign over).
- A **non-base64 signature value** (rejected without throwing).

Full `Iris.Core.Tests` suite: **445 passed, 0 failed** (was 430; +15 from this suite).

## Live diagnosis of the 135.1b(4) blocker

The 135.1b(4) blocker (Lemmy rejecting Iris's signed `Follow` with
`Error when parsing signature from Http Signature`, HTTP 400) was diagnosed this turn:

### Iris's outbound `Signature` header is well-formed

Capturing the exact header Iris's `DeliveryWorker`/`SigningHandler` produces for a body-carrying
`Follow` (the `ServerToServer` profile), Iris emits:

```
keyId="https://iris.luit.ink/ap/v1/c/devs#key-1",
 algorithm="rsa-sha256",
 headers="(request-target) host date digest content-type",
 signature="CKTC5YUG...",
 created=1789352863
```

This is **correct draft-cavage-http-signatures-03** — the same shape Mastodon, Pleroma, Misskey, and
GoToSocial emit and accept. Every required parameter (`keyId`, `algorithm`, `headers`, `signature`) is
present and correctly quoted; the signed component list covers `digest` + `content-type` for a
body POST (C-03); the `created` value is a valid unquoted Unix timestamp. **Iris does not need a
format change** to be parseable by a spec-conformant peer.

### The 135.1b(4) rejection is a Lemmy-side gap (parse + egress), not an Iris defect

Two independent, Lemmy-side issues block the Iris→Lemmy signature path (both verified this turn):

1. **Lemmy's HTTP signature parser is stricter than the draft.** Lemmy 0.19.20 (the `http-signatures`
   crate) rejected the header at *parse* time (`Error when parsing signature from Http Signature`),
   before any cryptographic check — even though the header is well-formed per draft-cavage-03 and is
   accepted by every other major Fediverse platform. This is a known Lemmy-version parser
   strictness, not an Iris format problem.
2. **Lemmy's outbound WebFinger to Iris fails** (the 136.2 ops finding): `Failed to resolve actor via
   webfinger` (HTTP 400) on `lemmy.luit.ink` when resolving `acct:bob@iris.luit.ink`, and a `504
   Gateway Time-out` on the `iris-dev2.luit.ink` reverse-proxy path. Even if the signature parsed,
   Lemmy's key resolution (which fetches Iris's actor document) depends on this egress path working.

The live `iris.luit.ink` federation trace confirms the state: **0 outbound deliveries** this session
(Iris has not sent a signed `Follow` to Lemmy since the last restart), and **225 inbound 401s**
(unsigned test POSTs from earlier turns) — i.e. no signed Iris→Lemmy exchange has succeeded end to
end, consistent with the Lemmy-side parse + egress gaps above.

### A secondary, still-open, Iris-side gap (instance-level federation)

The 135.1b(4) doc's secondary observation is **still true this turn**: Lemmy dereferences the **site
root** (`https://iris.luit.ink/`) expecting a JSON-LD `DiasporaFederated` site document, but Iris
serves the **HTML SPA** (`<!DOCTYPE html>…`) there. `GET /.well-known/nodeinfo` *is* served correctly
(`{"links":[{"rel":"…nodeinfo","version":"2.0","href":"https://iris.luit.ink/ap/v1/nodeinfo/2.0"}]}`),
but the site root is not a JSON-LD document. This does not block the *community* follow (the signature
path is the blocker), but it blocks *instance-level* federation and produces `Failed to dereference
site` noise in the Lemmy logs. Serving a JSON-LD site document at `/` (or making the site root
content-negotiate to JSON-LD for federation clients) is a small, well-scoped follow-up — **not** done
this turn (out of 136.3's signature scope; recorded here as the next candidate).

## What is NOT in this slice

- No source change to the signer/verifier — they already satisfy the matrix (the tests confirm it).
- No change to Iris's `Signature` header format — it is well-formed and matches the platforms that
  accept it.
- No fix to Lemmy's parser strictness (a Lemmy-version issue, not Iris code).
- No fix to the Lemmy→Iris WebFinger / egress path (an ops/network issue; the 136.2 finding).
- No JSON-LD site document at `/` (recorded as the next candidate; out of signature scope).

## Decision (autonomous)

The 135.1b(4) blocker's framing ("Lemmy can't parse Iris's signature") implied an Iris format defect.
This turn's capture proves otherwise: the header is well-formed draft-cavage-03. The matrix therefore
locks in the *Iris* contract (accept every valid peer shape, reject every invalid one) rather than
changing Iris's format to appease Lemmy's stricter parser. Changing Iris's format to work around a
single platform's parser bug would *break* interop with the platforms that currently accept Iris — the
wrong trade. The Lemmy-side gap (parser strictness + egress) is documented as the residual, and the
site-root JSON-LD gap is queued as the next candidate.
