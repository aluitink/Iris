# 992 — Actor Count Background Enrichment

**Date:** 2026-09-18
**Status:** Complete

## Summary

Added `ActorCountRefreshService`, a background service that pre-computes the per-actor counters (`iris:postsCount`, `iris:followersCount`, `iris:followingCount`) and persists them onto stored actor documents, following the same pattern as the Phase 151 `ObjectInteractionCountRefreshService`.

## Problem

The actor-document and community-document read paths computed these counters on every read by walking the actor's outbox (`IActivityStore.GetOutboxAsync`) and the follow store (`IFollowStore.GetFollowersAsync` / `GetFollowingAsync`) — an O(outbox + follows) sweep per actor, per request. At scale this is wasted work: the counters are cacheable, change only when a post/follow/unfollow is recorded, and the same actor is read far more often than it is interacted with.

## What was built

### `ActorCountRefreshService` (new)

- **Location:** `src/Iris.Server/Stores/ActorCountRefreshService.cs`
- **Type:** `BackgroundService`
- **Behavior:** On a fixed interval (default 30s, configurable via `ActivityPubServerOptions.ActorCountRefreshInterval`), enumerates every stored actor via `IActorStore.ListActorsAsync`, computes the three counters, and writes them onto the actor's `ExtensionData` under the deployment's `iris:` namespace. Re-stores via `PutActorAsync` only when a value changed (idempotent).
- **Posts classification:** Mirrors the read-time `CountPostsAsync` in `ActivityPubServerExtensions`: an outbox item counts as a post when it is an `Announce` (boost) or a `Create` whose object is a `Note` or `Article`. Social/moderation activities are excluded.
- **Null persistence:** Inert (no-op) when the `IPersistenceProvider` is null.
- **Error handling:** Never throws into the host; a failed pass is logged and the next tick retries.

### `ActivityPubServerOptions.ActorCountRefreshInterval` (new option)

- Default: 30s. Non-positive value disables the periodic refresh (startup pass still runs).

### DI registration

Registered via `AddHostedService` (not `TryAddSingleton<IHostedService>`) in `AddActivityPubServer`, so it coexists with the other hosted services. Resolves the possibly-null `IPersistenceProvider` via `sp.GetService`.

### `InternalsVisibleTo`

Added `src/Iris.Server/AssemblyInfo.cs` with `[assembly: InternalsVisibleTo("Iris.Server.Tests")]` so tests can call the `internal RefreshOnceAsync` method directly.

## Test counts

12 new unit tests in `tests/Iris.Server.Tests/Stores/ActorCountRefreshServiceTests.cs`:

| Test | Verifies |
|------|----------|
| `RefreshOnce_NoActors_IsNoOp` | Empty actor store → no-op |
| `RefreshOnce_NullPersistence_IsInert` | Null persistence → no-op, no crash |
| `RefreshOnce_EmptyOutboxAndFollows_WritesZeroCounts` | Actor with no activity → zero counts written |
| `RefreshOnce_WithPosts_WritesPostsCount` | 2 Create(Note) outbox items → postsCount=2 |
| `RefreshOnce_WithAnnounce_CountsAsPost` | 1 Announce outbox item → postsCount=1 |
| `RefreshOnce_WithFollows_WritesFollowerAndFollowingCounts` | Follow edges → correct follower/following counts |
| `RefreshOnce_Idempotent_SecondPassDoesNotRewrite` | Same counts → no re-store (same instance) |
| `RefreshOnce_UpdatesCounts_WhenActivityChanges` | New post → count updates on next pass |
| `RefreshOnce_MultipleActors_UpdatesEach` | Independent per-actor counts |
| `RefreshOnce_PreservesExistingExtensions` | Pre-existing ExtensionData entries preserved |
| `RefreshOnce_NoNamespace_IsNoOp` | Minimal options → uses default namespace, no crash |
| `RefreshOnce_FollowActivityInOutbox_IsNotCountedAsPost` | Follow in outbox → postsCount=0 |

## Key decisions

1. **Mutate in place vs deep-copy:** The service mutates the in-memory actor's `ExtensionData` and re-stores the same instance. This preserves all existing extension properties (including the owner-only `privateKey` PEM) through the re-store. The read path (`BuildActorDocumentAsync`) deep-copies the actor before serving, so it never sees the stored mutation directly.

2. **Separate service vs extending ObjectInteractionCountRefreshService:** A separate service keeps the responsibilities clean (object interaction counters vs actor counters) and allows independent interval tuning. The two services run concurrently on the same host.

3. **Not yet wired into the read path:** The read path (`BuildActorDocumentAsync`, `AddActorCountersAsync`) still computes counts live from the stores. The stored counters are written and available for a future optimization that prefers the stored value when present (falling back to the live computation when absent). This follow-up is tracked in PLAN.md's Up Next.

## Files changed

- `src/Iris.Server/Stores/ActorCountRefreshService.cs` (new, 250 lines)
- `src/Iris.Server/ActivityPubServerOptions.cs` (added `ActorCountRefreshInterval`)
- `src/Iris.Server/ActivityPubServerExtensions.cs` (DI registration)
- `src/Iris.Server/AssemblyInfo.cs` (new, `InternalsVisibleTo`)
- `tests/Iris.Server.Tests/Stores/ActorCountRefreshServiceTests.cs` (new, 12 tests)
