# 82.1 — Outbound signature conformance (offline, deterministic) (Phase 82, slice 1)

**Slice:** 82.1 (outbound signature conformance — verify Iris's `ISignatureSigner` produces spec-correct `Signature` headers; fix any conformance gap).
**Status:** DONE — **1 production conformance fix** (added the missing `created` parameter) + **13 new deterministic offline conformance tests** (`Iris.Core.Tests`).
**Companion:** 82.2 (real-world signed delivery to a non-permissive instance) + 82.3 (key-management hardening) are the remaining Phase 82 slices (in Up Next).

## What was done

Surveyed the entire outbound signing subsystem (`ISignatureSigner`, `HttpSignatureSigner`, `SigningProfile`, `HttpRequestMetadata`, `SignatureHeader`, `Signatures`, `SigningHandler`), audited the existing test coverage, found one real conformance gap, fixed it, and added deterministic offline tests that pin the exact `Signature` header a fixed key produces for both profiles.

### Conformance gap found + fixed

**The `Signature` header omitted the `created` parameter.** Iris's outbound `Signature` header carried only `keyId`, `algorithm`, `headers`, `signature` — but draft-cavage-http-signatures-03 (the profile ActivityPub uses) and the major implementations (Mastodon in particular) emit `created` (a Unix timestamp in seconds). Some receiving servers treat a missing `created` as a replay-window / freshness problem. This was the one spec-required parameter that was absent.

**Fix (3 files, all in `Iris.Core`):**
1. **`SignatureHeader`** gained a `long Created = 0` field (default 0 = absent, so existing 4-arg call sites and legacy parsing stay valid).
   - `Format()` now emits `", created=N"` (an **unquoted** integer, per the spec) **after** the `signature` param, and only when `Created > 0`.
   - `TryParse` now parses `created` (and tolerates `expires`/`expiresIn`) as an **optional, unquoted integer** — it is read when present but is **not required**, so a peer that omits it (or an Iris pre-82.1 header) still parses. `created`/`expires` are handled before the `Unquote` step (which would reject unquoted values).
2. **`HttpSignatureSigner`** (`Sign` + `SignAsync`) now passes `Signatures.ToUnixSeconds(metadata.Date)` as `Created`.
3. **`Signatures.ToUnixSeconds(string?)`** — new helper: converts an RFC 1123 HTTP-date to epoch seconds (defensive: returns 0 when unparseable, never throws).

**Key design decision (recorded below):** `created` is derived from the **signed `date`** (not the wall clock at signing time). This makes the header **deterministic** for a given request (a fixed date → a fixed `created`), keeps `created` consistent with the `date` component that is actually covered by the signature, and removes any `DateTime.UtcNow` dependency (which would make the header non-reproducible and defeat deterministic tests).

### Verified correct (no code change)

- **`keyId`** is the actor's `publicKey` IRI (the key's `#fragment`, e.g. `…/u/alice#main-key`) — correct, not the bare actor IRI.
- **`algorithm`** label per key type: `rsa-sha256` / `ecdsa-p256-sha256` / `ed25519` — correct.
- **`headers` list per profile:** `ClientToServer` = `(request-target) host date`; `ServerToServer` = `(request-target) host date digest content-type`. `(request-target)` is **first** and the method is **lowercased** — matches the de facto Fediverse convention.
- **`signature`** is base64 of the key's signature over the signature base — correct, and verifiable by a peer holding only the public key.
- **`digest`** (ServerToServer) = `sha-256=base64(SHA-256(body))`, embedded in the base and signed — correct.
- **Signature base** construction (`name: value` lines joined by `\n`, no trailing newline) — correct (locked byte-for-byte by the new test).
- **`SigningHandler`** (the `Iris.Client` boundary) sets `Date` + `X-Signature-Date` + `Signature`, uses `ServerToServer` for body requests and `ClientToServer` for bodyless requests, and computes the `digest` — all correct (its existing `SigningHandlerTests` still pass unchanged).

### Tests added

