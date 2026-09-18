# 993 — DefaultLocalActorResolver: distinguish local from remote actors

**Date:** 2026-09-18
**Commit:** `22a09ae`
**Branch:** `interop-testing`

## Problem

During Mastodon interop testing, an Iris user followed a Mastodon actor
(`mstest@mastodon.luit.ink`). The follow edge was recorded locally (Edges
table, Kind=0) and the UI updated, but the outbound Follow activity was
never delivered to Mastodon's inbox. The Mastodon followers collection
showed `totalItems: 0`.

The delivery metrics (`/local/v1/metrics`) confirmed
`iris_delivery_enqueued_total 0` — no delivery job was ever enqueued.

## Root cause

`DefaultLocalActorResolver.IsLocalActorAsync` checked only store
membership:

```csharp
public Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default)
    => _persistence.Actors.TryGetActorAsync(actorIri, out _, ct);
```

But the `Actors` table holds **both** local actors (provisioned by
registration) and remote actors (persisted by `RemoteActorPersister` for
the directory's "All known" scope). When Iris discovered the Mastodon
actor via fediverse search, `RemoteActorPersister` cached it in the
`Actors` table. The outbox handler's `isLocal` check then returned
`true`, so the follow was written to the local inbox instead of being
delivered to the remote shared inbox.

## Fix

`DefaultLocalActorResolver` now accepts an optional `Iri? instanceBase`.
When set, `IsLocalActorAsync` first checks that the actor IRI starts with
the instance base prefix. Only actors hosted on this instance pass the
prefix check and are then confirmed by the store lookup. When no base is
configured (tests), the legacy store-membership-only behaviour is
preserved.

The DI registration in `ActivityPubServerExtensions` was updated to pass
`ActivityPubServerOptions.BaseUri` as the instance base.

## Files changed

- `src/Iris.Server/Caching/DefaultLocalActorResolver.cs` — added
  `instanceBase` parameter + prefix check
- `src/Iris.Server/ActivityPubServerExtensions.cs` — DI registration
  passes `BaseUri`

## Verification

- `dotnet build` — 0 warnings, 0 errors
- `dotnet test` — 1330 passed, 0 failed, 25 skipped
- All existing `DefaultLocalActorResolver` test usages are
  backwards-compatible (the `instanceBase` parameter defaults to `null`)

## Remaining interop blocker

The Iris Docker container cannot reach `mastodon.luit.ink` via HTTPS.
The Mastodon proxy (`mastodon-proxy-1`) serves HTTP only on port 8082;
the external TLS-terminating nginx is on the host and unreachable from
inside the container's Docker network. The fix is correct but cannot be
verified end-to-end until the container→Mastodon HTTPS path is resolved
(e.g. add HTTPS to the proxy, or reconfigure the shared network).
