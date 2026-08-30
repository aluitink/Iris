# 088 — Phase 12: F-19 — typed `DeliveryResult` for all write operations

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Gap closure (F-19)

## What was built

`DeliverAsync` and all convenience write methods on `IActivityPubClient` (`FollowAsync`,
`UndoFollowAsync`, `LikeAsync`, `BlockAsync`, `UnblockAsync`, `FlagAsync`, `UnflagAsync`,
`MuteAsync`, `UnmuteAsync`, `SubscribeRelayAsync`, `UnsubscribeRelayAsync`, `PostNoteAsync`,
`PostReplyAsync`) now return `Task<DeliveryResult>` instead of `Task<int>`. A caller can
distinguish a 2xx acceptance from a 401/404/429 without pattern-matching on a bare integer,
and can inspect the response body when the server returns an error message.

## The fix

Two changes in `src/Iris.Client`:

1. **New `DeliveryResult` record** (`DeliveryResult.cs`): a sealed record with
   `int StatusCode`, `bool IsSuccess`, and `string Body`.

2. **`IActivityPubClient` + `ActivityPubClient`**: all 14 write methods changed from
   `Task<int>` to `Task<DeliveryResult>`. `DeliverAsync` reads the response body and
   constructs the `DeliveryResult`. The shared private helpers (`LocalModerateAsync`,
   `LocalLocalDecisionAsync`) updated to match. All test call sites, sample `.razor` pages,
   and stub `IActivityPubClient` implementations updated.

## Tests

- **`DeliverAsync_PostsActivityWithActivityJsonContentType_ReturnsStatusCode`** (updated):
  now also asserts `result.IsSuccess`.
- **`DeliverAsync_401_ReturnsUnsuccessfulResult`** (new): a 401 response produces a
  `DeliveryResult` with `StatusCode = 401`, `IsSuccess = false`, and the response body.
- **`DeliverAsync_202WithBody_ReturnsSuccessResultWithBody`** (new): a 202 with a body
  produces a `DeliveryResult` with `StatusCode = 202`, `IsSuccess = true`, and the body.

## Files changed

- `src/Iris.Client/DeliveryResult.cs` — new, the typed delivery result record.
- `src/Iris.Client/ActivityPubClient.cs` — implementation updated.
- `src/Iris.Client/IActivityPubClient.cs` — interface updated.
- `samples/SampleBlazorClient/Pages/{ActorDetail,Compose,ObjectPage}.razor` — sample UI updated.
- 20+ test files — call sites updated to use `.StatusCode` / `.IsSuccess` / `.Body`.

## Test count

899 → 901 (+2), 0 failures.