**`Iris.Core.Tests/Signing/OutboundSignatureConformanceTests.cs`** (new file, 13 facts) — deterministic, offline conformance pinning using a **fixed RSA-2048 key** (baked-in PKCS#8 PEM) + a fixed valid date (`Wed, 26 Aug 2026 12:00:00 GMT` → epoch `1787745600`). Because RSA PKCS#1 v1.5 is deterministic, the signature bytes are reproducible and pinned as constants:

| Test | What it locks in |
|---|---|
| `Sign_ClientToServer_ProducesSpecConformingHeader` | GET (bodyless): `keyId` = key IRI, `algorithm` = `rsa-sha256`, `headers` = `(request-target) host date`, `created` = `1787745600`, and the **exact pinned signature bytes**. |
| `Sign_ServerToServer_ProducesSpecConformingHeaderWithDigest` | POST (Follow body): `headers` = `(request-target) host date digest content-type`, `created` = `1787745600`, and the **exact pinned signature bytes**. |
| `Sign_ClientToServer_SignatureVerifiesAgainstPublicKey` | A peer holding the key resolves it by `keyId` and `Verify` returns true — the signature is cryptographically valid. |
| `Sign_ServerToServer_SignatureVerifiesAgainstPublicKey` | Same, for the body-carrying ServerToServer case. |
| `Sign_SignatureBase_MatchesPinnedSpecExample` | Pins the **exact signed-string bytes** for both profiles (component order + lowercased method + `name: value` lines + `\n` join, no trailing newline) — catches any reordering/reformatting regression. |
| `Sign_Created_DerivedFromSignedDate_IsDeterministic` | Two signatures over the same fixed date carry the **same `created`** (not the wall clock) and are byte-identical. |
| `ToUnixSeconds_ValidHttpDate_ReturnsEpochSeconds` | The date→epoch conversion (`GMT` and `+0000` forms → the same instant). |
| `ToUnixSeconds_UnparseableDate_FallsBackToZero` | Defensive: null/empty/garbage → 0 (never throws). |
| `TryParse_LegacyHeaderWithoutCreated_StillParses` | Backward compatibility: a header without `created` still parses, `Created` defaults to 0. |
| `TryParse_HeaderWithCreated_ParsesCreated` | A header with `created` parses the integer value. |

Plus 3 existing `SignatureHeaderTests`/`HttpSignatureTests` call sites updated for the new `Created` param (the 4-arg constructors now pass `Created` through / default to 0).

## Decision (recorded per the autonomous-loop open-questions policy)

**Add `created` (vs. document-only):** The slice asked to verify the header against the spec, including `created`/`expires`. The honest finding was that `created` was **missing** entirely. Rather than only documenting the gap, I fixed it — it is a genuine, low-risk, high-value conformance improvement (Mastodon and other AP servers emit/expect it; its absence is a realistic rejection reason at non-permissive instances). The fix is small and contained to `Iris.Core` and is backward compatible (legacy headers without `created` still parse; `created` is optional).

**`created` derived from the signed `date`, not the wall clock:** Making `created` a function of the metadata's `Date` (rather than `DateTime.UtcNow`) keeps the header **deterministic** for a given request — which is exactly what the slice asked for ("offline, deterministic") and what enables byte-pinned signature assertions. It also keeps `created` consistent with the `date` component the signature actually covers (a peer checking freshness sees `created` and `date` agree).

**`expires` scoped out (follow-up, not a gap):** I deliberately did **not** add `expires`/`expiresIn`. `created` is the universally-emitted freshness marker; `expires` requires a policy decision (how long a signature is valid — a TTL Iris would have to pick and document). Adding a guessed TTL would be a larger design decision than this slice warrants. It is a candidate follow-up in 82.3 (key/signature management hardening) if real-world delivery (82.2) reveals a server that rejects on a missing `expires`.

## Verification

- **Build:** `dotnet build -c Release` → **0 warnings, 0 errors** (`TreatWarningsAsErrors` on).
- **Tests:** `dotnet test -c Release --filter "Category!=Slow"` → **1581 passed, 0 failed, 1 skipped** (up from 1568 — the 13 new `Iris.Core.Tests`). Existing signing tests all pass unchanged: `Iris.Core.Tests` signing 48, `Iris.Client.Tests` signing 7, `Iris.Server.Tests` outbound-conformance 5. `Iris.Server.Tests` 975/1-skipped in isolation (the occasional parallel-run failure is the known timing/contention flake, unrelated to this change).

## Files changed

- `src/Iris.Core/Signing/SignatureHeader.cs` — `Created` field (default 0); `Format()` emits `created` when set; `TryParse` parses `created` (optional, unquoted int) and tolerates `expires`/`expiresIn`.
- `src/Iris.Core/Signing/HttpSignatureSigner.cs` — `Sign` + `SignAsync` pass `Signatures.ToUnixSeconds(metadata.Date)` as `Created`.
- `src/Iris.Core/Signing/Signatures.cs` — new `ToUnixSeconds(string?)` helper (RFC 1123 → epoch seconds, defensive 0 fallback).
- `tests/Iris.Core.Tests/Signing/OutboundSignatureConformanceTests.cs` — new (13 tests, fixed-key deterministic pinning).
- `tests/Iris.Core.Tests/Signing/SignatureHeaderTests.cs` + `HttpSignatureTests.cs` — existing `SignatureHeader` constructor call sites updated for the new `Created` param (behavior preserved).
- No new NuGet packages; no csproj change.

## Test-debt log

- **Web tests:** none touched (core-side signature conformance is in-scope for new coded tests per the WASM manual-test policy).
- **Core/Client/Server tests:** 13 new + 2 call-site updates, all passing; no existing tests modified or deleted beyond the `Created`-param call sites (which preserve prior behavior).
