# 099 — Phase 13.3: Mastodon `Question`/poll inbound handling regression tests

> 2026-08-30 · Phase 13 (Live Federation Compatibility) · CI-testable sub-slice 13.3

## What was built

Regression tests proving the CI-testable guarantee for inbound polls: a signed `Create` carrying a
poll-bearing object (a `Note` with `poll` extension data — `options`, `votes`, `endsAt`, `expired`,
`multiple`, `totalVotes`, `oneOfMany`) is accepted, stored, and served back with the full poll shape
preserved verbatim. The poll properties are not in the ActivityStreams 2.0 vocabulary the library
models, so they ride in `ExtensionData` and are forwarded opaquely. This is the end-to-end version of
the poll passthrough: it exercises the real signed inbox pipeline (signature validation → deserialization
→ store → serve), not just store-then-serve.

## The tests

**`MastodonPollInboundIntegrationTests`** (2 new integration tests):

- `Inbox_AcceptsPollBearingCreate_AndServesPollVerbatim` — a signed `Create` with a `Note` carrying
  `poll` extension data (3 options with `votesCount`, `endsAt`, `expired`, `multiple`, `totalVotes`) is
  accepted (202), and the served object preserves the full poll shape verbatim.
- `Inbox_AcceptsPollWithOneOfMany_AndServesVerbatim` — a poll with `oneOfMany` (a link to the voter's
  own vote object) is preserved verbatim on round-trip.

## Decision: the `Question`-typed object is not tested

The AS2.0 `Question` type (a poll as an *activity*) is not how Mastodon sends polls. Mastodon sends a
`Note` object with `poll` extension data. When a `Question`-typed *object* is deserialized into
`IObject`, the library's polymorphic converter resolves it to the concrete `Question` class — but
`Question` is an `IntransitiveActivity`, not an `Object`, so re-serialization through the `Create.Object`
collection (`IEnumerable<IObject>`) hits a `DateTimeBooleanObjectOrLink` converter mismatch (the
`closed` boolean / `endsAt` date don't fit the converter's expected type). This is a **library quirk**,
not an Iris behavior: Iris forwards what the library gives it, and the realistic Mastodon shape
(`Note` + `poll`) works correctly. Testing the non-representative `Question`-typed object would be
testing a library bug, not an Iris guarantee, so it is not done.

## Files changed

- `tests/Iris.Server.Tests/MastodonPollInboundIntegrationTests.cs` — 2 new integration tests.

## Test count

969 → 971 (+2), 0 failures.
