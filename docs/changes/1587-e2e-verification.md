# 1587 — Phase 7: End-to-end verification

**Workstream:** Unified home feed (③④)
**Phase:** 7 of 7 (final)

## What was verified

Full live pass across all 7 phases of the unified home feed workstream. No code changes —
verification only.

### Checks

| # | Check | Result |
|---|---|---|
| 1 | `/home` feed tabs: `?source=people` / `?source=communities` requests | 200 OK |
| 2 | FeedBar bottom strip: tabs on `/home`, bell-only on `/directory` | Correct |
| 3 | `/communities` create → delete (2-step confirm, 404 on document) | Pass |
| 4 | `/profile` → Communities tab: empty state + "Manage communities →" link | Pass |
| 5 | Console errors across entire session | 0 |

### Phases covered

1. **Phase 1** — `Page` in `IsContentItem` (Lemmy posts render in `/home`)
2. **Phase 2** — Server `?source=` filter on `/u/{handle}/feed`
3. **Phase 3** — `FeedBar.razor` bottom control strip
4. **Phase 4** — FeedBar tabs wire to `?source=` filter
5. **Phase 5** — `DELETE /local/v1/c/{name}` + community delete in UI
6. **Phase 6** — `/profile` Communities tab
7. **Phase 7** — This verification pass

## Files changed

None (verification only).

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- `Iris.Server.Tests` 1385/1385 pass.
- Live-verified (fresh browser context, `s7test`): all 5 checks pass, 0 console errors.
