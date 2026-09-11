# 72.1 — Shared per-object engagement-count cache on `UiContext`

- **Status:** implemented (live-verified; fast suite green)
- **Phase:** 72 (Efficiency + UX residuals), slice 1
- **Resolves:** the 2× `/likes`+`/shares` walk per remote post (Phase 64/70 residual, first observed in 70.2)

## Problem

Each remote post's `EngagementBar` fired its `/likes` + `/shares` collection walk **twice** (14 posts → 56 round-trips instead of 28). The root cause (established in 70.2, re-confirmed this pass): `ActorAvatar`'s async actor-fetch completion triggers a page `StateHasChanged` that **re-creates** the `EngagementBar` as a fresh component instance. A per-instance `_countsLoaded` guard cannot catch this (a fresh instance always starts unloaded), so the second instance re-fired both collection round-trips for the same object.

## Fix

A shared per-object engagement-count cache on `UiContext` (the per-circuit, scoped UI context), keyed by content-object IRI and mirroring the 64.1 actor-coalescing gate:

- **`UiContext.EngagementCounts`** — a new record carrying the walk's result: `LikeCount`, `BoostCount`, the viewer's net `Liked`/`Boosted` state, and the minted `LikeActivityIri`/`AnnounceActivityIri` (the ids an unlike / un-boost `Undo` references).
- **`UiContext.GetEngagementCountsAsync(objectIri, viewerIri)`** — walks `/likes` + `/shares` once (via the existing `GetLikesAsync`/`GetSharesAsync`, `BypassCache` so a fresh read is authoritative), and coalesces concurrent callers for the same object into a single walk (`ConcurrentDictionary<string, Task<EngagementCounts>>` `GetOrAdd` + `TryRemove` in `finally`, the same pattern as the actor gate). A re-created `EngagementBar` for the same post awaits the in-flight task instead of re-firing the network calls.
- **`UiContext.InvalidateEngagement(objectIri)`** — drops the cached entry so the next call re-walks. Called by `EngagementBar` after a successful like / un-like / boost / un-boost, because the cached minted id + net state are stale once the viewer's engagement changes.

`EngagementBar.razor` was rewired to read from the cache in **both** paths:

- **Fast path** (54.8 — server-rendered `iris:likedCount`/`sharedCount`/`isLiked`/`isShared` present): counts + state seed from the extensions as before; the *only* thing still walked (the minted id an `Undo` references, and only when the viewer engaged) now goes through `Ui.GetEngagementCountsAsync` — so a re-created bar reuses the already-loaded walk.
- **Fallback path** (no server counters — non-Iris / un-enriched / unsigned): the full `/likes`+`/shares` walk now goes through the same shared cache.

The **optimistic local-feed delta** (the `_likeCount++`/`--` on a like/unlike click) is applied on top in the component, never cached — the cache stores only the server-derived base, exactly as the 72.1 caveat required.

The bar's former private helpers (`ItemIri`/`LikeActorIri`/`AnnounceActorIri`/`ResolveActorIri`/`IriEquals`) moved into `UiContext` as private statics (the walk now lives there); the bar no longer needs them.

## Files

- `apps/Iris.Web.Client/Ui/UiContext.cs` — `EngagementCounts` record, `_engagement` cache, `GetEngagementCountsAsync`, `WalkEngagementAsync`, `InvalidateEngagement` + the item/actor-IRI helpers.
- `apps/Iris.Web.Client/Components/EngagementBar.razor` — injected `UiContext`; both `OnParametersSetAsync` paths + the four toggle mutation sites now route through / invalidate the shared cache.

## Verification (MCP Playwright, live on `https://iris.luit.ink:8088`, fresh `docker compose build` + `--force-recreate`)

- `/home` renders the home timeline: 10 posts, each with Like/Boost/Reply + counts; the one post `andrew` engaged (a `RayvenMX` status) shows Like **pressed** count 1, Boost **pressed** count 1, reply count 3. **0 console errors** (before + after the like/unlike interaction).
- **The 2× walk is eliminated.** Network capture for the single engaged remote post shows exactly **2** engagement requests (1 `/likes` + 1 `/shares`, both 200) — despite the `ActorAvatar` actor-fetch re-render that previously triggered the second pair. The request sequence (`…/u/andrew` → feed → `/likes` → `/u/andrew` (the re-render-triggering refetch) → `…/RayvenMX` → `/shares`) confirms the re-render happened *after* the first walk and did **not** re-fire it. Pre-fix this post would have produced 4 (2+2).
- **Unlike path verified:** clicking the pressed Like fired an `Undo` (the cached minted id was used), the button dropped `pressed`, and the count decremented — confirming `InvalidateEngagement` + the optimistic delta work together.
- **Build:** `dotnet build apps/Iris.Web.Client` 0 warn / 0 err. **Fast suite** (`dotnet test --filter "Category!=Slow"`): **974/975** (the 1 skip is the known-flaky delivery test, consistent with prior sessions). No new coded tests (WASM manual-test policy — UI verified via Playwright).

## Residual (now addressed by 72.2)

Even with the cache, the engaged post still pays **one** `/likes`+`/shares` walk to recover the minted `Undo` id. 72.2 (minted-id extension) renders that id on the object server-side so the client can skip even that walk.
