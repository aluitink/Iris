# Phase 142 — Cross-cutting Consistency Review

**Status:** Complete (findings cataloged)
**Deliverable:** This findings doc, consumed by Phase 143 as its worklist.
**Method:** Playwright-driven UI review (live Docker app) + code-level backend audit. No fixes made in this phase.

---

## UI Findings

### F-142.10 — Actor page stats use singular/plural inconsistently
- **Location:** Actor page (`/actor?iri=...`), stats row
- **Expected:** Consistent pluralization: "1 post", "1 follower", "1 following" → "3 posts", "2 followers", "11 following"
- **Actual:** Shows "3 posts", "1 follower", "11 following" — "1 follower" is correct singular, "11 following" is correct plural. However the "following" stat is a count of actors being followed, and the label "following" reads as a verb rather than a noun. Compare with the tab which says "Following (11)".
- **Severity:** S3 (cosmetic)
- **Impact:** Minor readability issue.

### F-142.11 — Community page has no "Follow/Unfollow" button in header
- **Location:** Community page (`/community?iri=...`), header area
- **Expected:** A follow/unfollow button next to the community name, analogous to how actor pages could show a follow button (though actor pages also don't have one — this is a feature gap rather than inconsistency).
- **Actual:** The community header shows: avatar, name, description, "Edit community" button (owner-only), member count. No follow button. Following a community requires going through the "Requests" tab or a separate flow.
- **Severity:** S2 (important — UX gap)
- **Impact:** Users must discover the follow flow indirectly; no one-click follow from the community landing page.

### F-142.12 — Object page heading uses `@handle` format, actor page uses bare handle
- **Location:** Object page (`/object?iri=...`) heading vs actor page (`/actor?iri=...`) heading
- **Expected:** Consistent actor name display across pages.
- **Actual:** Object page heading: `@andrew` (with @ prefix). Actor page heading: `andrew` (no @ prefix). The actor page also shows a subtitle "Andrew Luitink" (display name). The object page does not show the display name in the heading.
- **Severity:** S3 (cosmetic)
- **Impact:** Minor visual inconsistency.

### F-142.13 — Actor page "Posts" tab shows boosted/reposted content mixed with original posts
- **Location:** Actor page → Posts tab
- **Expected:** Clear visual distinction between original posts and reposts/boosts.
- **Actual:** The Posts tab shows a mix: the first item is the user's own post, but subsequent items include posts by other actors (Gargron, nomdeb, aharoni) that appear to be boosts/reposts from the actor's outbox. The "Boosted by" label is absent on these items in the actor page Posts tab (it IS present on the home timeline). The items show "Block/Mute/Report" buttons for the original author, which is correct, but there's no "Boosted by andrew" attribution line.
- **Severity:** S2 (important — confusing UX)
- **Impact:** Users cannot distinguish their own posts from posts they boosted in the Posts tab.

### F-142.14 — Home timeline shows "Boosted by" but actor page Posts tab does not
- **Location:** Home timeline vs actor page Posts tab
- **Expected:** Consistent boost attribution across all list contexts.
- **Actual:** Home timeline: each boosted item shows "Boosted by [actor]" with a "View boosted post →" link for unexpanded boosts. Actor page Posts tab: boosted items show no "Boosted by" attribution; the content is inlined directly.
- **Severity:** S2 (important)
- **Impact:** Inconsistent information presentation; actor page loses the boost provenance.

### F-142.15 — Community page "Feed" tab has a search bar, actor page "Posts" tab does not
- **Location:** Community page Feed tab vs actor page Posts tab
- **Expected:** Either both have search or neither does (or document the intentional difference).
- **Actual:** Community Feed tab has a "Search posts in this community" input + Search button. Actor Posts tab has no search — only a "Refresh" button and a "Load more" button.
- **Severity:** S3 (minor — could be intentional if community feeds are large and actor feeds are small, but undocumented)
- **Impact:** Inconsistent affordances.

### F-142.16 — Object page shows "Edit" and "Delete" buttons for own content, but no equivalent on actor/community pages
- **Location:** Object page (`/object?iri=...`) action bar
- **Expected:** Consistent action availability.
- **Actual:** Object page shows: Reply, Edit, Delete (for own content). Actor page: no edit/delete for the actor profile (though there's a Settings page). Community page: "Edit community" button in header. This is actually consistent — each entity type has its own edit surface. No fix needed.
- **Severity:** Informational
- **Impact:** None.

### F-142.17 — "Load more" button text varies
- **Location:** Actor page Posts tab, community page Feed tab
- **Expected:** Consistent pagination button text.
- **Actual:** Actor page shows two buttons: "Load more items" (with subtext "Scroll to load more") AND a separate "Load more" button. This appears to be a duplicate — both trigger the same action. The community page Feed tab (when it has content) should be checked for the same pattern.
- **Severity:** S2 (important — UI bug, duplicate button)
- **Impact:** Confusing; user may think they need to click both.

### F-142.18 — Empty state messaging differs
- **Location:** Community page Feed tab (empty) vs actor page Posts tab (never empty for active user)
- **Expected:** Consistent empty-state copy and presentation.
- **Actual:** Community Feed empty state: "No posts in this community yet." (plain paragraph). The home timeline empty state and other empty states should be compared. The community also has a "Community Feed" H2 + "Posts from this community's members." subtitle above the empty state, which is more structured than a bare message.
- **Severity:** S3 (minor)
- **Impact:** Minor.

### F-142.19 — Actor page has no "About" or profile summary section
- **Location:** Actor page (`/actor?iri=...`)
- **Expected:** Profile summary/bio visible on the actor page.
- **Actual:** The actor page shows: banner image, avatar, name, display name ("Andrew Luitink"), stats (posts/followers/following), tabs. No summary/bio text is rendered. The AP document likely has a `summary` field that is not displayed.
- **Severity:** S2 (important — missing information)
- **Impact:** Users cannot see an actor's bio on their profile page.

### F-142.20 — Community page shows "0 members" but has a "Members (0)" tab
- **Location:** Community page header + tab list
- **Expected:** Consistent member count display.
- **Actual:** Header shows "0 members" (plain text in a generic div). Tab shows "Members (0)". Both are correct but redundant. The header count could be omitted if the tab already shows it, or the tab could omit the count if the header shows it.
- **Severity:** S3 (minor — redundancy)
- **Impact:** Cosmetic.

---

## Backend Findings

### F-142.1 — `GET /c/{name}` does not honor `?refresh=true`; `/u/{handle}` does
- **Location:** `CommunityDocumentHandler` (~line 8426) vs `ActorDocumentHandler` (~line 1335) in `ActivityPubServerExtensions.cs`
- **Expected:** Both actor and community documents honor the `?refresh=true` cache-bypass contract.
- **Actual:** `ActorDocumentHandler` reads `HasRefreshBypass(context)`, bypasses the `LocalActorDocumentCache`, and emits `no-cache` on bypass. `CommunityDocumentHandler` never consults `?refresh` and unconditionally emits `ActorCacheControl` (`max-age=60, stale-while-revalidate=300`).
- **Severity:** S2 (important)
- **Impact:** `?refresh=true` on a community document silently does nothing. Operators/peers who just toggled a setting see stale data for up to 60s.

### F-142.2 — Community document render is never invalidated on outbox-published settings changes
- **Location:** `FinishCommunityOutboxPublishAsync` (~line 3834) vs `OutboxPublishHandler` Add/Remove branches (~line 3501)
- **Expected:** A write that changes the rendered document invalidates the document cache.
- **Actual:** The actor path calls `actorDocumentCache.Invalidate(actorIri)` on Add/Remove. The community path invalidates only the outbox page — and there is no community document cache to invalidate.
- **Severity:** S2 (important)
- **Impact:** Document/store divergence window; clients reading the community document can act on stale capability flags.

### F-142.3 — `CommunityCollectionEndpointAsync` uses a different `?refresh` predicate than the shared helper
- **Location:** `CommunityCollectionEndpointAsync` (~line 8653) vs `HasRefreshBypass` (~line 5525)
- **Expected:** One shared refresh predicate for all cacheable endpoints.
- **Actual:** `HasRefreshBypass` returns true for any non-empty value except `"false"`. `CommunityCollectionEndpointAsync` uses the stricter `.Equals("true", OrdinalIgnoreCase)` inline check.
- **Severity:** S3 (minor)
- **Impact:** `?refresh=1` bypasses actor document caches but not community collection-page caches.

### F-142.4 — Community outbox `Add`/`Remove` accept any object; person path requires self-reference
- **Location:** `CommunityOutboxPublishHandler` (~line 3774) vs `RecordPersonAddAsync` (~line 3936)
- **Expected:** Both settings-style writes validate that the posted object is the owner's own document.
- **Actual:** The actor path checks `objectIri == actorIri`. The community path calls `RecordCommunityAddAsync` for any Add/Remove whose `actor` is the community — no object-IRI self-reference check.
- **Severity:** S2 (important — potential data corruption)
- **Impact:** A validly-signed community outbox Add carrying an unrelated object can mutate community extension state.

### F-142.5 — Community outbox 500 response has no logging; actor path logs
- **Location:** `CommunityOutboxPublishHandler` (~line 3822) vs `OutboxPublishHandler` (~line 3635)
- **Expected:** Symmetric error handling for the two write surfaces.
- **Actual:** Actor path logs the exception via `ILoggerFactory` before returning 500. Community path returns a bare 500 with no logging.
- **Severity:** S3 (minor — operability)
- **Impact:** Unhandled publish failures on a community are invisible in logs.

### F-142.6 — Community `?q` parameter is parsed into cache key but silently ignored outside feed
- **Location:** `CommunityCollectionEndpointAsync` (~line 8658) vs fetch lambdas
- **Expected:** Query parameters that affect the cache key affect the response.
- **Actual:** Only the feed handler passes `?q` into `feedService.GetFeedAsync`. For `members`/`following`/`followers`/`blocks`/`flags`/`mutes`, a `?q=` value produces a distinct cache entry that renders identical content.
- **Severity:** S3 (minor — cache pollution)
- **Impact:** Unbounded distinct cache entries per query string; clients filtering members via `?q` get unfiltered results with no signal.

### F-142.7 — Cache key omits `limit` on both collection endpoint families
- **Location:** `CollectionEndpointHandler` (~line 8211) and `CommunityCollectionEndpointAsync` (~line 8664)
- **Expected:** `limit` should be part of the cache key since it affects the response.
- **Actual:** Page 1 with `?limit=5` and page 1 with `?limit=50` share the same cache key; whichever renders first wins.
- **Severity:** S3 (minor)
- **Impact:** Clients mixing page sizes on the same collection within the 60s TTL get inconsistent slice sizes.

### F-142.8 — Collection page-1 documents do not advertise `iris:` capability extensions
- **Location:** `CollectionEndpointHandler` (~line 8228), `CommunityCollectionEndpointAsync` (~line 8683)
- **Expected:** All local collections that support `?refresh=true` should declare it in the `iris:capabilities` extension.
- **Actual:** Only `FollowFeedHandler` advertises `iris:refresh/query/type/depth`. All other collections (outbox, members, followers, following) support `?refresh=true` functionally but do not declare it.
- **Severity:** S3 (minor)
- **Impact:** Capability-discovering clients cannot tell these collections support refresh.

---

## Suggested Priority Order for Phase 143

1. **F-142.17** (duplicate "Load more" button) — UI bug, quick fix.
2. **F-142.13 + F-142.14** (boost attribution missing in actor Posts tab) — UX clarity.
3. **F-142.1 + F-142.2** (community document `?refresh` + invalidation) — backend correctness.
4. **F-142.4** (community Add/Remove object-IRI guard) — data integrity.
5. **F-142.19** (actor profile summary not displayed) — missing feature.
6. **F-142.11** (community follow button in header) — UX gap.
7. **F-142.5** (community 500 logging) — operability.
8. **F-142.3 / F-142.6 / F-142.7 / F-142.8** — cache-contract cleanup.
9. **F-142.10 / F-142.12 / F-142.15 / F-142.18 / F-142.20** — cosmetic.
