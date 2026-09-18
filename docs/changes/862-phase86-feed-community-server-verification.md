# 86.2 — Community-type interop: server-level verification of the `Feed` → `Group` mapping

**Phase 86.2** · Community-type interop (Feed → Group mapping) — verification slice

## What was built

86.1 established the `"Feed" → Group` type mapping in `ActivityJson.CreateOptions()`. 86.2 is the
verification slice: it proves a real PieFed `Feed` community document flows correctly through the
**server's** community pipeline (stores + inbox handlers) — not just the core deserializer and the
file-backed store that 86.1 already covered.

New test file: `tests/Iris.Server.Tests/FeedCommunityServerIntegrationTests.cs` (6 integration
tests). All drive the captured `piefed-group-actor.json` fixture (`"type":"Feed"`) through the
real server components:

| # | Test | Seam it exercises |
|---|------|-------------------|
| 1 | `FeedCommunityDocument_Deserializes_ToGroup` | Polymorphic converter: `"Feed"` materializes as `Group`; `Group`-specific fields (`PreferredUsername`, `Name`) land. |
| 2 | `FeedCommunity_StoreRoundTrip_ReadsBackAsGroup` | `ICommunityStore.PutCommunityAsync` + `TryGetCommunityAsync` round-trip as a `Group`. |
| 3 | `Follow_OfFeedCommunity_RecognizedAsCommunityFollow_RecordsEdgesAndSchedulesAccept` | `FollowActivityHandler` community branch — gated on `TryGetCommunityAsync` recognizing the `Feed` community: follow/follower edges recorded, `Accept` scheduled back to the follower. |
| 4 | `Follow_OfFeedCommunity_InboundFollowLandsInCommunityOutbox` | The inbound community follow is surfaced in the community's outbox. |
| 5 | `FeedCommunity_WireDeserialization_MatchesGroupCastSites` | **Wire-level**: a real `"Feed"` document matches every `as Group` / `is Group` / `is not Group` cast site the mapping fixes; wire type preserved as `"Feed"`. |
| 6 | `FeedCommunity_WireDocument_StoredAndReadBack_AsGroup` | **Wire-level end-to-end**: `Feed` document deserialized → stored → re-read (fresh-store path), the store's `as Group` read cast succeeds, and the persisted JSON still carries the `"Feed"` wire type. |

Tests 5 and 6 are the genuine proof of the mapping. They are **load-bearing**: with the
`ObjectTypes.Types["Feed"] = typeof(Group)` registration disabled, all 6 tests fail; with it
present, all 6 pass (verified by toggling the registration and re-running).

## Why a handler-level Update test would have been hollow

An earlier draft of this file contained two handler-level tests that drove an `Update` activity
whose embedded object was a `Group` constructed **in code** (not deserialized from wire JSON).
Those were removed because they do **not** actually prove the mapping:

- `Group` **derives from** `Actor` in the ActivityStreams library.
- `UpdateActivityHandler.HandleAsync` dispatches on `updated is Actor` (line 124) — which fires for
  *any* `Group`, regardless of its wire type — before the community-specific `is Group` cast is
  ever reached.
- So a test that builds a `Group` in C# and hands it to the handler passes whether or not the
  `"Feed" → Group` registration exists. The wire is where the type is *decided*; a code-constructed
  `Group` bypasses that decision entirely.

The replacement wire-level tests (5 and 6) instead deserialize a real `"type":"Feed"` document —
the only path where the mapping is observable — and assert the result matches the exact cast/pattern
sites the mapping fixes.

## Key changes

| File | Change |
|------|--------|
| `tests/Iris.Server.Tests/FeedCommunityServerIntegrationTests.cs` | **New** (6 integration tests). Uses a `RecordingDeliveryService` to capture scheduled `Accept` deliveries, an `InMemoryPersistenceProvider` for the community/object/follower stores, and the real `FollowActivityHandler` + `ActivityJson` wire path. |

No production code changed in this slice — 86.1 already made the fix; 86.2 only adds the
verification.

## Decision: wire-level over handler-level

- **Wire-level (chosen).** Deserialize a real `"Feed"` document and assert the `Group` cast sites
  match. This is where the mapping is actually load-bearing, and the tests fail without the
  registration (proven).
- **Handler-level with in-code `Group` (rejected).** `Group : Actor` means the handler's `is Actor`
  dispatch fires for any `Group` regardless of wire type, so such tests pass without the mapping and
  prove nothing about it.

## Test counts

- **6 new** integration tests (`FeedCommunityServerIntegrationTests`) — all passing, all
  load-bearing (fail without the mapping).
- Full suite green: `dotnet build` 0 warn / 0 err; `dotnet test --filter "Category!=Slow"`
  0 failed across all 11 assemblies (1071 Iris.Server.Tests passing, up from 1065).

## Spec references

- ActivityStreams `Group` (an `Actor` that represents a group of members):
  <https://www.w3.org/TR/activitystreams-v2/#group-objects>.
- Pleroma's `Feed` type is a Pleroma-fork extension for communities; standard Pleroma and
  ActivityPub use `Group`. PieFed (piefed.social) is a Pleroma fork that uses `Feed`.

## Follow-up

Phase 86 is now complete (86.1 mapping + 86.2 server verification). The remaining open question
from 81.2 — *other* Pleroma-fork community terms (if any exist beyond `Feed`) — can be handled by
adding further entries to the same `ObjectTypes.Types` registry at the same centralized point
(`ActivityJson.CreateOptions()`), if/when a real instance is observed. No code change is needed
until then.
