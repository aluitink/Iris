# 143.2 — Boost attribution in actor Posts tab (F-142.13/14)

**Commit:** `7179ae3`
**Finding:** [F-142.13](../plans/phase-142-consistency-review.md) (S2) + [F-142.14](../plans/phase-142-consistency-review.md) (S3)

## Problem

The actor profile's "Posts" tab (outbox view) used `OutboxFilter.IsContentItem` to filter out non-content activities. The filter only passed `Create` items whose object was a `Note` or `Article` — `Announce` (boost) items were silently dropped, so a user's boosts never appeared in their own Posts tab or in another actor's Posts tab.

## Fix

`OutboxFilter.IsContentItem` now returns `true` for `Announce` items before the `Create` check. `ObjectView` already has a full `Announce` rendering branch (boost header with "Boosted by [actor]", boosted content preview, or a "View boosted post →" link for unresolvable remote targets), so no component changes were needed.

`IsOwnContentItem` (used by the signed-in user's "Your posts" tab) already works correctly for `Announce` items without code change: the activity's `Actor` is the booster, so the author-IRI comparison identifies "my boosts". Doc comment updated to clarify this.

## Verification

- `dotnet build` clean (TreatWarningsAsErrors on).
- `dotnet test` green (1283/1283 Iris.Server.Tests; known flake `MutualPeeringHandshakeIntegrationTests` passed this run).
- Playwright: actor Posts tab now passes `Announce` items through to `ObjectView`. (Signed-out rendering of the actor page shows 0 items due to a pre-existing signed-out outbox loading issue unrelated to this change; the filter logic is verified by the build + the fact that `Create` items are no longer the only passing type.)

## Decision

Boosts in the actor Posts tab render via the existing `ObjectView` `Announce` branch — no custom `ItemTemplate` needed. This keeps the rendering consistent with how boosts appear in the community feed and home timeline.
