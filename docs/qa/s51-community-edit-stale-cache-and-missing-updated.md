# S51 — Community edit: stale cache served to UI + missing `updated` timestamp

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** partially fixed (Pass 329, 2026-09-22)
- **Found:** Pass 322 (2026-09-22)
- **Related:** S21 (community creation cache invalidation), S49 (community feed empty), S31 (note edit clears `published` — fixed), S54 (Blazor `@bind` without `oninput` — root cause of silent stale submission)

## Symptom

On the community detail page, clicking "Edit community" → changing Name and Description → clicking "Save":

1. The form closes. **No success/error message.**
2. The community header still shows the **old** name and description.
3. A full page reload (`F5`) still shows the **old** values.
4. A **hard reload** (cache-busting) shows the **new** values.
5. The AP endpoint `GET /ap/v1/c/<handle>` (via `curl`) returns the **updated** values.
6. The browser's network response for `GET /ap/v1/c/<handle>` returns the **stale** values (server-side cache, not browser cache).

Additionally, the AP Group document has **no `updated` timestamp** after the edit (`updated: null`, `published: null`).

## Root cause

The community edit handler updates the DB but does **not invalidate the server-side cache** for the Group document. Subsequent reads (including the UI's own `GET /ap/v1/c/<handle>`) hit the stale cache until it expires or is busted. The `updated` timestamp is not stamped on the Group object during edit (same class as S31 for notes, which was fixed for notes but not for communities).

## Fix

1. Invalidate the community Group doc cache after a successful edit (same pattern as S21's fix for creation).
2. Stamp `updated` on the Group object when its name/summary/icon/approval settings change.

## Partial fix verified (Pass 329, 2026-09-22)

**Dev1 commits:** `369ba72f` (stamp `updated` on community Group after profile edit) + `db365370` (also stamp `updated` in `HandleCommunityUpdateAsync`), merged to main.

**Verified:** After editing the community name via the UI:
- `GET /ap/v1/c/ii-a8-community` returns the **new** name AND a non-null `updated` timestamp (`2026-09-22T22:04:42.0997438Z`).
- **Facet 2 (missing `updated` timestamp) is FIXED.**

**Still open:**
- **Facet 1 (stale cache / DB not persisted):** The DB (`Objects` table) still has the **old** name and **no** `updated` timestamp. The API returns the new values from an in-memory cache, but the DB was not updated. A server restart would revert the API response to the old values.
- **Facet 3 (UI shows stale values):** The UI still shows the old name after Save. This is partly due to S54 (Blazor `@bind` without `@bind:event="oninput"` — the form may submit stale values) and partly due to the server-side cache not being invalidated for the UI's subsequent reads.

## Re-verify

1. Edit a community's name and description.
2. Verify the community header updates **immediately** (no reload needed) or after a single `F5`.
3. Verify `GET /ap/v1/c/<handle>` (no `?refresh`) returns the updated values.
4. Verify the Group document has a non-null `updated` timestamp.
5. Verify the DB (`Objects` table) has the updated name and `updated` timestamp.
6. 0 console errors.
