# S8 — Communities "All on this instance" list is incomplete/inconsistent

- **Class:** bug / data — **Severity:** S2
- **Status:** fixed (2026-09-20, verified Pass 27 against the 2026-09-20 03:31 UTC rebuild; deployed-commit label in PLAN.md was stale, see Pass 27 note)
- **Found:** Pass 15 (2026-09-20) — re-confirmed Passes 16, 17

## Symptom

The Communities page's "All on this instance" tab shows only **6** community cards (interop, owner-test-5428, piefed-test, technology, test-882, test-community-541) while the local-Group search endpoint (`/ap/v1/search?local=true&type=Group`) returns **11** (6 Iris + 5 remote). The UI **omits the local `qa-pass15`** (freshly created — API 200 + present in search) **and the local `interop`** community, yet *includes* the **remote** `lemmy.luit.ink/c/interop`. Stable across reload.

## Root cause (suspected)

`Communities.razor:195-222` builds the list from `SearchAsync(baseIri, "", {Type="Actor", LocalOnly:true})` filtered to `Group` and sorted by handle — the same endpoint the API query hits, so it's a real **filter / limit / IRI-normalization gap** (a missing *local* item can't be pushed past a limit that still fits a *remote* one).

## Fix

Make the local-Group list a complete, correctly-scoped local query: dedupe by normalized IRI, don't drop local Groups, exclude remote Groups. Re-verify the count matches the local-Group store.

## Re-verify

Create a fresh local community; it appears in Communities → "All on this instance" immediately; the tab count equals the local-Group store count (no remote Groups listed).

**Re-verification evidence (Pass 27, 2026-09-20, andrew):** Communities → "All on this instance" now lists **all 6 local communities** (interop, owner-test-5428, qa-pass15, technology, test-882, test-community-541 — matching the DB's 6 local `Group` objects) plus the seeded remote ones (interopX, piefed-test), with correct Join/Leave state (technology = Leave) and **0 console errors**. FIXED. (Note: the tab still surfaces the 2 seeded remote cards, but the original "missing local community" defect is gone.)
