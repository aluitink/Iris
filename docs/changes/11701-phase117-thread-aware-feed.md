# 117.1 — Thread-Aware Feed: inReplyTo Detection + `?depth` Parameter

## What

Improved the followed feed's reply filtering (117.1) to use `inReplyTo` as the deterministic primary
signal for reply detection, with the audience heuristic as a fallback. Added a `?depth` query parameter
to the feed endpoint that includes replies from followed actors (the thread's top replies appear inline
in the feed rather than only under the parent post's replies section).

## Key Changes

### Reply Detection (FeedService)

- **Primary signal:** `contentObj.GetParentIri()` (reads `inReplyTo`) — deterministic, present on all
  properly-formed ActivityPub replies.
- **Fallback:** `contentObj.GetAudienceIris().Count > 0` — the Phase 101 heuristic, used when
  `inReplyTo` is absent (some remote servers omit it).
- **Own replies:** always kept (merged from the actor's own outbox, not filtered).
- **Non-Create activities:** (Announce, Like, Follow) are never treated as replies.

### `?depth` Query Parameter

- `GET /ap/v1/u/{handle}/feed?depth=1` — includes first-level replies from followed actors.
- When `?depth` is absent or 0 — all replies from followed actors are filtered (default).
- Advertised as `iris:depth: true` on the collection's page-1 document (capability extension).
- `IrisExtensionTerms.Depth` added to `Iris.Core` (shared between server and client).

### Interface

- `IFollowFeedService.GetFeedAsync` — new `int? threadDepth = null` parameter.
- `FeedService.BuildFeedAsync` — accepts `threadDepth`, skips reply filtering when > 0.

### Collection Page Serialization

- `BuildCollectionPageDocument` / `SerializeCollectionPage` — new `supportsDepth` parameter.
- Feed handler passes `supportsDepth: true`.

## Tests

5 new tests in `FeedServiceTests`:

1. `Feed_FollowReply_WithInReplyTo_IsFilteredOut` — inReplyTo detection (primary).
2. `Feed_FollowReply_WithOnlyAudience_IsFilteredOut` — audience fallback (no inReplyTo).
3. `Feed_FollowReply_ThreadDepth1_IncludesReply` — `?depth=1` includes replies.
4. `Feed_OwnReply_AlwaysKept_RegardlessOfDepth` — own replies always kept.
5. `Feed_FollowAnnounce_NotAffectedByReplyFilter` — Announce never filtered.

Total: 1,130 server tests pass (1,833 across all test projects).

## Decisions

- **`?depth=1` includes ALL replies** (not just first-level): determining reply depth requires walking
  the `inReplyTo` chain, which would need a store lookup per item. The ObjectDetail page already renders
  the full thread (replies section), so the feed's role is to show *some* replies inline for context.
  A future slice can add true depth-limited filtering.
- **`inReplyTo` is the primary signal** (not audience): it's deterministic and present on all
  well-formed ActivityPub replies. The audience check is a heuristic (a DM-style post to a single
  actor looks like a reply) but is kept as a fallback for remote objects that omit `inReplyTo`.

## Files Changed

- `src/Iris.Core/IrisExtensionTerms.cs` — `Depth` constant.
- `src/Iris.Server/Services/IFollowFeedService.cs` — `threadDepth` param.
- `src/Iris.Server/Services/FeedService.cs` — `IsFollowReply` (inReplyTo + fallback), `BuildFeedAsync` (threadDepth).
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `?depth` parsing, `iris:depth` capability.
- `tests/Iris.Server.Tests/Services/FeedServiceTests.cs` — 5 new tests, `AddReply` seeds `inReplyTo`.
