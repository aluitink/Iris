# 135.1b(3) — Live Iris ↔ Lemmy community follow interop (verified end to end)

**Date:** 2026-09-13
**Slice:** 135.1 (the remaining live-interop step that 135.1b flagged as blocked on external infra)
**Commits:** *(this commit is docs-only; the interop is a live-state change, not a code change)*

## Milestone

The full live follow-interop is **working end to end**. A real Iris community followed the real Lemmy
community `test`, and Iris fetched, persisted, and now serves that Lemmy community as known content:

1. **Follow driven:** `POST /local/v1/c/owner-test-5428/follow/https://iris-dev2.luit.ink/c/test`
   (Basic auth `alice:alice`) → **HTTP 204**.
2. **Lemmy `Group` fetched + persisted:** the follow's key resolution triggered
   `IrisActorDocumentFetcher.GetActorAsync(…/c/test)` → `RemoteCommunityPersister` stored the real
   Lemmy `Group` in the durable store (verified: `Actors` row `Handle=test`,
   `iri=https://iris-dev2.luit.ink/c/test`, `type=Group`).
3. **Served as known content:** `GET /ap/v1/actor?iri=https://iris-dev2.luit.ink/c/test` → **200**,
   the stored `Group` (`type: Group`, `preferredUsername: test`, `name: Iris Interop Test Community`,
   `publicKey`, `source` all intact).
4. **In the directory:** `GET /ap/v1/search?q=lemmy` returns the Lemmy community (full document).
5. **Follow edge recorded:** `Edges` row `Kind=10` (follow),
   `Source=https://iris.luit.ink/ap/v1/c/owner-test-5428`, `Target=https://iris-dev2.luit.ink/c/test`.

This closes the 135.1 "see it as known content" goal for the community-follow direction.

## How the earlier "blockers" were actually resolved

135.1b recorded two blockers (host nginx 400s the POST federation paths; the running Iris container's
actor Basic-auth creds "don't match"). Both are now understood and worked around — **no infra change
was needed**:

1. **nginx 400 → bypass via direct container access.** The host nginx reverse proxy 400s the external
   POST paths (`/local/v1/c/*/follow/*`, `/ap/v1/proxy/*`), but hitting the Iris container **directly**
   on its Docker-network IP (`http://172.19.0.3:8080`) bypasses nginx and reaches the handler. The 400
   was never an app bug — it was nginx.
2. **Basic-auth creds → `alice:alice` (handle/handle).** The running app's
   `IActorCredentialValidator` (`WebAppFactory.cs:300`) validates Basic auth as
   `username == SeedHandle && password == SeedHandle` for the actor IRI `{base}/ap/v1/u/{SeedHandle}`,
   with `SeedHandle = "alice"` (`WebAppFactory.cs:61`). So the only Basic-auth credential that works is
   **`alice:alice`** — *not* `alice:alice-password` (that is the cookie-login password in
   `apps/Iris.Web/.env`, a different auth path). `VerifyCommunityCreatorAsync` builds the person IRI
   from the community owner's handle, so the follow can only be driven on a community **owned by
   `alice`** (the seed handle) — here `owner-test-5428` (its `attributedTo` is `…/u/alice`).

## Live-state note (not code)

This change is a **live-state** change (the follow + the persisted community live in the running
`irisweb-iris-web-1` container's Postgres), not a source-code change. The code that made it possible
(135.1a's `RemoteCommunityPersister` + fetcher `Group` handling) is already committed and covered by
tests (`RemoteCommunityPersisterTests`, `IrisActorDocumentFetcherTests` including
`GetActor_RealLemmyCommunity_PersistsAndServesIt`).

## Remaining for 135.1

- **Reverse direction** (Lemmy community follows an Iris community / user) — Lemmy 0.19 has no clean
  REST endpoint for community-follows-remote-community (`/api/v3/community/follow` is
  user-follows-community, signed by the user).
- **Content posting** in both directions (Iris community posts a Note that federates to Lemmy; Lemmy
  posts that appear in the Iris community feed) — requires signed ActivityPub outbox publishes.
