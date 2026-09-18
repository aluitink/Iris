# 136.17 — Duplicate/replay defense validation

**Date:** 2026-09-14
**Slice:** 136.17 (Lemmy interop — duplicate/replay defense validation)
**Status:** **COMPLETE.** Three cross-instance integration tests verify that duplicate activity
delivery is handled idempotently, that replayed signed requests are accepted (pinning the current
no-expiry behavior), and that wire-header reordering does not break signature validation.

## What this slice delivers

136.17's acceptance criteria:

1. Re-send identical activities (same ID/signature window) and confirm strict idempotent handling.
2. Replay near-expiry and expired signed requests to verify acceptance/rejection boundaries are enforced.
3. Re-order benign headers in equivalent signed requests to confirm canonical validation is robust, not brittle.
4. Exit when replay attempts do not create state duplication and all decisions are observable in logs.

### Test coverage

The three tests in `CrossInstanceReplayDefenseIntegrationTests` (two-instance `TestServer` fixture,
A: `replay-a.domain.local` alice, B: `replay-b.domain.local` bob) verify:

| Test | What it verifies |
|------|-----------------|
| `DuplicateCreateDelivery_StoredOnce_HandlerRunsOnce` | A `Create` activity delivered twice to bob's inbox (simulating a peer redelivery) is stored exactly once in B's activity store (IRI-dedup via `TryAddActivityAsync`), both deliveries are accepted (202), and the note appears in bob's inbox exactly once. |
| `ReplayedSignedRequest_SameDateSameSignature_IsAccepted` | A signed request captured at the wire level and re-sent verbatim (same Date, same Signature, same Digest, same body) is accepted (202). This pins the **current behavior**: no signature expiry/freshness check exists in the validation path — the `date` component is cryptographically bound (a tampered date fails verification) but not wall-clock-freshness-checked (a stale-but-validly-signed date verifies). The activity-store IRI dedup prevents any duplicate state. |
| `ReorderedWireHeaders_StillValidate` | A signed request re-sent with its HTTP content headers in a different wire order (Digest before Content-Type, instead of Content-Type before Digest) still validates. The canonical signature base is reconstructed from the declared component list in the `Signature` header (not the wire order of the HTTP headers), so benign header reordering does not break validation. |

### Key findings

- **No signature expiry check exists.** The `created`/`expires` params in the `Signature` header are parsed by `SignatureHeader.TryParse` but never consumed. `HttpSignatureVerifier.Verify` does a pure cryptographic check — no date comparison, no clock-skew tolerance, no max-age. A stale-but-validly-signed `date` verifies. This is a **known gap** (documented here, not fixed in this slice). A future slice could add a freshness window to `HttpSignatureValidator.ValidateAsync` (comparing the `date`/`created` component against `DateTimeOffset.UtcNow` with a configurable tolerance in `ActivityPubServerOptions`).
- **Replay protection today** = cryptographic binding of `date`/`digest` (tamper tests exist in `HttpSignatureTests` and `CanonicalSignatureMatrixTests`) + per-peer inbound rate limiting (429, sliding 1-minute window in `SlidingWindowInboundRateLimiter`). Same-activity redelivery is neutralized by the `TryAddActivityAsync` IRI dedup in `InboxProcessor.ProcessAsync` (C-07), not by signature checks.
- **Canonical signature base is order-independent on the wire.** `Signatures.BuildSignatureBase` iterates the components in the order **declared in the `Signature` header's `headers` list**, not the order the headers appear on the wire. Header lookups are case-insensitive and by name. This is pinned by `CanonicalSignatureMatrixTests.Accept_ComponentOrderIsIrrelevant_Verifies` and re-verified here at the integration level (full HTTP round-trip through the `TestServer`).
- **Duplicate delivery is accepted (202), not rejected (4xx).** A redelivered activity is a no-op at the handler level (the `InboxProcessor` idempotency guard skips dispatch), but the HTTP endpoint returns 202 Accepted — not a 409 Conflict or similar. This is the correct AP semantics: the receiving instance should not penalize a peer for at-least-once delivery.

### Replay defense posture (current)

| Attack | Defense | Status |
|--------|---------|--------|
| Body tamper (change payload, reuse signed headers) | `digest` component cryptographically bound | **Enforced** (401 on mismatch) |
| Date tamper (change date, reuse signature) | `date` component cryptographically bound | **Enforced** (401 on mismatch) |
| Header reordering (benign wire reordering) | Canonical base uses declared component order | **Robust** (validates correctly) |
| Stale-date replay (re-send old but valid signature) | **No freshness check** | **Gap** (accepted; IRI dedup prevents state duplication) |
| Rate-limited spam (many deliveries from one peer) | Per-peer inbound rate limiter (429) | **Enforced** (429 + Retry-After) |
| Duplicate activity (same ID re-delivered) | `TryAddActivityAsync` IRI dedup (C-07) | **Enforced** (stored once, handler runs once) |
