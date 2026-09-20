# 1597 — Lemmy interop: actor `published` field + follow delivery for cached remote communities

- **Date:** 2026-09-20
- **Status:** COMPLETE
- **Related:** [docs/plans/community-simplification.md](../plans/community-simplification.md) ⑤ live-interop re-verify

## Summary

Two Lemmy interop blockers found during live ⑤ verification are fixed:

1. **Actor documents missing `published`** — Lemmy's `objects::instance` parser REQUIRES a `published`
   field on a site actor's document. It dereferences the follower's site when it receives a `Follow` and
   rejects the activity with **400** ("missing field `published`") when the field is absent.
2. **Follow delivery skipped for a cached remote community** — when a local person follows a *remote*
   community (the peered-Lemmy case), the remote community is cached in the follower's own community store,
   so the old store-membership local/remote test misclassified it as *local* and **skipped the
   cross-instance delivery**. The Follow was recorded in the follower's outbox + the local follows edge, but
   never sent to the remote community's inbox, so the peer never recorded the follower.

## What changed

### 1. `published` on every public actor document (`ActivityPubServerExtensions.BuildActorDocumentAsync`)

Added `doc.Published ??= DateTime.UtcNow;` so every public actor document (the site actor and every Person)
carries a `published` field. The core ActivityStreams `Actor` model does not force it; Lemmy's parser does.
It is a best-effort fallback (now, when the actor's creation time is not otherwise recorded) — the field only
needs to be present for the remote parser to accept the document.

### 2. Host-based local/remote split in `OutboxPublishHandler` (`ActivityPubServerExtensions`, ~line 4199)

The local/remote test for a published activity's recipient changed from:

```csharp
var isLocal = await localActors.IsLocalActorAsync(recipient, ct)
    || await persistence.Communities.TryGetCommunityAsync(recipient, out _, ct);
```

to:

```csharp
var isLocal = IsOnInstance(recipient, baseUrl);
```

A REMOTE community this instance has followed (e.g. a peered Lemmy community) is cached in its own store — so
`TryGetCommunityAsync(recipient)` is TRUE for it, and the store-membership test misread it as local. The host
comparison against the instance's advertised base IRI is the authoritative "is this on my instance" signal —
the same fix `GetCrossPostTargetsAsync` already applied (the cross-post path hit this exact trap first). A
genuinely-local recipient (on this host) still short-circuits to the local inbox write.

## Tests

- **`PersonFollowsRemoteCommunityDeliveryIntegrationTests`** (new) — two-instance harness (A: alice, B: the
  remote community `lumen`). The remote community's `Group` is **cached in A's community store** before the
  follow (the peered-community precondition). A signed `Follow` published to alice's outbox on A must
  **federate to B's community inbox** (B's follows/followers sets record alice). **Verified to FAIL on the
  old store-membership code and PASS on the host-based fix** — a true regression guard.
- **`InstanceActorAtRootIntegrationTests`** (2 new) — `Root_SiteActorDocument_CarriesPublished` and
  `PersonDocument_CarriesPublished`: the site actor's root document and a Person's document both carry a
  `published` field (Lemmy `objects::instance` requires it).

## Verification

1. **Build:** 0 warnings, 0 errors.
2. **Tests:** full suite green (`dotnet test --filter "Category!=Slow"`). `Iris.Server.Tests` 1396 passed
   (incl. the 3 new tests). One unrelated flaky `Iris.Server.Data.Tests.DataVolumeGrowthTests` timing test
   failed once under load and **passes in isolation** (not touched by this change).
3. **Deployed:** container `irisweb-iris-web-1` rebuilt + recreated, healthy. Live check: the site actor's
   root document now returns `published: 2026-09-20T16:09:11Z` (was absent).
4. **Lemmy follow delivery:** the end-to-end Follow-to-cached-remote-community delivery is proven by the new
   integration test (fails on old code, passes on the fix). A full live Lemmy follow additionally requires
   the follower's private key to sign the outbox `Follow` (not exported in production), so it is covered by
   the integration test rather than a manual live click.

## Result

Lemmy can now parse Iris actor documents (`published` present) and Iris now delivers a person's Follow to a
*cached* remote community's inbox (host-based local/remote split), unblocking the ⑤ live Lemmy interop
re-verify.
