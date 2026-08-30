# 100 — Phase 13.4: Mastodon `sensitive` flag inbound handling regression tests

> 2026-08-30 · Phase 13 (Live Federation Compatibility) · CI-testable sub-slice 13.4

## What was built

Regression tests proving the CI-testable guarantee for the `sensitive` flag: `sensitive` is a boolean
Mastodon sets on objects (most commonly media — `Image`/`Video` — but also `Note`) to indicate the
content is not safe for work. It is not in the ActivityStreams 2.0 vocabulary the library models (no
`Sensitive` property on any object type), so it lands in `ExtensionData` and is forwarded opaquely.
This test proves the guarantee end-to-end over the real signed inbox pipeline: a signed `Create`
carrying an object with `sensitive` is accepted, stored, and served back with the flag preserved
verbatim.

## The tests

**`MastodonSensitiveFlagInboundIntegrationTests`** (3 new integration tests):

- `Inbox_AcceptsSensitiveNote_AndServesVerbatim` — a `Note` with `sensitive: true` is accepted (202)
  and served with the flag preserved.
- `Inbox_AcceptsNonSensitiveNote_AndServesVerbatim` — a `Note` with `sensitive: false` is accepted and
  served with the flag preserved (proving it is not dropped or coerced to a default).
- `Inbox_AcceptsSensitiveImage_AndServesVerbatim` — the realistic Mastodon media shape: an `Image`
  embedded in the `Note`'s `image` property with `sensitive: true` is preserved verbatim.

## Decision

The `sensitive` flag is an extension property (not in AS2.0), so it is forwarded opaquely via
`ExtensionData` — the same mechanism as the other Mastodon extensions in 13.1. The distinct angle for
13.4 (vs 13.1, which tested `sensitive` via store-then-serve) is the **inbound over the signed
pipeline**: the flag survives signature validation → deserialization → store → serve, and is preserved
on both a `Note` and an embedded `Image` (the realistic media shape).

## Files changed

- `tests/Iris.Server.Tests/MastodonSensitiveFlagInboundIntegrationTests.cs` — 3 new integration tests.

## Test count

971 → 974 (+3), 0 failures.

**All four CI-testable Phase 13 sub-slices (13.1–13.4) are now complete.** The remaining sub-slices
(13.5–13.10, live-interop) are blocked on Phase 9 (FQDN/TLS) and real partner instances.
