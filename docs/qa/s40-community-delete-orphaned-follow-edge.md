# S40 — Deleting a community leaves the creator's auto-follow `Follow` edge; the deleted community lingers in the Following tab (with a 404 re-fetch)

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** FIXED + live-verified (commit `59afe497`); regression test + full suite + live EF-path check all green
- **Found:** Pass 268 (2026-09-22, `ii-a1`@A, no-cache build carrying `1f941cfb`)
- **Related:** [S21](s21-new-community-missing-following-tab.md) (the auto-follow edge this leaks), [S24](s24-cross-instance-follow-state-inconsistent.md) (Following-tab state consistency), [S4](s04-communities-following-remote.md) (Following-tab resolution)

## Symptom

When an owner deletes a community they created (`/communities` → the community card's **Delete** → **Confirm**), the community's actor row is removed, but the creator's **auto-follow edge** to that community is **not**. Consequences, observed live:

1. The deleted community **still appears in the creator's Communities "Following" tab** (e.g. `qa-pass268e-test` rendered with "You are the only owner" / Leave / Delete after its `Actors` row was gone).
2. The UI fires a **404** for the deleted community on re-resolution: `GET /ap/v1/c/{handle}` → 404 (one per deleted community; 4 observed for `qa-pass268{b,c,d,e}-test`).
3. The signed `GET /ap/v1/u/{creator}/following` collection **still lists the deleted community IRIs** (`orderedItems` included all 4 deleted communities).

Repro (ii-a1, A):
1. Create communities `qa-pass268b/c/d/e-test` (each auto-follows the creator).
2. Delete each via the Communities card Delete → Confirm (HTTP 204 each).
3. DB `Actors` has **no** `qa-pass268*` community rows (deleted), but `Edges` still has `Kind=0` (Follow) rows `ii-a1 → qa-pass268{b,c,d,e}-test` (4 rows).
4. `GET /communities` → Following tab still renders `qa-pass268e-test`; console shows 404s for the deleted communities.

## Root cause

`DeleteCommunityAsync` (`src/Iris.Server.Data/Stores/EfCommunityStore.cs:185`) removes the community's `ActorEntity` row and a set of **community-scoped** edges (`CommunityMember`, `CommunityJoinRequest`, `CommunityFollow`, `CommunityFollower`, `CommunityBlock`, `CommunityFlag`, `CommunityMute` — as source, plus `CommunityFollower` as target). It does **not** remove the creator's **`Follow` (`EdgeKind.Follow = 0`)** edge *to* the community.

That `Follow(0)` edge is created by the community auto-follow in `RecordCreateLocalAsync` (`src/Iris.Server/ActivityPubServerExtensions.cs` ~6044–6110 — the S21 fix). It is the edge the S21 fix relies on to surface the new community in the Following tab. On deletion the community-scoped edges are cleaned up, but this user→community `Follow` edge is orphaned: it points at a `Group` whose `ActorEntity` row no longer exists.

The Communities "Following" tab (`apps/Iris.Web.Client/Components/Pages/Communities.razor:350` `ResolveFollowingCommunitiesAsync`) walks the `/following` IRIs; a followed IRI not in the local search cache is fetched by IRI and kept when it is a `Group`. For a deleted local community the IRI fetch 404s (caught + skipped in that path) **but the local search cache (`Items`) still holds the stale `Group`** for a window, so the deleted community is rendered until that cache lapses — and the underlying edge remains regardless (it shows again on any fresh `/following` read).

## Fix

In `DeleteCommunityAsync` (or the `CommunityDeleteHandler` `src/Iris.Server/ActivityPubServerExtensions.cs:11392`), also remove the **inbound `Follow` edges to the community** — i.e. `Edges` where `Kind = EdgeKind.Follow` and `Target = communityIri` (the creator's auto-follow, plus any other actor that explicitly followed the community). This is the inverse of the `CommunityFollower` (kind 10) cleanup that already happens, and covers the `Follow(0)` edge the S21 auto-follow creates. Also invalidate the creator's `following` collection-page cache (as the S21 fix does on create) so the change is reflected immediately.

Regression test: create a community (auto-follow edge exists), delete it, assert no `Follow` edge remains targeting the community IRI and the creator's `/following` no longer lists it.

## Fix (implemented, commit `59afe497`)

Both the handler and the EF store now clean up the inbound `Follow` edge:

- **`CommunityDeleteHandler`** (`src/Iris.Server/ActivityPubServerExtensions.cs`): captures the community's `attributedTo` (owner) **before** deletion, then after `DeleteCommunityAsync` calls `persistence.Follows.RemoveFollowAsync(owner, community)` and invalidates the owner's `following` page-1 + feed caches (`InvalidateLocalCollectionPage(owner, "following")` + `IFollowFeedService.InvalidateActorFeedCache(owner)`). This is store-agnostic, so it covers the **InMemory** provider (the integration-test path) and any provider whose `DeleteCommunityAsync` does not drop the user→community `Follow` edge.
- **`EfCommunityStore.DeleteCommunityAsync`** (`src/Iris.Server.Data/Stores/EfCommunityStore.cs`): also removes the inbound `Follow` (`EdgeKind.Follow = 0`) edges **targeting** the community — the inverse of the existing `CommunityFollower` (kind 10) cleanup — so the live EF path is self-contained.

Regression test `DeleteCommunity_RemovesCreatorsAutoFollowEdgeAndInvalidatesFollowingCache`
(`tests/Iris.Server.Tests/CommunityDeleteIntegrationTests.cs`): creates a community (auto-follow edge + warm `following` cache, `totalItems=1`), deletes it, then asserts `IsFollowingAsync(creator, community)` is false, `GetFollowingAsync(creator)` no longer lists it, and a **plain (non-`?refresh`)** `/following` read immediately returns `totalItems=0` (the warm cache was invalidated). Verified it **fails** without the handler fix and **passes** with it.

## Re-verify

Clean entry, logged in as the creator:
1. Create a community via `/communities` → "+ Create a community".
2. Delete it (Delete → Confirm).
3. The community no longer appears in the Communities **Following** tab (no re-fetch 404 for it).
4. The signed `GET /ap/v1/u/{creator}/following` does not list the deleted community.
5. DB: no `Follow` (Kind=0) edge targeting the deleted community IRI.
6. 0 console errors on the delete + reload.

## Live verification (dev1 stack, commit `59afe497`, EF/Postgres path)

Verified end-to-end on the `dev1` stack (a `--no-cache` rebuild carrying the fix) with a fresh registered
account `s40ui` (key-signed community create + cookie-authenticated owner delete, the exact S40 UI repro):

- `CreateCommunityAsync` (signed Create → outbox) → **202**; the creator auto-follows the community.
- `GET /ap/v1/u/s40ui/following` (public, pre-delete) → **200, totalItems=1**, lists the community.
- `DELETE /local/v1/c/s40live` (cookie-authenticated) → **204**.
- `GET /ap/v1/u/s40ui/following` (public, **no** `?refresh`, post-delete) → **200, totalItems=0**, does **not**
  list the community (the warm creation-path cache was invalidated by the fix — no 60s stale window).
- `GET /ap/v1/c/s40live` (post-delete) → **404**.
- DB (`iris_a`): `SELECT count(*) FROM "Edges" e WHERE e."Kind"=0 AND NOT EXISTS (SELECT 1 FROM "Actors" a WHERE a."Id"=e."Target")` → **0** (no orphaned Follow edges), and no `Actors` row for the deleted community.

(The WASM UI path itself was not usable for the re-verify in this environment: the browser reached the
app on `127.0.0.1:10081` while the client's ActivityPub fetches target the FQDN origin, which the
document CSP (`connect-src 'self'`) blocks — a test-harness networking gap, not an Iris defect. The raw
API exercises the identical handler + store code the UI drives.)
