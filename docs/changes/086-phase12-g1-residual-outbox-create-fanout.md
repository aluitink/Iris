# 086 — Phase 12: G-1 residual — outbox-publish Create full fan-out to all remote followers

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Gap closure (G-1 residual)

## What was built

The outbox-publish `Create` path (`POST /ap/v1/u/{handle}/outbox`) now fans out to **every** remote,
non-blocked follower, not just the first. This closes the **G-1 residual**: the core outbound-`Create`
capability was live in Phase 11 (Slice 11.7) via the *inbound* `CreateActivityHandler` (a local post
delivered to the author's own inbox federates to all remote followers), but the *outbox-publish* write
surface (the client's `PostNoteAsync` path, which POSTs directly to the author's outbox) delivered the
`Create` to only the **first** remote follower.

## The fix

Two changes in `src/Iris.Server/ActivityPubServerExtensions.cs`:

1. **`RecordCreateLocalAsync`** (private helper) changed from `Task<Iri?>` (returning the first remote,
   non-blocked follower) to `Task<IEnumerable<Iri>>` (returning **all** remote, non-blocked followers).
   The loop over `GetFollowersAsync` now accumulates every matching follower into a list instead of
   returning on the first one, mirroring `CreateActivityHandler`'s fan-out loop.

2. **`OutboxPublishHandler`** restructured: the `Create` branch is now handled separately from the
   single-recipient activity types (`Follow`, `Block`, `Flag`, `Like`, `Undo`). The `Create` branch
   iterates over every recipient from `RecordCreateLocalAsync` and calls
   `delivery.DeliverToActorAsync(recipient, activity, actorIri, ct)` for each, server-delivering the
   signed `Create` to every remote follower's inbox. All other activity types keep the existing
   single-recipient `switch` + single-delivery logic.

## Tests

`tests/Iris.Server.Tests/OutboxCreateFanOutIntegrationTests.cs` (new, 3 tests) — a 3-instance
topology (A = author/alice, B = bob-follower, C = carol-follower):

- **`OutboxPublish_CreateWithTwoRemoteFollowers_FederatesToBoth`:** alice posts a `Create` to her
  outbox; both B and C validate the federated `Create` (resolving alice's key from A's actor doc)
  and store it. Proves the full fan-out (not just the first follower).
- **`OutboxPublish_CreateWithNoRemoteFollowers_SurfacesLocallyOnly`:** an author with no followers
  posts a `Create`; it is recorded in the author's outbox (local surfacing) but no delivery is
  scheduled.
- **`OutboxPublish_CreateWithBlockedRemoteFollower_SkipsBlocked`:** a remote follower who is
  blocked by the author is skipped — the `Create` is not delivered to them.

The test wiring uses a `RoutingHandler` (dispatches A's outbound delivery to B or C by request
host) and a `RoutingFetcher` (resolves actor documents by host: A → A's `TestServer`, B → B's,
C → C's). The `RoutingHandler` clones the request (like `LazyHandler`) because the inner
`RetryHandler` pipeline may replay the same `HttpRequestMessage`.

## Files changed

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `RecordCreateLocalAsync` return type +
  `OutboxPublishHandler` `Create` branch.
- `tests/Iris.Server.Tests/OutboxCreateFanOutIntegrationTests.cs` — new, 3 integration tests.

## Test count

893 → 896 (+3), 0 failures.
