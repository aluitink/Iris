# S40 — Deleting a community leaves the creator's auto-follow `Follow` edge; the deleted community lingers in the Following tab (with a 404 re-fetch)

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** fixed (dev2 `296e3cdd`, 2026-09-22; QA re-verified Pass 269)
- **Found:** Pass 268 (2026-09-22, `ii-a1`@A, no-cache build carrying `1f941cfb`)
- **Fix:** dev2 (2026-09-22) — `DeleteCommunityAsync` now also removes inbound `Follow`(0) edges (`Target=communityIri`); `CommunityDeleteHandler` (provider-agnostic) removes the inbound `Follow` edge per owner + invalidates the owner's `following` page + feed cache. Regression tests: `CommunityDeletionFollowEdgeTests` (EF) + `CommunityDeleteIntegrationTests.DeleteCommunity_RemovesCreatorAutoFollowEdge_GoneFromFollowing` (in-memory e2e).
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

## Re-verify

Clean entry, logged in as the creator:
1. Create a community via `/communities` → "+ Create a community".
2. Delete it (Delete → Confirm).
3. The community no longer appears in the Communities **Following** tab (no re-fetch 404 for it).
4. The signed `GET /ap/v1/u/{creator}/following` does not list the deleted community.
5. DB: no `Follow` (Kind=0) edge targeting the deleted community IRI.
6. 0 console errors on the delete + reload.

## QA re-verify — PASS 269 (2026-09-22, qa stack rebuilt to `296e3cdd`)

Re-verified live on the rebuilt **qa** stack (`qa-iris-a` fresh build, `/app/Iris.Server.dll` 06:39, login `ii-a1` 200) from a clean entry:

1. **Create:** new community `qa-pass269-s40` (handle) via `/communities` → "+ Create a community". It landed correctly in `Actors` (Type=Group) + `Objects` (Group, not tombstoned), and the **S21 auto-follow edge** `ii-a1 → qa-pass269-s40` (Kind=0) was recorded (1 row). It rendered in the Following tab after a hard reload.
2. **Delete:** Delete → Confirm (HTTP 204).
3. **DB (the crux):** after delete, the `Actors` row is **gone** and — critically — the `Follow`(Kind=0) edge `ii-a1 → qa-pass269-s40` is **gone** (0 rows); **no edge of any kind** targets the community IRI. *Before the fix this edge survived* (the 4 `qa-pass268*` orphans below prove it).
4. **Following tab:** after a fresh reload, `qa-pass269-s40` no longer appears (only `ii-a8-community` + `qa-pass261-test` remain) — no stale render.
5. **Console:** on a clean post-delete reload, **no `qa-pass269-s40` 404**. (One transient 404 appeared on the first post-delete paint — the page fetched the IRI once before the delete landed — but it is gone on the clean reload.) The only remaining 404s are the **pre-fix `qa-pass268{b,c,d,e}` orphans** (created during Pass 268 on the old build, never cleaned — they are the residual evidence of this very bug and are **not** removed by the fix; they will clear once those orphaned edges are swept).

**Result:** S40 **FIXED + re-verified.** The fix removes the inbound `Follow`(0) edge on delete (EF store at the SQL level; the delete handler for the in-memory/file-backed providers) and invalidates the owner's `following`/feed cache. **Note for follow-up:** the 4 pre-fix `qa-pass268*` orphan edges (and their `Objects` rows) remain in the DB as residual test data — a one-off cleanup, not a code defect.
