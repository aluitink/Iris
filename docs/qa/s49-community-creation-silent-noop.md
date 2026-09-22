# S49 — Community creation is a silent no-op (no error, community not created)

- **Class:** bug — **Severity:** S2
- **Status:** open
- **Found:** Pass 318 (2026-09-22)
- **Related:** S21 (newly created community missing from Following tab — fixed, but creation itself now fails entirely)

## Symptom

On `https://qa-iris-b.luit.ink/communities`, clicking "+ Create a community" opens a form with Name, Handle, and Description fields. After filling in:
- Name: `QA Pass 318 Community`
- Handle: `qa-pass-318`
- Description: `QA pass 318 community test`

and clicking the "Create community" button, the form closes silently. **No error message is shown.** The community is NOT created:

- "My communities" tab: "You don't own any communities yet."
- "All on this instance" tab: "No communities on this instance yet."
- "Following" tab: only the pre-existing `ii-a8-community` (from a previous session) is listed.

0 console errors.

## Root cause

Not yet determined. The form submits successfully (no client-side validation error, no HTTP error visible in the browser), but the community does not appear in any list. Possible causes:

1. The server-side `POST` to create the community fails silently (e.g., 400/500 response that the WASM UI does not surface).
2. The community is created in the DB but the UI does not refresh the community lists after creation.
3. The handle `qa-pass-318` conflicts with an existing entry (case-insensitive collision) and the server rejects it without the UI showing an error.

## Fix

TBD — needs investigation of the network request the WASM makes on "Create community" click, and the server-side community creation handler.

## Re-verify

1. On `https://qa-iris-b.luit.ink/communities`, click "+ Create a community".
2. Fill Name="QA S49 reverify", Handle="qa-s49-reverify", Description="test".
3. Click "Create community".
4. Verify: a success message or the new community appears in "My communities" + "All on this instance" + "Following" tabs.
5. Verify: `GET /ap/v1/c/qa-s49-reverify` returns 200 with the Group document.
6. 0 console errors.
