# 135.1 — Persist remote community (Group) documents for Lemmy / community interop

**Date:** 2026-09-13
**Slice:** 135.1 (PLAN "Up Next" — deploy a local Lemmy container and interoperate with it: the first vertical slice is making the instance persist remote community `Group` documents so the communities it has interacted with are served as known content)
**Commits:** `7cec341` — `Persist remote community (Group) documents for Lemmy interop (135.1)`

## Problem

Iris's federation already caches **remote actor** documents (Phase 117.3's
`RemoteActorPersister` persists a remote `Actor`'s document on first encounter, triggered by every
signed inbound activity), and Phase 134.1 surfaces them (the `GET /ap/v1/actor?iri=...` cached-actor
endpoint + the directory's "All known" scope). But it had **no equivalent for communities**: when a
remote instance's **community** (`Group` in ActivityStreams) signed an activity, or when Iris
interacted with a remote community, the community's `Group` document was **not persisted** anywhere.
A remote Lemmy community — the central unit of Lemmy — was invisible to the instance after
interaction.

Two root causes:

1. `IActorDocumentFetcher`'s fetch path called `IActivityPubClient.GetActorAsync`, which returns
   `Actor?` and does `GetObjectAsync(...) as Actor`. In ActivityStreams a community's document is a
   **`Group`** — and although a `Group` *is* an `Actor`, the cast-to-`Actor` on a `Group` document
   fetched via the client's actor path did not surface it as a persistable community, and there was
   no community store write at all.
2. There was no persister that writes a `Group` to the durable `ICommunityStore`.

## Change

### New `RemoteCommunityPersister` (Iris.Server.Security)

`src/Iris.Server/Security/RemoteCommunityPersister.cs` — mirrors `RemoteActorPersister`
(117.3). `PersistIfNewAsync(Group? community, CancellationToken ct)`:

- Returns `false` for a null document, or a document with no IRI.
- Resolves the instance base from `IrisActivityPubOptions.BaseUri` (normalized); a community whose
  IRI host+path matches the instance base (a **local** community) is skipped (`false`).
- For a **remote** community, if it is not already stored in `ICommunityStore`
  (`TryGetCommunityAsync`), it is written via `PutCommunityAsync` and `true` is returned. An
  already-stored community is left untouched (idempotent), returning `false`.
- Best-effort: any store failure is logged and swallowed (`false`), mirroring
  `RemoteActorPersister` — persistence is a side effect and must never break the request that
  triggered it.

### `IrisActorDocumentFetcher` fetches the full object and persists communities

`src/Iris.Server/Security/IrisActorDocumentFetcher.cs`:

- Constructor gains an optional `RemoteCommunityPersister? communityPersister = null` (4th param).
- `FetchDocumentAsync` now calls `IActivityPubClient.GetObjectAsync(iri, ct)` (the full
  ActivityStreams object) **instead of** `GetActorAsync`, so a community `Group` document is not
  dropped by the actor cast.
- `GetActorAsync` branches on the fetched object, checking **`Group` first** (a `Group` *is* an
  `Actor` in the ActivityStreams model, so the more specific check must precede the general one):
  - **`Group`** → persisted via `RemoteCommunityPersister` (remote only), then **returned** (a
    `Group` IS an `Actor`) so the inbound key resolver can still read its `publicKey` and validate
    community-signed activities. (Returning `null` here would break signature validation for
    community-signed activities — the first implementation returned null and broke 31 community
    federation tests with `Unauthorized`; returning the `Group` as an `Actor` is the correct
    behavior.)
  - **`Actor` (non-Group)** → persisted via `RemoteActorPersister` (remote only) and returned, as
    before.
  - **other/null** → `null`.

### `/ap/v1/actor?iri=...` also serves a cached remote community

`src/Iris.Server/ActivityPubServerExtensions.cs` — `ActorByIriHandler` now, on a miss in
`persistence.Actors`, falls back to `persistence.Communities.TryGetCommunityAsync`. On a community
hit it serves the stored `Group` **as-is** (deep-copied via `ActivityJson.Deserialize<IObjectOrLink>`
→ `as IObject`), with the same `ActorCacheControl` header. An IRI in neither store still 404s. This
lets the Phase 134.1 cached-actor endpoint (and the directory's "All known" scope) also render
remote communities the instance has interacted with.

### DI wiring

`ActivityPubServerExtensions.cs` — a `RemoteCommunityPersister(persistence.Communities,
options.BaseUri, logger)` is constructed (when the persister is enabled) and passed as the 4th arg
to the `IrisActorDocumentFetcher`.

## Verification

**Server tests** (`tests/Iris.Server.Tests/RemoteCommunityPersisterTests.cs`, 6 tests, its own xunit
collection + shared `TestServer` fixture): a remote community is persisted to the community store
(returns `true`); a local community (IRI prefix = instance base) is **not** persisted; persisting
twice is idempotent (returns `false`, store unchanged); a null / no-IRI community is skipped
(`false`); the `/ap/v1/actor` endpoint serves a cached remote **community** `Group` (200, the stored
document); the endpoint 404s an unknown community.

`tests/Iris.Server.Tests/Security/IrisActorDocumentFetcherTests.cs`: the stub client's
`GetObjectAsync` now returns the actor/`Documents` (the fetcher fetches the full object, 135.1) and
counts `GetObjectCalls`; assertions updated `GetActorCalls`→`GetObjectCalls`. New test
`GetActor_RemoteCommunity_PersistsItAndReturnsItForKeyResolution`: a fetched `Group` is persisted to
the community store **and** returned (as an `Actor`) for key resolution.

**Build + suite:** `dotnet build Iris.slnx` clean (0 warnings); `dotnet test Iris.slnx` green —
`Iris.Server.Tests` 1141 passed / 0 failed (the new 6 + 1 tests pass; the previously-passing 31
community federation tests remain green, confirming the `Group`-returned-as-`Actor` behavior keeps
community signature validation working). Stable across repeated runs.
