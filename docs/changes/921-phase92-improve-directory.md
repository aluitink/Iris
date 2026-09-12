# 92.1 — Improve Directory (scope toggle, card layout, chevron)

**Phase:** 92 — Improve Directory
**Date:** 2026-09-12
**Status:** Complete
**Commit:** `acbe745`

## Objective

From the 92 investigation, the directory page had several rough edges:

1. **Scope** — "do we cache/track a record for every actor we see? … The Directory could have a toggle for local actors only or all known actors; same with communities."
2. **Communities tab + follow buttons look odd** — "it's long for communities with longer descriptions."
3. **The `+` for recent items looks odd** — "we should brainstorm a better visual for this."

Phase 92 addresses all three: a **This instance / All known** scope toggle (backed by a new `?local` search parameter), a reworked **directory card row** (follow button pulled out of the expand control, chevron instead of `+`/`−`), and CSS so long community descriptions no longer squash the follow button.

## What was built

### Server — `?local` on the global search endpoint

`GET /ap/v1/search` (the directory's data source) now accepts `?local=true`, which restricts the **actor pass** to this instance's own actors (the directory) — a cached remote actor is excluded. The plumbing, all with a `localOnly` defaulting to `false` (so existing callers are unchanged):

- **`IActorStore.SearchActorsAsync` / `CountSearchMatchesAsync`** gained a `bool localOnly = false` parameter.
  - **EF (`EfActorStore`)** — the local filter is `Handle IS NOT NULL` (a local actor/community is provisioned with a handle; a remote actor cached here has none). The constant clause is inlined verbatim into the raw SQL (EF1002 — a non-constant string would not be verifiable); the no-query path uses a LINQ `Where(e => e.Handle != null)`.
  - **InMemory / FileBacked** — a `localOnly` guard (`IsLocal`): an actor passes only if it carries a `preferredUsername` (a local handle).
- **`IGlobalSearchService.SearchAsync` / `SearchPagedAsync`** take `localOnly` and forward it to the actor pass (the content pass is unaffected — stored content is always the instance's own).
- **`GlobalSearchHandler`** reads `?local` and passes it through.

A "known actor" in this model is a remote actor the instance has **durable-cached** (e.g. via a federated `Update` that rewrites the actor row, or a remote stand-in). A plain in-memory federation cache (`RemoteActorCache`, 1 h TTL) is not queryable and is out of scope — the toggle is therefore *local directory* vs *every actor this instance stores* (which is exactly "all known actors" for the durable surface). On the live instance the durable actor table held only local actors (0 remote cached), so both scopes returned the same 9 rows — the filter is correct; there is simply no remote-actor delta to show yet.

### Client — scope toggle + reworked card row

- **`Directory.razor`** — a `directory-toolbar` now wraps the People/Communities tabs and a new **`directory-scope`** segmented control ("This instance" / "All known"). Toggling sets `_localOnly` (default `true`) and re-queries with `new SearchOptions { Type = "Actor", LocalOnly = _localOnly }`. The active button carries `aria-pressed`.
- **`SearchOptions`** — a new `LocalOnly` (default `false`) member; `ActivityPubClient.SearchAsync` appends `&local=true` to the search IRI when set.
- **`DirectoryCard.razor`** — the card header became a **`directory-card-row`**:
  - The expand control is now a button (`directory-card-expand-btn`) wrapping just the `ActorCard` — the **`FollowButton` is pulled out** to a sibling, so it no longer sits *inside* the clickable header (a nested interactive control, and the source of the "follow buttons look odd" complaint).
  - The `+`/`−` recent-posts glyph is replaced by a **chevron** (`directory-card-chevron`, an SVG that rotates 180° when expanded) — a clearer "expand/collapse recent posts" affordance than a bare plus sign.

### CSS (`app.css`, mirrored to `apps/Iris.Web`)

- `directory-toolbar` (flex, space-between, wraps) + `directory-scope` segmented buttons (accent fill when active).
- `directory-card-row` (flex, align-center) with the expand button `flex: 1 1 auto; min-width: 0` (so a long community description truncates/wraps in the body instead of pushing the row apart).
- `.directory-card .follow-button { min-width: 0 }` — the follow button hugs the card edge instead of reserving `min-width: 6rem`.
- `directory-card-chevron` + `directory-card-chevron-svg` (transition on `transform`; `is-expanded` rotates 180°).

## Live verification (Playwright, per the WASM manual-test policy)

Rebuilt the Docker app (`docker compose build iris-web` + `up -d --force-recreate iris-web`) and restarted the Playwright MCP (`bash scripts/start-playwright.sh`, caching disabled) for a fresh browser. Signed in as `andrew` and visited `/directory`.

| Scenario | Result |
|---|---|
| Scope toggle renders | ✅ "This instance" / "All known" segmented control present in the toolbar beside the tabs; "This instance" active by default |
| "All known" toggles the query | ✅ clicking "All known" sets `aria-pressed` and re-queries the directory (list reloads) |
| Follow buttons pulled out of the expand button | ✅ each row's Follow/Unfollow button is a row-level sibling of the expand button (not nested in the header) |
| Chevron replaces `+` | ✅ the recent-posts affordance is a chevron; clicking the expand button flips the `aria-label` to "Hide recent posts" and renders the posts; the chevron carries an `is-expanded` rotation class |
| Communities tab with long descriptions | ✅ community cards render handle + name + (long) description in the body; the follow button stays aligned at the row edge (e.g. the long "A Feed-typed community seeded for the Phase 87 live verification…" description does not squash the row) |
| `?local` endpoint wiring | ✅ `curl /ap/v1/search?type=Actor` and `&local=true` both return the durable actor set (9 local actors, 0 remote cached) |
| Console errors | **0** across navigation + toggle + expand (only the pre-existing remote-federation avatar fetch, which is not an error here) |

## Build / test

- `dotnet build -c Release` → 0 warnings, 0 errors.
- `dotnet test -c Release --no-build --filter "Category!=Slow"` → green. **1852 passed / 0 failed.** `Iris.Server.Tests` is now **1087 passed / 1 skip** (1084 prior + **3 new**).
- **3 new coded tests** (`tests/Iris.Server.Tests/Services/GlobalSearchServiceTests.cs`) pin the `localOnly` contract directly (pure server logic, no TestServer boot — the same kind of test Phase 91 added for the notification filter):
  - `Search_LocalOnly_ExcludesCachedRemoteActors` — "all known" (default) returns a local actor *and* a cached remote actor; `localOnly: true` drops the remote one.
  - `Search_LocalOnly_AppliesToQueryMatches` — a query match finds both a local and a remote actor without `localOnly`; with `localOnly` it finds only the local one.
  - `Search_LocalOnly_DoesNotAffectContent` — a `localOnly` search with a no-type filter still returns the note (content is never "remote" in this model).
- The EF/InMemory/FileBacked store signature changes are backward-compatible (`localOnly = false` default), so no existing caller or test changes were required beyond the two interface-fake signatures in the observability tests (kept in sync).

## Notes / decisions

- **Why a server `?local` flag rather than a client-side filter?** The search is computed fresh per request and the actor pass already pushes matching into the store (57.4). Filtering client-side would fetch the whole durable actor surface and then discard remote rows — the server flag is a cheap `WHERE Handle IS NOT NULL` and keeps the directory's `totalItems` accurate for each scope.
- **Why `Handle IS NOT NULL` as the local predicate?** `ActorEntity.Handle` is the local provisioned handle (e.g. `alice`); a remote actor cached here is a stand-in with no local handle, so its `Handle` is null. This is the cleanest durable distinction between "this instance's own actor" and "a remote actor we cached." (A `Group`/community is local too and carries its name as a handle, so communities stay in the directory under "This instance.")
- **Why a chevron instead of `+`/`−`?** A plus/minus reads as "add/remove" (it looked like a follow/subscribe action, which is why it sat oddly next to the follow button). A down-chevron is the conventional "expand/collapse this section" affordance and, rotated 180° when open, clearly signals the card's expanded state.
- **Why pull the follow button out of the expand button?** The follow button was rendered as *child content* inside the `directory-card-header` `<button>`, i.e. an interactive control nested inside another button. Browsers handle this poorly (the nested control's click can be swallowed or both fire), and it is the root of the "follow buttons look odd" complaint. Moving it to a row-level sibling makes each control independent.
- **Why "This instance" is the default scope?** The directory's historical behavior listed this instance's own actors; keeping that as the default is least-surprising, and "All known" is an explicit opt-in to the broader (currently identical, but future-proofed) surface.
