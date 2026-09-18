# Phase 98 — Improve collection scrolling (infinite scroll)

**Status:** COMPLETE

## Summary

Replaced the static "Load more" button in the shared `PagedCollection` component (used by the home timeline, public timeline, notifications, outbox, and actor/profile/community lists) with **infinite scroll**: as the user scrolls near the bottom of the list, the next page of items loads automatically. A ghost "Load more" fallback button remains for accessibility and for environments where the scroll listener is unavailable.

## How it works

The implementation is a **pure client-side (JavaScript) scroll-based infinite scroll**, deliberately chosen over a Blazor `IJSRuntime` / `DotNetObjectReference` / `OnAfterRender` approach. During development it was found that the `PagedCollection` component's `OnAfterRender` / `OnAfterRenderAsync` JS-interop attach path did not reliably fire in the live WASM app (no console output, the callback was never registered), even though the component clearly rendered its items and the standalone JS worked when invoked manually. Rather than chase a Blazor lifecycle quirk, the trigger was moved entirely into the host page's JS, which is guaranteed to run.

- **`apps/Iris.Web.Client/wwwroot/index.html`** — an inline IIFE adds a single passive `scroll` + `resize` listener (plus a one-shot `load` check). On each event it queries all `.paged-collection-sentinel` elements and, for any that are within `200px` of the viewport bottom and not currently busy, sets a `data-busy` attribute and calls `el.click()`. The click dispatches the sentinel's Blazor `@onclick` handler (`LoadMoreAsync`). The `data-busy` flag is cleared (re-armed) on a subsequent scroll once new content has pushed the sentinel back below the trigger line, so successive scrolls keep loading successive pages. A microtask-deferred `checking` guard coalesces a burst of scroll events within one frame into a single pass.
- **`apps/Iris.Web.Client/Components/PagedCollection.razor`** — when `HasMore` is true, the component renders a sentinel `<div class="paged-collection-sentinel paged-collection-sentinel--clickable" @onclick="LoadMoreAsync" role="button" aria-label="Load more items" tabindex="0">` at the bottom of the list. A new `[Parameter] public bool InfiniteScroll { get; set; } = true;` gates the UI: when `true` (default) the sentinel + ghost fallback button render; when `false` the original secondary "Load more" button renders instead. `LoadMoreAsync` already guards re-entry with `if (LoadingMore || !HasMore) return;`, so rapid scroll-triggered clicks collapse to a single in-flight load. All the earlier `IJSRuntime` / `DotNetObjectReference` / `OnAfterRender` / `IAsyncDisposable` / `ElementReference` scaffolding was removed (it was dead and was the source of the Docker `CS0169`/`CS0649` build friction).
- **`apps/Iris.Web.Client/wwwroot/css/app.css`** and **`apps/Iris.Web/wwwroot/css/app.css`** (kept in sync) — `.paged-collection-sentinel--clickable` is a 48px-tall, centered, pointer-cursor strip showing a muted "Scroll to load more" caption via `::after`; `.paged-collection-loadmore-fallback` styles the ghost fallback button (smaller, muted).

## Why not `IntersectionObserver` + Blazor JS interop

The first (abandoned) design used an `IntersectionObserver` on the sentinel wired to a Blazor `DotNetObjectReference` created in `OnAfterRender`. In the live WASM app that attach never ran (verified by console markers and by checking that the JS callback map was never populated), so the observer was never connected. Because the component's render lifecycle could not be relied upon for JS-interop setup, the trigger was moved to a page-level scroll listener that needs no Blazor lifecycle hook at all — it simply finds the sentinel in the DOM and clicks it. This is the standard, robust "scroll-based infinite scroll" pattern and sidesteps the lifecycle issue entirely.

## Verification (Playwright, live Docker app at :8088, signed-out public timeline)

- Initial render: **36** items.
- Scroll to bottom → auto-loads to **56** (a second page, net +20 before the `ItemFilter` excludes non-content items).
- Scroll again → **71**, then **79** (successive pages load on successive scrolls; the `data-busy` re-arm works).
- Clicked the ghost **fallback "Load more"** button → **91** (the manual button still works).
- Console: only the expected federation CORS errors (remote actor profiles on `mas.to` / `mastodon.social`) — **0 new errors** from the infinite-scroll code.

## New coded tests

None. This is a WASM client-side UI change (the WASM manual-test policy in effect since Phase 45: no new coded web-UI tests; UI is verified via Playwright). The load mechanics it relies on (`LoadMoreAsync` re-entry guard, page enumeration) are already covered by the existing `Iris.Client.Tests` / `Iris.Web.Tests` suites. Full fast suite after the change: **1093 passed / 0 failed / 1 skipped**.
