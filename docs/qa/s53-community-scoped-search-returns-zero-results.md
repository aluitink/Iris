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

## Root cause hypothesis

The community search handler likely fails to resolve the community's IRIs (owner, members, community IRI) and builds a query that matches nothing. The global search (`/ap/v1/search`) works because it searches all objects without a community filter. The community search needs to join the `Objects` table with the community's membership/ownership edges to scope results to that community's posts.

## Fix

The community search endpoint (`GET /ap/v1/c/<handle>/search?q=…`) should:
1. Resolve the community's IRI from the handle.
2. Query `Objects` where `SearchVector @@ plainto_tsquery('simple', q)` AND the object is attributed to the community (i.e., `Document->'attributedTo'` contains the community IRI).
3. Return matching objects in an OrderedCollection.

## Re-verify

1. On a community with posts, use the search box to search for a term that appears in a community post.
2. Verify results are returned (non-zero `totalItems`).
3. Verify results are scoped to the community (no results from other communities or the user's personal posts).
4. Verify the search works for both single-word and multi-word queries.
5. 0 console errors.
