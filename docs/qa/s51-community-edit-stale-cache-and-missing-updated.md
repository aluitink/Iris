# S51 — Community edit: stale cache served to UI + missing `updated` timestamp

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** open
- **Found:** Pass 322 (2026-09-22)
- **Related:** S21 (community creation cache invalidation), S49 (community feed empty), S31 (note edit clears `published` — fixed)

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

## Re-verify

1. Edit a community's name and description.
2. Verify the community header updates **immediately** (no reload needed) or after a single `F5`.
3. Verify `GET /ap/v1/c/<handle>` (no `?refresh`) returns the updated values.
4. Verify the Group document has a non-null `updated` timestamp.
5. 0 console errors.
