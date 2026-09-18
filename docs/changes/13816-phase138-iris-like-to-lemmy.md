# 13816 — Iris → Lemmy Likes (138.16)

## Summary

Verified that an Iris-authored `Like` of a Lemmy-sourced post (a `Page` on another instance) is
correctly delivered to the object's home (the Lemmy stand-in) via the existing outbound Like
federation path (Phase 24.1 `ResolveObjectOwnerForDeliveryAsync` + server→server delivery). No new
production code was required.

## Verification

Two two-instance integration tests (`LemmyLikeOutboundIntegrationTests`):

1. **`IrisLikeOfLemmyPage_DeliveredToObjectHome_AndRecordedThere`** — alice (B) likes a `Page` owned
   by bob (A, the Lemmy stand-in). B resolves the Page's owner by fetching it over the wire, records
   the local like edge, and delivers the `Like` to A (the object's home). A records the like edge
   (alice → Page) and the `/likes` collection on A lists alice's like.

2. **`IrisLikeOfLocalPost_RecordedLocally_NoCrossInstanceDelivery`** — alice (B) likes a local `Note`
   (owned by alice, stored on B). The like is recorded locally on B; no cross-instance delivery to A
   occurs (the object's owner is local).

Both tests pass.

## Why no production code change

The outbound Like path (in `OutboxPublishHandler`'s non-Create branch) already handles this:
- `RecordLikeLocalAsync` records the local like edge AND resolves the object's owner via
  `ResolveObjectOwnerForDeliveryAsync` (local store lookup or remote wire fetch of the object's
  `attributedTo`).
- The resolved owner is used as the delivery recipient: if remote, the `Like` is delivered over the
  wire to the owner's inbox (server→server, signed as the acting local actor).

This is the same mechanism as Phase 136.8 (cross-instance boost integrity) for `Announce`, applied
to `Like` via the shared `recipientIri` delivery path.

## Files

- `tests/Iris.Server.Tests/LemmyLikeOutboundIntegrationTests.cs` (new) — 2 integration tests
- `docs/plans/phase-138-lemmy-community-integration.md` (updated) — 138.16 marked `[x]`
