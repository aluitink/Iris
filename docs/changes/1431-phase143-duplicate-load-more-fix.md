# 143.1 — Remove duplicate "Load more" button (F-142.17)

**Commit:** `918f835`
**Finding:** [F-142.17](../plans/phase-142-consistency-review.md#f-14217--load-more-button-text-varies) (S2, UI bug)

## Problem

Three pagination components (`PagedCollection`, `ActorListPanel`, `InteractionActorsPanel`) each rendered **two** visible "Load more" controls:

1. A 48px-tall `<div class="paged-collection-sentinel paged-collection-sentinel--clickable">` with a CSS `::after` pseudo-element displaying "Scroll to load more" and `role="button" aria-label="Load more items"`.
2. A ghost `<button>` labeled "Load more".

Both called the same `LoadMoreAsync` handler. The sentinel was designed for a scroll-driven infinite-scroll listener that was **never implemented** — the `InfiniteScroll` parameter existed but had no `onscroll`/`IntersectionObserver`/JS-interop backing.

## Fix

- **Razor (3 files):** Removed the `--clickable` class from the sentinel div (it's now a 1px-tall invisible click target). Removed the `InfiniteScroll` conditional branching — the ghost "Load more" button is now the single visible affordance, rendered when not loading. The sentinel remains for keyboard/screen-reader access (`role="button"`, `tabindex="0"`).
- **CSS (2 files):** Removed `.paged-collection-sentinel--clickable` and its `::after` rule. Added `cursor: pointer` to the base `.paged-collection-sentinel` so the 1px area is still clickable.
- **API:** Removed the now-unused `InfiniteScroll` `[Parameter]` from `PagedCollection` (no page ever set it to `false`).

## Migration backfill fix

While redeploying to verify, discovered the `AddActivityObjectIri` migration's backfill SQL failed on the live DB (`cannot get array length of a non-array`). Root cause: `jsonb_array_length` in the `WHERE` clause errored on rows where `Document -> 'object'` is a jsonb **object** (not an array) — Postgres evaluates all `WHERE` predicates regardless of short-circuit order.

Fix:
- Removed `jsonb_array_length(...)` from both `WHERE` clauses.
- Added a **third** backfill statement for the common case where `object` is a single object (not an array): `Document -> 'object' ->> 'id'` with `jsonb_typeof(...) = 'object'` guard.
- The two existing statements (array case, `id` and `href`) are unchanged but no longer call `jsonb_array_length`.

## Verification

- `dotnet build` clean (0 warnings).
- `dotnet test` green (1282/1299 pass, only known flake).
- Live Playwright (fresh browser context): actor page Posts tab shows **1** "Load more" button (was 2). WASM bundle confirmed to not contain "Scroll to load more" string.
- Migration applied cleanly on live DB; `ObjectIri` populated for 212/400 activities (remaining 188 are activities without an `object` property, e.g. bare Undo/Follow).
