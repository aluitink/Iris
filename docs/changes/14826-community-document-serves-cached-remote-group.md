# 14826 — `GET /ap/v1/c/{name}` serves a cached remote Group (S30)

- **Status:** done (unit-verified; live re-verify deferred to QA)
- **Slice:** S30 (cross-instance community join/view blocked)
- **Related:** S29 (community WebFinger 404, fixed `8af708c`), S24 Defect 3 (remote-actor direct GET, fixed `27b1ba6`)

## Problem

A remote community (Group) that has been federated to this instance (e.g. via a `Create` activity
that the peer's `RemoteCommunityPersister` cached) is stored in the durable community store under
its **remote IRI** (e.g. `https://peer.example/ap/v1/c/ii-comm`). However, the `GET /ap/v1/c/{name}`
route builds a **local** IRI (`{base}/ap/v1/c/{name}`) and looks it up in the store — which 404s
because the local IRI never matches the remote IRI. The result: a peer instance that has already
received and cached the remote community cannot view it via its community route.

## Root cause

`CommunityDocumentHandler` (`src/Iris.Server/ActivityPubServerExtensions.cs`, line ~9893) performs
a single lookup by the local community IRI. When that lookup misses, it returns 404 without
checking whether a cached remote community with the same name (last path segment) exists in the
store.

## Fix

Added a remote-community fallback in `CommunityDocumentHandler`, mirroring the S24 Defect 3
pattern in `ActorDocumentHandler`: when the local IRI lookup misses, enumerate all stored
community IRIs, find one whose last path segment matches the requested name and whose IRI is on
a different origin (not this instance), and serve its document AS-IS (the stored document already
carries the remote instance's own inbox/outbox/followers/following IRIs — no Iris-local collection
extensions are added, which are only valid for local communities).

The fallback is a linear scan of `GetAllCommunityIrisAsync()` filtered by:
1. Not the same IRI as the local lookup (avoids self-match).
2. Not on this instance's origin (remote only).
3. Last path segment matches the requested name (case-insensitive).

The first match is served (deterministic by store ordering). If no match, 404 as before.

## Tests

Two new tests in `tests/Iris.Server.Tests/CommunityEndpointIntegrationTests.cs`:

- `CommunityDocument_CachedRemoteCommunity_IsServedByRemoteIri` — seeds a remote Group
  (`https://remote.example/ap/v1/c/ii-comm`) in the community store, asserts `GET /ap/v1/c/ii-comm`
  returns 200 with the remote community's document (type Group, remote IRI, remote inbox/outbox).
- `CommunityDocument_UnknownCommunity_NoRemoteCache_Returns404` — asserts a name that matches
  neither a local nor a cached remote community still 404s.

## Verification

- `dotnet build`: 0 warnings / 0 errors.
- `dotnet test` (Iris.Server.Tests): 1418 pass / 0 fail (was 1416; +2 new tests).
- Live re-verify deferred to QA (requires two-instance federation stack).

## Scope note

This fix addresses the **direct-view** facet of S30 (facet 2 in the QA finding). The other facets
(1: remote-community follow entry point in Directory/Communities; 3: federated community discovery
in "All known") remain open and will be addressed in subsequent slices. S29 (WebFinger `!`-form)
is already fixed (`8af708c`), so the WebFinger resolution prerequisite for remote-community follow
is in place.
