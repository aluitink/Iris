# 098 — Phase 13.2: `ld+json` accept behavior regression tests

> 2026-08-30 · Phase 13 (Live Federation Compatibility) · CI-testable sub-slice 13.2

## What was built

Regression tests proving Decision #4's accept half: Iris accepts `application/ld+json` on inbound (the
legacy MIME type some older implementations use), in addition to `application/activity+json`. The
server's inbox handler reads the raw JSON body and deserializes it via `ActivityJson.Deserialize`
regardless of the `Content-Type` header — it does not reject the legacy MIME type. The `ld+json` accept
behavior was already correct; this change adds the regression test that proves it.

## Key detail

The `ServerToServer` signature profile covers `(request-target) host date digest content-type`. Because
`content-type` is part of the signed base, the test signs the request **with** `Content-Type:
application/ld+json` from the start (not by re-sending a signed request with a different content type,
which would break the signature). The test manually constructs the `HttpRequestMetadata`, signs it with
the `HttpSignatureSigner`, and sends the signed POST with `Content-Type: application/ld+json`.

## The tests

**`LdJsonAcceptIntegrationTests`** (3 new integration tests):

- `Inbox_AcceptsLdJsonContentType` — a signed `Create` delivered with `Content-Type:
  application/ld+json` returns 202 (Accepted).
- `Inbox_AcceptsLdJson_AndProcessesActivity` — the accepted `Create` is actually processed: the embedded
  `Note` is stored and served by its IRI (proving the activity was deserialized and the `Create` handler
  ran, not just accepted).
- `Inbox_AcceptsLdJson_FollowActivity` — a signed `Follow` with the legacy content type is also
  accepted (202), proving the accept behavior is not specific to `Create`.

## Files changed

- `tests/Iris.Server.Tests/LdJsonAcceptIntegrationTests.cs` — 3 new integration tests.

## Decisions

- **Test the accept, not the produce.** Decision #4: Iris produces `application/activity+json` (the
  canonical ActivityPub MIME type) and accepts both `activity+json` and `ld+json`. The produce side is
  already correct and is covered by existing tests (every served endpoint asserts
  `application/activity+json`). The accept side had no dedicated regression test. This change adds it.
  Changing the produce side to `ld+json` would be a regression (it would break clients that expect the
  canonical type), so it is not done.

## Test count

966 → 969 (+3), 0 failures.
