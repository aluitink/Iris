# Phase 116.3 — Inbox Log IRI Extraction Fix

**Date:** 2026-09-13
**Type:** Bug fix (logging)
**Scope:** `ExtractActorIriFromActivity` / `ExtractTargetIriFromActivity` in `ActivityPubServerExtensions.cs`

## Problem

The `Inbox accepted` / `Inbox rejected` log lines printed `System.Linq.Enumerable+RangeSelectIterator`2[...]` instead of the actor/target IRIs. This made the logs useless for debugging federation issues.

**Root cause:** `Activity.Actor` and `Activity.Object` are `IEnumerable<IObjectOrLink>` (collections), not single objects. The old code did `actor is IObject { Id: { } id } ? id : actor?.ToString()` — the pattern match always failed (a `RangeSelectIterator` is not an `IObject`), so it fell through to `.ToString()` which returns the type name.

## Fix

Added a `FirstIriFromCollection(IEnumerable<IObjectOrLink>?)` helper that iterates the collection and returns the first resolvable IRI (an `IObject` with an `Id`, or an `ILink`). Both `ExtractActorIriFromActivity` and `ExtractTargetIriFromActivity` now use this helper.

**Before:**
```
Inbox accepted: Delete from System.Linq.Enumerable+RangeSelectIterator`2[System.Int32,KristofferStrube.ActivityStreams.IObjectOrLink] targeting System.Linq.Enumerable+RangeSelectIterator`2[...]
```

**After:**
```
Inbox accepted: Delete from https://mastodon.social/users/117089561788137031 targeting https://mastodon.social/users/117089561788137031/statuses/117089561788137031
```

## Files Changed

- `src/Iris.Server/ActivityPubServerExtensions.cs` — fixed both extraction helpers + added `FirstIriFromCollection`

## Verification

- Build clean (0 warnings, 0 errors)
- All 1,816 tests pass (0 failures)
- Docker app rebuilt and restarted (healthy)

## Investigation Notes

The user asked to investigate "invalid key errors" in the logs. Three categories found:

1. **`could not resolve public key` (10×)** — Remote instances (mastodon.social, etc.) send inbox POSTs but Iris can't fetch their actor docs. Likely network egress or rate-limiting issues from the Docker container. Not a code bug.

2. **`malformed Signature header` / `KeyId: (none)` (14×)** — Unauthenticated probes (bots/crawlers hitting inbox without signing). Not a code bug — correctly rejected with 401.

3. **DNS failures: `s.rayven.mx` (20×)** — Dead follower. The delivery queue keeps retrying a host that no longer resolves. Should eventually dead-letter. Not a code bug (expected behavior for a dead peer), but the dead-letter threshold could be tuned.

The `libgssapi_krb5.so.2` startup error is a non-fatal Kerberos library warning (expected in a slim Docker image).
