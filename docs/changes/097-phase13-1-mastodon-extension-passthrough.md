# 097 — Phase 13.1: Mastodon extension passthrough tests

> 2026-08-30 · Phase 13 (Live Federation Compatibility) · CI-testable sub-slice 13.1

## What was built

Integration tests proving the "don't drop unknown properties" guarantee (F-01, change 064) applied
specifically to the Mastodon surface that Phase 13 live-interop will exercise. Mastodon-specific
extension properties are not in the ActivityStreams 2.0 vocabulary that `KristofferStrube.ActivityStreams`
models, so they land in the library's `[JsonExtensionData]` and are forwarded opaquely. This test proves
the guarantee end-to-end: an inbound object carrying these Mastodon extensions is stored and served back
with every extension property preserved on the wire.

## The tests

**`MastodonExtensionPassthroughIntegrationTests`** (5 new integration tests):

- `Note_WithSensitiveFlag_RoundTripsThroughStore` — a `Note` with a top-level `sensitive` boolean
  (Mastodon NSFW flag) is stored and served back with `sensitive: true` preserved.
- `Note_WithTootEmoji_RoundTripsThroughStore` — a `Note` with a `toot:emoji` array (Mastodon custom
  emoji) is stored and served back with the full emoji array preserved verbatim.
- `Note_WithSensitiveAndTootEmoji_RoundTripsTogether` — both extensions together round-trip.
- `Question_PollObject_RoundTripsAsOpaqueObject` — a `Note` with an embedded `poll` object (Mastodon
  poll shape: `options`, `votesCount`, `endsAt`, `expired`, `totalVotes`) is stored and served back with
  the full poll object preserved verbatim as an opaque extension.
- `Actor_WithMastodonExtensions_RoundTripsThroughActorDoc` — a `Person` actor with
  `manuallyApprovesFollowers` and `memorable` (Mastodon actor extensions) is served via the
  actor-document endpoint with both extensions preserved.

## Key finding

During implementation, it was discovered that `source` is a **reserved** ActivityStreams property name
(modeled as a typed property on the library's `Object` type), not a free-form extension slot. A Mastodon
actor's `source` object (HTML/markdown settings) is therefore captured into the typed `Source` property,
not `ExtensionData`, and is serialized as an empty object when unset. The actor test uses
`manuallyApprovesFollowers` and `memorable` instead — both are genuine Mastodon extensions that land in
`ExtensionData` and round-trip correctly.

## Files changed

- `tests/Iris.Server.Tests/MastodonExtensionPassthroughIntegrationTests.cs` — 5 new integration tests.

## Decisions

- **Test the guarantee, not the feature.** Iris does not implement Mastodon polls, custom emoji, or
  NSFW marking — it forwards them opaquely. The tests prove the forwarding guarantee (the "don't drop
  unknown properties" behavior from F-01) holds specifically for the Mastodon surface. This is the
  correct scope for a CI-testable slice: it validates the interop contract without requiring a live
  Mastodon instance.

## Test count

961 → 966 (+5), 0 failures.
