# 1584 — Phase 4: Wire home-feed tabs to `?source=` filter

**Workstream:** Unified home feed (③④)
**Phase:** 4 of 7

## What was built

The FeedBar's Posts/Communities tabs now control the home feed's `?source=` filter
(added in Phase 2). A new scoped service `HomeTabState` bridges the FeedBar (in
`MainLayout`, always rendered) and `HomeTimeline` (the `/home` page content).

**`HomeTabState`** (scoped, `Program.cs`): holds the active tab (`"posts"` or
`"communities"`, default `"posts"`). Raises `TabChanged` when the tab switches. Both
`FeedBar` and `HomeTimeline` inject it and subscribe to `TabChanged` for re-render.

**`FeedBar.razor`**: reads/writes `_state.Tab` instead of a local field. The
`localStorage` persistence (`iris:homeTab`) is unchanged.

**`HomeTimeline.razor`**:
- `FeedIri` appends `?source=people` (Posts tab) or `?source=communities` (Communities tab)
  to the actor's `/feed` IRI.
- `FeedKey` includes the tab name so `PagedCollection` resets when the tab switches
  (different collection IRI → new key → fresh load, no stale state).
- Per-tab empty states: Posts → "Follow people… [Browse the directory →]";
  Communities → "Follow communities… [My communities →]".
- Implements `IDisposable` to unsubscribe from `TabChanged`.

## Files changed

- `apps/Iris.Web.Client/Components/HomeTabState.cs` — **new** scoped service.
- `apps/Iris.Web.Client/Components/FeedBar.razor` — uses `HomeTabState`.
- `apps/Iris.Web.Client/Components/Pages/HomeTimeline.razor` — `?source=` wiring + empty states.
- `apps/Iris.Web.Client/Program.cs` — registers `HomeTabState` as scoped.

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- Live-verified (fresh browser context, `s7test`):
  - `/home` Posts tab: feed request `?source=people` (200). Empty state: "Your timeline is empty."
  - Click Communities: feed request `?source=communities` (200). Empty state: "No community posts yet."
  - Click Posts: feed request `?source=people` (200). Empty state back.
  - 0 console errors.
