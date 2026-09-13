# Phase 131.3 — Federation edge cases: tombstone rendering

**Date:** 2026-09-13
**Branch:** `phase-32-production-app`

## What was built

The Blazor WASM client now renders AS2.0 `Tombstone` objects (deleted objects) as a graceful
placeholder instead of falling through to the generic `IObject` branch (which would render an empty
card with no content) or, in the worst case, a broken "Unknown" state.

### Changes

| File | Change |
|---|---|
| `apps/Iris.Web.Client/Components/ObjectView.razor` | New `else if (Item is Tombstone)` branch (before the generic `IObject` fallback) rendering a muted "deleted" placeholder card with a warning icon, the `formerType` label (e.g. "Note post deleted"), and the `deleted` timestamp. |
| `apps/Iris.Web.Client/Components/ObjectView.razor.cs` | Added `internal static string? TombstoneFormerType(Tombstone)` helper — reads the first `formerType` value for the placeholder label. |
| `apps/Iris.Web.Client/Components/Pages/ObjectDetail.razor` | (1) New `IsTombstone` property. (2) `PageHeading` returns "Deleted post" for tombstones. (3) Body: when `IsTombstone`, renders a dedicated tombstone card (icon + "Note post deleted" + deleted time + hint text) and skips the edit/delete/reply/engagement/replies/parent-context surface entirely. (4) `OnParametersSetAsync` skips `LoadParentAsync`/`LoadRepliesAsync`/`LoadEngagementAsync` for tombstones. (5) `ShowRepliesSection` excludes tombstones. |
| `apps/Iris.Web.Client/wwwroot/css/app.css` | New `.object-tombstone` styles (dashed border, muted background, warning icon, label/state/time/hint sub-elements, `--detail` variant for the full-page card). |
| `apps/Iris.Web/wwwroot/css/app.css` | Same `.object-tombstone` styles (kept in sync). |

### Design decisions

1. **Tombstone branch placed before the generic `IObject` fallback in `ObjectView`.** The
   `Tombstone` class implements `IObject` (inherited from `Object`), so without an explicit branch
   it would fall into the `Item is IObject { Id: { Length: > 0 } id }` pattern and render an empty
   card (a tombstone has no `Content`, `AttributedTo`, `Published`, etc.). The explicit branch
   catches it first.

2. **ObjectDetail skips all enrichment for tombstones.** A deleted object has no content, replies,
   engagement, or parent context to load. Skipping `LoadParentAsync`/`LoadRepliesAsync`/
   `LoadEngagementAsync` avoids three unnecessary network fetches (and the client's `ProxyGoneCache`
   410 short-circuit for dead IRIs).

3. **`formerType` is surfaced in the label.** The server's `BuildTombstone` preserves the original
   object's AS2.0 type (e.g. "Note"), so the placeholder reads "Note post deleted" rather than a
   generic "Post deleted" — matching the user's mental model of what was removed.

4. **No new coded tests** (WASM manual-test policy, Phase 45+). Verification is via live Playwright.

## Verification (live Playwright, `iris.luit.ink` via Docker)

1. **Created a post** as `andrew` ("Tombstone test post - will delete this") → landed in profile.
2. **Deleted the post** via the object detail page (Delete → Confirm) → navigated to home.
3. **Server serves a Tombstone:** `curl /ap/v1/u/andrew/notes/06G9P6MR2FZ0CVZ4ZHS7V55QZR` →
   `{"type":"Tombstone","id":"…","formerType":"Note","deleted":"2026-09-13T14:11:03Z"}`.
4. **Object detail page renders the tombstone placeholder:** heading "Deleted post", card shows
   warning icon, "Note post deleted", "deleted 09/13/2026 14:11", and the hint
   "This object was removed. Its content, replies, and interactions are no longer available."
   No Reply/Edit/Delete buttons, no Replies section, no engagement counts.
5. **Profile timeline:** the deleted post does not appear (correctly excluded from the outbox feed).
6. **Home timeline:** renders without errors.
7. **Console errors:** 0 across all pages visited.

Screenshot: `tmp/.playwright-mcp/page-2026-09-13T14-11-46-234Z.png` (tombstone detail page).

## Build & test

- `dotnet build Iris.slnx` — clean (0 errors, 0 warnings).
- `dotnet test Iris.slnx` — all pass (1 known flaky federation test under full-suite concurrency;
  passes in isolation).
