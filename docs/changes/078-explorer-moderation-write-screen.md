# 078 — Phase 8 S7: Explorer moderation write screen (mute/block/flag)

> 2026-08-30 · Phase 8 (Sample) · Slice S7 (moderation write surface)

## What was built

The Blazor WASM explorer's **moderation write screen** — the first of the S7 follow-up write-surface
screens — is complete: the actor detail screen now offers **Mute/Unmute**, **Block/Unblock**, and
**Flag/Unflag** against a loaded actor. Each action is driven through `ExplorerSession.GetClient()` and
pinned by an in-process test against a live `SampleServer` (the same way the compose/follow/like screens
are, per [076](076-explorer-write-screens.md)). This completes the F-07 moderation write surface in the
explorer.

The screen makes the delivery-model split visible to the user: **block and flag are signed writes to the
acting actor's own outbox** (the delivery model from [077](077-delivery-model-outbox-write-surface.md) —
the instance records the edge and federates to the target's inbox), while **mute is a local,
Basic-authenticated decision** (no ActivityStreams `Mute` type, no federation — a `204` local
moderation endpoint).

## Key types & files

- **`samples/SampleBlazorClient/Pages/ActorDetail.razor`** — a new **Moderation** card (below the
  follow/unfollow card) with six buttons (Mute/Unmute/Block/Unblock/Flag/Unflag), reusing the existing
  `FollowUnfollowAsync(Func<Iri, Iri, Task<int>>)` write helper. A caption explains the signed-outbox
  (block/flag) vs. local-Basic-auth (mute) split.
- **`src/Iris.Client.Extensions/IrisClientOptions.cs`** (new property) — `LocalModeration` (default
  `true`): when set, the acting user's Basic-auth credentials (`ProxyCredentials`) are also passed to the
  built client as `ActivityPubClientOptions.LocalCredentials`, enabling the local `MuteAsync`/
  `UnmuteAsync` path. When `false` the client has no local credentials and those methods throw.
- **`src/Iris.Client.Extensions/IrisClientFactory.cs`** — maps `IrisClientOptions.LocalModeration` →
  `ActivityPubClientOptions.LocalCredentials`.
- **`tests/SampleBlazorClient.Tests/S7ScreenTests.cs`** (3 new facts) — the moderation writes driven
  in-process against the dial-base host:
  - `Moderation_MuteUnmute_RecordsAndRemovesMuteEdge` — `MuteAsync` → `204` + the muter's `mutes`
    collection lists the target + `IsMutedAsync` true; `UnmuteAsync` → edge gone.
  - `Moderation_BlockUnblock_RecordsAndRemovesBlockEdge` — `BlockAsync` → `202` + the blocker's `blocks`
    collection lists the target + `IsBlockedAsync` true; `UnblockAsync` → edge gone.
  - `Moderation_FlagUnflag_RecordsAndRemovesFlagEdge` — `FlagAsync` → `202` + the flagger's `flags`
    collection lists the target + `HasFlaggedAsync` true; `UnflagAsync` → edge gone.

## Tests

`SampleBlazorClient.Tests` 42 → 45 (3 new moderation facts). Full solution green — **881 tests, 0
failures** — build clean (0 warnings).

## Decisions

- **Mute rides the client's local-auth pipeline.** A mute is not a signed ActivityPub delivery (there is
  no ActivityStreams `Mute` type); it is a Basic-authenticated `POST {actor}/mutes/{target}` to the
  acting actor's own instance. The explorer's pre-configured client therefore needs the acting user's
  Basic-auth credentials as `LocalCredentials`. The cleanest surface for that is a `LocalModeration`
  flag on `IrisClientOptions` (default `true`, mirroring `UseProxyFallback`): the same acting user's
  Basic auth that authenticates the session and drives the proxy fallback is reused for local
  moderation, so a host that already sets `ProxyCredentials` (the sample does) gets mute support with no
  extra configuration.
- **Block/flag are outbox writes, mute is a local decision.** The screen's caption states the split
  explicitly. Block/flag go through the signed pipeline to the actor's own outbox (per the [077]
  delivery model); the server records the edge and, for a remote target, federates it. Mute never leaves
  the instance.
