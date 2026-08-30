# 087 — Phase 12: F-15 — outbox-publish Announce full fan-out to all remote followers

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Gap closure (F-15)

## What was built

The outbox-publish `Announce` path (`POST /ap/v1/u/{handle}/outbox`) now federates the boost to
**every** remote, non-blocked follower, mirroring the `Create` fan-out. This closes gap **F-15**:
previously an `Announce` published to the outbox was recorded locally (in the author's outbox) but
never delivered to any remote follower — the `OutboxPublishHandler` had no `Announce` branch, so it
fell through to the single-recipient `switch` where `recipientIri` was `null`, and no delivery was
scheduled.

## The fix

Two changes in `src/Iris.Server/ActivityPubServerExtensions.cs`:

1. **New `Announce` branch in `OutboxPublishHandler`** (between the `Create` and the
   single-recipient `else`): an `Announce` fans out to every remote, non-blocked follower via the
   shared `GetRemoteNonBlockedFollowersAsync` helper, delivering the signed `Announce` to each
   follower's inbox (signed as the acting local actor). Unlike a `Create`, an `Announce` carries no
   embedded object — it is a reference to an existing object IRI — so no object-store write is needed.

2. **`GetRemoteNonBlockedFollowersAsync` extracted** from `RecordCreateLocalAsync`: the
   "enumerate the author's remote, non-blocked followers" loop (skip local actors, skip blocked
   followers) is now a shared private helper used by both the `Create` and `Announce` branches.
   `RecordCreateLocalAsync` still handles the embedded-object storage (object store + reply edge)
   before delegating to the shared helper for the recipient list.

## Tests

`tests/Iris.Server.Tests/OutboxAnnounceFanOutIntegrationTests.cs` (new, 3 tests) — a 2-instance
topology (A = author/alice, B = bob-follower):

- **`OutboxPublish_AnnounceWithRemoteFollower_FederatesToFollower`:** alice posts an `Announce`
  to her outbox; B validates the federated `Announce` (resolving alice's key from A's actor doc)
  and stores it. Proves the outbound Announce federation (F-15).
- **`OutboxPublish_AnnounceWithNoRemoteFollowers_SurfacesLocallyOnly`:** an author with no
  followers posts an `Announce`; it is recorded in the author's outbox but no delivery is
  scheduled.
- **`OutboxPublish_AnnounceWithBlockedRemoteFollower_SkipsBlocked`:** a remote follower who is
  blocked by the author is skipped — the `Announce` is not delivered to them.

## Files changed

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `OutboxPublishHandler` `Announce` branch +
  `GetRemoteNonBlockedFollowersAsync` extraction.
- `tests/Iris.Server.Tests/OutboxAnnounceFanOutIntegrationTests.cs` — new, 3 integration tests.

## Test count

896 → 899 (+3), 0 failures.
