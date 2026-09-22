# S53 — Community-scoped search always returns 0 results

- **Class:** bug / search — **Severity:** S2
- **Status:** open
- **Found:** Pass 325 (2026-09-22)
- **Related:** S48 (global search hyphenated terms), S49 (community feed empty)

## Symptom

On a community page, the "Search posts in this community" search box returns **0 results** for any query, even when matching posts exist in the community.

**Reproduction:**
1. Navigate to `/community?iri=https://qa-iris-b.luit.ink/ap/v1/c/qa-pass-319-feed`.
2. Type "QA Pass" in the search box, click Search.
3. UI shows: "No posts in this community match your search."
4. `GET /ap/v1/c/qa-pass-319-feed/search?q=QA+Pass` → `totalItems: 0`.
5. `GET /ap/v1/c/qa-pass-319-feed/search?q=QA` → `totalItems: 0`.
6. `GET /ap/v1/c/qa-pass-319-feed/search?q=testing` → `totalItems: 0`.

**Control (global search works):**
- `GET /ap/v1/search?q=QA+Pass` → `totalItems: 23` (includes the 3 notes from this community).

**DB evidence:**
- 3 notes in `Objects` have `qa-pass-319-feed` in their `Document` (attributedTo).
- Note `06GCNP4CNZ6X130KW4F8D7EQ1G` has `SearchVector` = `'319':3A 'appear':10A 'community':13A 'feed':5A,14A 'in':11A 'note':8A 'pass':2A 'qa':1A 's49':4A 'should':9A 'test':6A 'the':12A 'this':7A` — contains `'qa'` and `'pass'`.

**The community-scoped search endpoint is completely non-functional.**

## Root cause (CONFIRMED — Pass 330, build `0d7307e5`)

The community search is **not** a DB tsquery join. It is `CommunitySearchHandler` (`ActivityPubServerExtensions.cs:11833`) → `CommunityFeedService.SearchCommunityAsync` (`CommunityFeedService.cs:401`), which:
1. Calls `GetFeedAsync(communityIri, …)` — the community's **feed** (the union of member + followed-actor outboxes, filtered to community-tagged content, 40.3).
2. Runs an **in-memory** case-insensitive substring match (`ContainsInStrings`) over the feed items' `content`/`name`.

So community search returns results **only if the community's feed is non-empty**, and the feed is non-empty **only if** (a) the community is in the **local** community store AND (b) it has ≥1 member whose outbox carries community-tagged content. Two distinct failure modes were confirmed:

**Mode 1 — 404 (cross-instance, A reading a B community):** `GET A /ap/v1/c/qa-pass-319-feed/search` → **404**, because `TryGetCommunityAsync` (`ActivityPubServerExtensions.cs:11846`) fails — `qa-pass-319-feed` is a **B-hosted** community and is **not** in A's local community store (A's `Actors` has no `qa-pass-319-feed` row; A's `Objects` only has the cached Group doc + the 3 federated notes, no community-store row). The handler 404s before any search runs. **The community search endpoint cannot serve a community hosted on another instance** (no remote-community fallback / proxy, unlike the actor-document and collection-proxy seams).

**Mode 2 — empty feed (own instance, B):** `GET B /ap/v1/c/qa-pass-319-feed/search?q=QA` → 200 but `totalItems: 0`. On B the community **is** in the store, but its feed is empty because the 3 notes are `attributedTo` **ii-b1 (the person)**, not the community (`attributedTo = …/u/ii-b1`), AND the community has **zero members** (no `CommunityFollower` `Kind=10` edge — only the creator's `Follow`(0) edge `ii-b1 → community`). The S49 fix added the **creator to the followers collection** on community *creation*; this community predates that or the member edge is missing, so the feed's member branch (40.3 community-tagged filter) and follow branch both contribute nothing. Even if it had a member, the notes are person-attributed, not community-tagged, so the 40.3 filter would drop them.

**Why global search works:** `/ap/v1/search` searches **all** `Objects` by `SearchVector` with no community scope, so it finds the 3 notes regardless of community membership/`attributedTo`.

## Fix

1. **Remote community search (Mode 1):** when `TryGetCommunityAsync` fails for a *known remote* community (a cached Group doc exists in the object store), the handler should proxy the search to the community's home instance (mirroring the S24-D4 remote-actor collection proxy, `ProxyRemoteActorCollectionAsync`) instead of 404ing.
2. **Empty-feed search (Mode 2):** the feed-based search is inherently limited to community-tagged, member-contributed content. Either (a) ensure community posts are `attributedTo` the community (so the 40.3 filter admits them) and the creator is a member (S49), or (b) add a DB-backed tsquery path that scopes by `Document->'attributedTo'` / community membership edges rather than relying on the in-memory feed walk.

## Re-verify

1. On a community with posts, use the search box to search for a term that appears in a community post.
2. Verify results are returned (non-zero `totalItems`).
3. Verify results are scoped to the community (no results from other communities or the user's personal posts).
4. Verify the search works for both single-word and multi-word queries.
5. 0 console errors.
