# S55 — Post edit: UI shows stale content after save; Post delete: silent no-op (no request sent)

- **Class:** bug / UX — **Severity:** S2
- **Status:** open
- **Found:** Pass 332 (2026-09-22)
- **Related:** S51 (community edit stale cache — different area, same symptom class)

## Symptom

**Facet 1 — Post edit: UI shows stale content after successful server-side save:**
1. On an object detail page (own post), click **Edit**.
2. Modify the text (e.g., change "test post for edit/delete" to "test post EDITED (edit works)").
3. Click **Save**.
4. The UI **still shows the old text** — no re-render, no confirmation, no error.
5. **Server-side the edit DID land:** `GET /ap/v1/u/ii-a1/notes/06GCPRA8…` returns the **new** content + an `updated` timestamp; the DB `Objects.Document` also has the **new** content.
6. A full page reload (F5 / navigate away and back) **still shows the old text** — the stale content persists across reloads.

**Facet 2 — Post delete: silent no-op (button disappears, no request sent, nothing deleted):**
1. On an object detail page (own post), click **Delete**.
2. The **Delete button disappears** from the UI (replaced by an empty element) — making it look like the delete happened.
3. **No network request is fired** (verified via network log: no DELETE/POST to the outbox or a delete endpoint).
4. **Server-side nothing changed:** `GET /ap/v1/u/ii-a1/notes/06GCPRA8…` still returns **200** with the full object; DB `Objects.IsTombstoned` is still **false**.
5. The post is **not deleted** — it still appears in the profile "Your posts" tab and the home feed.

## Repro

1. Sign in as `ii-a1` on `https://qa-iris-a.luit.ink`.
2. Compose a new note (e.g., "QA Pass 332: test post for edit/delete") → Post (HTTP 202).
3. Open the post's object detail page (`/object?iri=…/notes/06GCPRA8…`).
4. **Edit facet:** Click **Edit** → change the text → **Save** → observe the UI still shows the old text. `curl` the AP object → new content present. Reload the page → old text still shown.
5. **Delete facet:** Click **Delete** → observe the button disappears. Check network log → no delete request. `curl` the AP object → still 200, not tombstoned.

## Expected

- **Edit:** After Save, the UI should immediately re-render with the new content (or navigate to a fresh fetch). A page reload should show the new content.
- **Delete:** Clicking Delete should either (a) show a confirmation dialog and, on confirm, send the delete request and navigate away; or (b) immediately send the delete request and navigate away. The button should not disappear without a request being sent.

## Root cause (hypothesis)

Both facets point to the **object detail page's client state not syncing with the server** after a mutation:

- **Edit:** The `Save` handler likely calls the server-side edit endpoint (which succeeds — AP object + DB updated) but does **not** refetch the object or update the local Blazor state with the new content. The page re-renders from the **stale in-memory object** (the pre-edit doc), so the UI shows the old text. The stale state persists across reloads because the client **caches the object by IRI** and serves the cached (pre-edit) doc on subsequent loads without checking the server's `updated` timestamp.
- **Delete:** The `Delete` button's click handler likely sets a local state flag (e.g., `_deleted = true`) that hides the button, but **does not actually call the server-side delete endpoint** (no `DeleteAsync` / `TombstoneAsync` call, no outbox `Delete` activity POST). The button disappears because the local state changed, but no request was ever sent.

**Suspect code paths:**
- `ObjectDetail.razor` — the Edit/Save and Delete handlers. The Save handler may be missing a `StateHasChanged()` + refetch after the server call. The Delete handler may be missing the actual `DeleteAsync` call.
- The object cache in `IActorSessionAccessor` / `ActivityPubClient` — may be serving stale cached docs without an invalidation on edit/delete.

## Fix

- **Edit:** After a successful server-side edit, either (a) refetch the object by IRI and update the local state, or (b) invalidate the object cache entry and let the next render fetch fresh. Ensure `StateHasChanged()` is called after the state update.
- **Delete:** The Delete handler must actually call the server-side delete endpoint (e.g., `client.DeleteObjectAsync(iri)` or POST a `Delete` activity to the outbox). Optionally add a confirmation dialog before the destructive action. After a successful delete, navigate away (e.g., to `/profile` or `/home`).

## Re-verify

**Edit:**
1. Compose a new note → Post.
2. Open the object detail page.
3. Click **Edit** → change the text → **Save**.
4. Verify the UI **immediately shows the new text** (no reload needed).
5. Reload the page → verify the **new text** is shown.
6. `curl` the AP object → content matches the edit.
7. 0 console errors.

**Delete:**
1. Compose a new note → Post.
2. Open the object detail page.
3. Click **Delete** → (if confirmation dialog appears) confirm.
4. Verify a **network request** is sent (DELETE or POST to outbox).
5. Verify the page **navigates away** (to /profile or /home).
6. `curl` the AP object → **404** or **410** (tombstoned).
7. DB `Objects.IsTombstoned` → **true**.
8. The post is **gone** from the profile "Your posts" tab and the home feed.
9. 0 console errors.
