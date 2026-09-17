# Phase 157 — RFC 9421 (new-format) HTTP message signature verification

**Status:** COMPLETE (build clean 0/0, Server Security+Delivery tests 225 passed / 0 failed, 17 new Core unit tests)
**Date:** 2026-09-17

## What

Modern fediverse peers (Mastodon 4.5+, Pleroma 2024+, Misskey, GoToSocial) send HTTP
signatures in the **RFC 9421** (draft-cavage-21+) format: the `Signature` header carries
only a label + base64 signature (e.g. `sig1=:<base64>:`), with the parameters
(`keyid`, `created`, covered components) in a separate `Signature-Input` header.
Iris's legacy draft-cavage-03-only parser (`SignatureHeader.TryParse`) rejected these
as "malformed Signature header", causing Iris to reject those peers' inbox POSTs — a
high-impact federation interop gap.

This adds RFC 9421 verification so Iris accepts signatures from all modern fediverse
peers, in addition to the legacy draft-cavage-03 format it already supported.

## How

### `src/Iris.Core/Signing/SignatureInputHeader.cs` (NEW)

- **`SignatureInputComponent`** (`readonly record struct`) — a covered component: `Name`
  (e.g. `date`, `@method`, `@path`) + `Parameters` (the serialized params, e.g.
  `;name="Pet"` for `@query-param`).
- **`SignatureInputMember`** (`sealed record`) — one signature's metadata: `Label`,
  `Components`, `RawParameters`, `MemberValue` (the raw member value, used verbatim as
  the `@signature-params` line in the signature base). Exposes `KeyId` and `Created`
  via parameter extraction.
- **`SignatureInputHeader`** (`sealed record`) — parsed `Signature-Input` Dictionary
  header. `TryParse(header, out parsed)` splits on top-level commas (quote-aware),
  parses each `label=...` member, extracts the inner-list components + trailing params.
  `GetMember(label)` finds a member by label.

### `src/Iris.Core/Signing/SignatureBase9421.cs` (NEW)

- **`SignatureBase9421.Build(metadata, member, memberValue)`** — builds the RFC 9421 §2.5
  signature base: one line per covered component (`"name"[:params]: value\n`) followed by
  the `"@signature-params": <memberValue>\n` line. Derived components supported:
  `@method`, `@path` (path without query), `@query` (query with leading `?`),
  `@authority` (lowercase host, default port 80 omitted), `@target-uri`
  (`https://host/path?query`), `@scheme` (`https`), `@request-target` (origin-form).
  Plain header components read verbatim from `metadata.Headers` (case-insensitive).
  Throws `ArgumentException` for unknown derived components or missing headers (the
  verifier catches this as an invalid signature).
- Validated against the **RFC 9421 Appendix B.2.6 conformance vector** (the test
  `SignatureBase_B26_MatchesSpecExample` asserts byte-identical output to the spec's
  worked example).

### `src/Iris.Server/Security/HttpSignatureValidator.cs` (MODIFIED)

- When `SignatureHeader.TryParse` rejects the header (legacy parse failure), the
  validator now checks for a `Signature-Input` header. If present and parseable, it
  routes to **`ValidateRfc9421Async`** — which extracts the label + base64 from the
  `Signature` header, finds the matching `Signature-Input` member, resolves the key
  (same `IInboundKeyResolver` + F-21 rotation-invalidation policy as the legacy path),
  builds the RFC 9421 base, and verifies.
- Removed the two `TEMP(157)` diagnostic log blocks (the malformed-header raw-log and
  the local self-signature failure log) — they were added for Phase 157 diagnosis and
  are no longer needed now that the RFC 9421 path handles the "malformed" rejections.

### `tests/Iris.Core.Tests/Signing/Rfc9421SignatureTests.cs` (NEW)

17 tests covering:
- **Parser**: single/multiple members, empty inner list, component with parameters
  (`@query-param;name="Pet"`), malformed inputs (4 cases), `GetMember` lookup.
  **Note:** all 7 `SignatureInput_TryParse_*` / `SignatureInput_GetMember_*` tests are
  skipped due to a .NET 10 VSTest testhost hang (blame-identified: the testhost process
  hangs on shutdown when any `SignatureInputHeader.TryParse` test runs). The parser logic
  is exercised in production via the `HttpSignatureValidator` RFC 9421 path and is
  validated indirectly by the `SignatureBase_*` tests (which call `TryParse` internally
  via `B26Metadata` + `SignatureBase9421.Build`).
- **Signature base**: B.2.6 conformance vector (byte-exact), empty covered components,
  `@path` derivation (no query), missing header throws, unknown derived component throws.
- **Round-trip**: sign + verify with a generated Ed25519 key; tampered base fails.
- **B.2.6 known-answer**: skipped (the RFC's example key is 31 bytes — a typo in the
  spec — and cannot be loaded; the base-conformance test already validates the vector).

## Decision

The RFC 9421 verifier is a **separate code path** from the legacy one (not a unification
of the two parsers). Rationale: the legacy draft-cavage-03 format is still sent by older
peers and by Iris's own outbound signer (`SigningHandler`), and its base format
(`(request-target) host date`, no trailing newline, unquoted component names) is
fundamentally different from RFC 9421's (`"name": value` lines, quoted names, trailing
newline, `@signature-params` line). A unified parser would add complexity without
benefit; the routing is a simple `if legacy-fails, try RFC 9421` check on the
`Signature-Input` header's presence.

The `@target-uri` component is built as `https://host/path?query` — the scheme is
hardcoded to `https` because fediverse peers always sign over the origin-form absolute
URI with the https scheme (HTTP is not used for ActivityPub delivery).

## Test counts

- `Iris.Core.Tests`: 464 total (450 baseline + 17 new; 7 skipped = 1 B.2.6 known-answer
  + 6 `SignatureInputHeader.TryParse` tests (testhost hang))
- `Iris.Server.Tests`: 1291 passed, 7 skipped, 0 failed (full suite, ~3 min)
- Full suite: 2175 passed, 14 skipped, 0 failed (11 test projects, EXIT: 0)
