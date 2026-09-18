# 89.1 — Community Peering (a community follows other actors)

**Phase:** 89 — Community Peering
**Date:** 2026-09-12
**Status:** Complete
**Commit:** `04dcaac`

## Objective

Answer the 89 investigation question — *"what is the proper way for a community to follow a community (or actor) as to build a replica?"* — and implement it. In ActivityPub a **community is a `Group` actor**; a `Group` can hold a `following` set exactly like a person, and a `Group` can emit signed `Follow` / `Undo(Follow)` activities just like a person. The "replica" behavior (Lemmy-style: the community's page shows the content of the communities/users it follows) is achieved by **merging the followed actors' outboxes into the community feed**, with no community-tagging requirement (unlike members, whose contributions must be tagged with the community).

**Decision:** the standard AP way is the right one — a community follows an actor by the community authoring a signed `Follow` (recorded in its `following` collection + outbox, delivered to the target), and the community feed surfaces the followed actors' content. No special "replica" protocol is needed; it falls out of the existing follow + feed machinery.

## What was built

### Server — feed merge (core)

- **`src/Iris.Server/Services/CommunityFeedService.cs`** — `GetFeedAsync` now merges **two** contributor sets, deduped by activity IRI:
  1. **Members** (edge `CommunityMember`) — their outboxes, `requireCommunityTagged: true` (existing behavior).
  2. **Follows / peers** (edge `CommunityFollow`) — their outboxes, `requireCommunityTagged: false` (new). A followed actor's content is attributed to *that actor*, so it must not be required to carry the community tag.
  - Both run through a shared `MergeContributorOutboxAsync`. A member the community also follows contributes **once** (dedup by activity IRI).
  - **Removed the early-return** for "0 members" — a community with no members but with peers must still show the peers' content.
  - `ReadOutboxAsync` checks the **community store** before the local-actor resolver, so a local *community* actor's outbox is read from the local activity store (the resolver is person-only by contract and would mis-route a `Group`).

### Server — community follow/unfollow endpoint

- **`src/Iris.Server/ActivityPubServerExtensions.cs`** — `POST /local/v1/c/{name}/follow/{**targetIri}` (route name `community-follow-endpoint`), handled by `CommunityFollowHandler`. The community is a `Group` and holds **no client key**, so it cannot sign its own outbox from the browser; the server authors + records + delivers the activity on the community's behalf, gated to the owner:
  - **Auth + gate:** the caller authenticates (cookie/Basic) and must be the community's **creator** (`VerifyCommunityCreatorAsync`); otherwise 403.
  - **Degraded mode:** 503 if federation delivery is unavailable.
  - **Self-follow:** 400 if `targetIri == communityIri`.
  - **Follow:** records the `CommunityFollow` edge, appends a signed `Follow` to the community's outbox, schedules delivery, and (for a local target) records the reciprocal `CommunityFollower` edge.
  - **`?unfollow=true`:** removes the edge, appends a signed `Undo(Follow)`, schedules delivery.
- **`DefaultLocalActorResolver` was intentionally left person-only.** An initial attempt to add a community-store check here broke the inbox/outbox handlers (they check `IsLocalActorAsync` *before* the community-store branch, so a local community would hit the person path). Community outbox routing is handled entirely inside `CommunityFeedService.ReadOutboxAsync`.

### Client

- **`src/Iris.Client/ILocalModerationClient.cs` / `LocalModerationClient.cs`** — added `FollowAsCommunityAsync(Iri communityId, Iri targetId, ...)` and `UnfollowAsCommunityAsync(...)` (2 overloads each, with optional `ProxyCredentials`). A private `LocalCommunityFollowAsync` maps the AP IRI to the local tree (`/local/v1/c/{name}/follow/{target}`, `?unfollow=true` to remove) via the existing `BuildCommunityLocalRequest`.

### UI

- **`apps/Iris.Web.Client/Components/Pages/CommunityDetail.razor`** — a **Peers** tab (creator-only, like Owners/Requests):
  - IRI input + **"Follow as this community"** button.
  - List of followed actors with a per-row **Unfollow** button.
  - **Refresh** button (forces a `BypassCache: true` re-read of the `following` collection).
  - `LoadPeersAsync` reads `communityIri.FollowingOf()`; the tab is wired into `SwitchTab` so it loads on first switch.

## Live verification (Playwright, per the WASM manual-test policy)

Rebuilt the Docker app (`docker compose build --no-cache iris-web` + `up -d --force-recreate iris-web`) and restarted the Playwright MCP (`bash scripts/start-playwright.sh`, caching disabled) for a truly fresh browser. Logged in as `bob` (creator of `test-community-541`, which already follows `andrew`).

| Scenario | Result |
|---|---|
| Peers tab renders + loads on switch | ✅ shows `andrew` (the pre-existing follow), tab reads "Peers (1)" |
| Follow `verifier87` as the community | ✅ edge `CommunityFollow` recorded (DB); after Refresh the Peers tab lists **both** `andrew` and `verifier87` ("Peers (2)") |
| Peered content surfaces in the feed | ✅ `verifier87`'s post (`test-882`) appears in `/ap/v1/c/test-community-541/feed`. `verifier87` is a **follow**, not a member (only `andrew` is a member) → the appearance is purely peering |
| Unfollow `verifier87` | ✅ edge removed (DB); the post drops out of the feed; after Refresh the Peers tab is back to "Peers (1)" with only `andrew` |
| Console errors | **0 peering-related** (the only logged errors are pre-existing 401/403s from remote federation proxy fetches to `mastodon.social` / `ursal.zone` that reject this instance) |

State was restored (community follows only `andrew`) after verification.

## Build / test

- `dotnet build -c Release` → 0 warnings, 0 errors.
- `dotnet test -c Release --no-build --filter "Category!=Slow"` → **1842 passed / 1 skip / 0 failed** (all 12 assemblies).
- **13 new coded tests** (server + client behavior — allowed under the policy; the UI is verified manually above):
  - `tests/Iris.Server.Tests/Services/CommunityFeedPeeringTests.cs` — **6** feed-merge tests (peer content merged, member content still required to be tagged, dedup of a member-who-is-also-a-peer, 0-member-with-peers no longer empty, unfollow drops content, local community outbox routing).
  - `tests/Iris.Server.Tests/CommunityPeeringFollowEndpointTests.cs` — **7** endpoint tests (follow, unfollow, self-follow 400, unauthenticated 401, non-creator 403, unfollow-not-followed 404, follow appears in `following`).
  - `tests/Iris.Client.Tests/LocalModerationClientTests.cs` — **4** new client tests (follow route, person target, unfollow query, non-success propagates).
- **0 new coded web tests** (WASM manual-test policy — Phase 45+).

## Notes / decisions

- **Why server-side authoring rather than a client key for the Group?** A `Group` actor has no client-held key (it is not a sign-in identity), so it cannot sign its own outbox from the browser. Authoring the `Follow`/`Undo` server-side, gated to the owner, is the correct and secure path — it reuses the same signing/delivery machinery the server already uses for instance-actor activities.
- **Why `requireCommunityTagged: false` for peers but `true` for members?** Member contributions are filtered to "posts tagged with this community" so a member's general feed doesn't leak into the community. A *followed* actor is followed precisely to pull *all* of their content in, so requiring the tag would defeat the purpose.
- **Why the early-return removal matters:** the original "0 members → return []" short-circuit would have made a brand-new (no-member) community with peers always show an empty feed. Removing it lets the follows branch run unconditionally.
- **The community `following` collection is the source of truth** for the Peers tab (read via `GetCollectionItemsAsync` with `BypassCache: true`), so it stays consistent with what the federation actually sees.
