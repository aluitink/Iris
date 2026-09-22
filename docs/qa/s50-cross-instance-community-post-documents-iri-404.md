# S50 — Cross-instance community post uses `documents/` IRI (type "Page") → 404 on remote instance

- **Class:** bug / federation — **Severity:** S2
- **Status:** open
- **Found:** Pass 321 (2026-09-22)
- **Related:** S30 (cross-instance community join/post — previously fixed), S49 (community feed empty)

## Symptom

Posting a note to a **remote** community (`ii-a8-community`@`qa-iris-a.luit.ink`) from instance B (`ii-b1`@`qa-iris-b.luit.ink`):

1. Compose page shows "Posting to II-A8 Test Community" → "Post to community" → **HTTP 202**.
2. The Create activity's object has:
   - `id`: `https://qa-iris-b.luit.ink/ap/v1/u/ii-b1/documents/06GCNQ0EHKMYJDFB9YK0CBNA7C`
   - `url`: same `documents/` IRI
   - `attributedTo`: `[ii-b1, ii-a8-community]`
   - `to`: `[ii-a8-community, #Public]`
   - `cc`: `ii-a8-community/followers`
   - `type`: **Page** (not Note)
3. On instance B: `GET /ap/v1/u/ii-b1/documents/06GCNQ0EHKMYJDFB9YK0CBNA7C` → **200** (works).
4. On instance A: `GET /ap/v1/u/ii-b1/documents/06GCNQ0EHKMYJDFB9YK0CBNA7C` → **404** (even after 10s wait).
5. Instance A's DB **does have** the object cached: `Objects` row with `Id = …/documents/06GCNQ0EH…`, `ObjectType = Page`.
6. The note is **NOT** in the community feed on A (`/ap/v1/c/ii-a8-community/feed` does not include it).

## Root cause hypothesis

When posting to a **remote** community, the server generates a `documents/` IRI with type "Page" instead of a `notes/` IRI with type "Note". This is different from posting to a **local** community (which correctly uses `notes/` + "Note"). The remote instance's object-doc endpoint may not recognize the `documents/` path for a remote actor's content, returning 404 despite the object being cached in the DB.

**Contrast (local community post, Pass 319):**
- IRI: `…/u/ii-b1/notes/06GCNKS5FN696JZA1E0MDA219C`
- Type: Note
- `GET` on B: 200

## Fix

TBD — needs investigation of the community post creation path to determine why remote-community posts generate `documents/` IRIs (Page) instead of `notes/` IRIs (Note).

## Re-verify

1. From instance B, post a note to the remote community `ii-a8-community`@A.
2. Verify the Create activity's object IRI uses `notes/` (not `documents/`) and type is "Note" (not "Page").
3. Verify `GET /ap/v1/u/ii-b1/notes/<id>` on instance A returns 200.
4. Verify the note appears in `GET /ap/v1/c/ii-a8-community/feed` on instance A.
5. 0 console errors.
