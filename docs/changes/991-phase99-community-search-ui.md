# Phase 99 — Community search UI

**Status:** COMPLETE

## Summary

Added an in-page **search box to the Community detail page** (Feed tab), wired to the server's existing `GET /ap/v1/c/{name}/search?q=...` endpoint. Before this phase the community search API existed and was unit/integration-tested on the server, but there was no UI to invoke it from the community page. This phase makes it usable end-to-end, and — critically — fixes a server-side pagination bug that would have made the search results **lose their filter after the first page**.

## The server fix (the important part)

The shared `BuildSearchPageDocument` in `src/Iris.Server/ActivityPubServerExtensions.cs` builds the ActivityPub `OrderedCollectionPage` for **both** the global search and the community search. It previously emitted the paged `next` / `prev` / `first` / `partOf` / page-`id` links as bare collection IRIs with only `offset` / `limit` query params — **the `q` was dropped**. So when the client walked the `next` link to page 2, the server received a request with no `q`, treated it as "the whole feed", and returned unfiltered items. The search would silently degrade to the full feed after page 1.

The fix threads the query through every generated link:

- `trimmedQuery` (the `q` value, trimmed) is captured; `queryPart` is `?q={Uri.EscapeDataString(trimmedQuery)}` when a query is present, `""` otherwise.
- A new `static string PageLink(string baseIri, string queryPart, int offset, int limit)` helper centralizes link construction. When `queryPart` is empty it preserves the **exact legacy format** (`{baseIri}/?offset=..&limit=..`) so the no-query paging assertions (global search paging, the community no-query feed paging) are byte-for-byte unchanged. When `queryPart` is present the links become `{baseIri}?q={escaped}` (page 1 `first` / `partOf` / `id`) and `{baseIri}?q={escaped}&offset=..&limit=..` (page 2+ `prev` / `next` / `id`).
- The `iris:searchQuery` extension continues to carry the **un-escaped** query.

Because `BuildSearchPageDocument` is shared, the global search (`/ap/v1/search`) gets the same fix for free — its paged links now also carry `?q=`.

## The client (WASM) change

`apps/Iris.Web.Client/Components/Pages/CommunityDetail.razor` (Feed tab):

- Renders a `.community-feed-search` form (`<input id="community-feed-search" @bind="FeedQueryInput" @bind:event="oninput">`, a **Search** button that is `disabled` while the input is empty, and a **Clear** button that appears once a search is active). `@bind:event="oninput"` (not the default change-on-blur) is used so the Search button enables live as the user types.
- A `PagedCollection` now drives the feed. Its `CollectionIri` is computed: when a search is active it is `{communityIri.AppendSegment("search")}?q={Uri.EscapeDataString(ActiveFeedQuery)}`, otherwise the plain `{communityIri.FeedOf()}`. Changing that `CollectionIri` makes `PagedCollection` re-fire a fresh load, so submitting a search, clearing it, or switching communities all reload the correct collection.
- The title / description / empty-message are all conditional on the active query: e.g. heading "Posts matching "phone"" with caption "Showing posts matching "phone"." and empty state "No posts in this community match your search." — versus the normal "Community Feed" / "No posts in this community yet." when no search is active.
- `SubmitFeedSearch` trims the input and, if non-empty, sets `ActiveFeedQuery`; `ClearFeedSearch` resets it to null (restoring the normal feed). Both rely on the `CollectionIri` change to trigger the reload.
- On community change (`OnParametersSetAsync`), the search state is reset (`ActiveFeedQuery = null`, input cleared) so a stale query from a previous community doesn't leak into the new one.

CSS for `.community-feed-search` / `-form` / `-caption` was added to both `apps/Iris.Web.Client/wwwroot/css/app.css` and `apps/Iris.Web/wwwroot/css/app.css` (kept in sync; verified with `diff`).

## New coded tests (server-side, per the WASM manual-test policy)

`tests/Iris.Server.Tests/CommunitySearchIntegrationTests.cs`:

- `Search_Page1_WithQuery_NextAndFirstCarryTheQuery` — page 1 of a multi-page query: `next` and `first` both embed `?q=…`; `partOf` = the search IRI; `iris:searchQuery` = the un-escaped query.
- `Search_Page2_WithQuery_PrevAndIdCarryTheQuery` — page 2: `prev` and `id` carry `?q=…` **and** `offset` / `limit`; `next` carries `?q=…` with the advanced offset. This is the regression test for the dropped-`q` bug.
- `Search_WithQuery_PagedNextWalkStillFilters` — walks `next` verbatim and asserts the page-2 items are **still filtered** (all match) and `totalItems` is stable — the end-to-end proof the fix works.
- `Search_WithSpacesInQuery_QueryIsPercentEscapedInLinks_AndUnescapedInExtension` — a multi-word query (`"garden post"`): the links contain the percent-escaped `?q=garden%20post`, a single matching item is returned, and the `iris:searchQuery` extension carries the **un-escaped** `"garden post"`.

Two pre-existing assertions were updated to the new (correct) query-carrying `id` / `first` format: `Search_MatchesContent_CaseInsensitive` and `GlobalSearchIntegrationTests.Search_MatchesActorsAndContent_CaseInsensitive`.

## Verification (Playwright, live Docker app at :8088, signed in as `andrew`)

Against the live community `test-community-541` (111 feed items):

- **Render:** the search box appears in the Feed tab; the Search button is disabled while empty and enables live as you type (`oninput` binding).
- **Single-word query `phone`:** 21 matches; every loaded item contains "phone".
- **Infinite scroll preserves the filter:** page 1 request was `POST …/search?q=phone`; after scrolling to the sentinel, page 2 was `POST …/search?q=phone&offset=20&limit=20` — **the `next` link carried `?q=phone`**, so the filter was preserved across pages (item count 20 → 21, all still matching; the sentinel then disappeared at the end of the collection). Without the server fix, page 2 would have dropped the filter.
- **Multi-word query `foldable phone`:** the request was `POST …/search?q=foldable%20phone`; exactly 4 matches, all containing "foldable phone" — URL-encoding round-trips correctly.
- **No-match query `zzznomatch123`:** renders the search-specific empty state "No posts in this community match your search."
- **Clear:** restores the normal "Community Feed" (unfiltered), clears the input, removes the Clear button and caption.
- **Console:** only the expected federation proxy 404/403 errors (remote instances `mementomori.social`, `ursal.zone`) — **0 new error classes** from the search UI.

## Test counts

Full fast suite after the change: **1864 passed / 0 failed / 1 skipped** across all projects (the `Iris.Server.Tests` search subset — 36 tests — passes consistently across 3 repeated runs). One unrelated, timing-sensitive federation test was flaky on a single full-suite run but passed on immediate re-run; it is not part of this change.
