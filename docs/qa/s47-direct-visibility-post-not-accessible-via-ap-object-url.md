# S47 — Direct-visibility post not accessible via AP object URL (404 for author + recipient)

- **Class:** bug — **Severity:** S2
- **Status:** open (found Pass 313, 2026-09-22)
- **Found:** Pass 313 (2026-09-22)
- **Related:** S46 (Followers-visibility notes not visible to remote followers — same VisibilityFilter root-cause family); S45 (compose-time mention resolution truncates hyphenated handles — the DM's `tag` array carries the truncated mention IRI).

## Symptom

Signed-in (ii-a1@A). Compose a **Direct**-visibility note mentioning `@ii-b1` (a remote actor on B). After posting:

1. The DM **is delivered** to ii-b1@B's notifications inbox (the Create activity's `cc` correctly names ii-b1@B).
2. The DM is **not** in ii-b1@B's home feed (correct — Direct posts don't appear in feeds).
3. The DM is **not** in ii-a2@A's home feed (correct — Direct posts don't appear in followers' feeds).
4. **However**, `GET https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/notes/...` (the Note's AP URL) returns **404** — even for the **author** (ii-a1@A, Basic auth) and even for the **recipient** (ii-b1@B, Basic auth on B).
5. The WASM UI **does** render the DM on the object-detail page (via the local object store, bypassing the AP endpoint's VisibilityFilter).
6. The Note's stored `to`/`cc` arrays are **incorrect** for a Direct post:
   - `to: ["…/ii-a1/followers", "…/u/ii"]` (followers collection + truncated mention IRI)
   - `cc: "…/ii-a1/followers"` (followers collection)
   - The recipient (ii-b1@B) is **not** named in `to`/`cc`.
7. The Note's `tag` array carries the truncated mention IRI (`…/u/ii` instead of `…/u/ii-b1`) — the S45 compose-time facet.

## Root cause

Two compounding issues:

### 1. Incorrect `to`/`cc` arrays for Direct posts

The Direct-visibility Note's audience should name the **recipient** (ii-b1@B) in `to` or `cc`, not the **followers collection**. The current implementation puts the followers collection in `to`/`cc`, which:
- Makes the post appear to have Followers-visibility (not Direct).
- Causes the VisibilityFilter to deny access to the recipient (the recipient's IRI is not in `to`/`cc`).
- Causes the AP object URL to return 404 for both author and recipient.

The Create activity's audience is correct (`cc: ii-b1@B`), but the Note's audience is wrong. This inconsistency means the Note's `to`/`cc` is built from a different code path than the Create's `to`/`cc`.

### 2. VisibilityFilter denies access when the recipient is not in `to`/`cc`

The `VisibilityFilter.IsVisibleTo` (sync) checks `ContainsAudience(obj, r) || IsAuthor(obj, r)`. For the recipient (ii-b1@B):
- `ContainsAudience`: false (ii-b1@B is not in `to`/`cc`).
- `IsAuthor`: false (the author is ii-a1@A).
- Result: **false** → 404.

For the author (ii-a1@A):
- `IsAuthor`: true (the author is ii-a1@A).
- Result: **should be true** → but the endpoint still returns 404.

The author 404 suggests a different issue: either the object is not found by the AP endpoint (despite being in the DB), or there is an additional gate (e.g., a "Direct posts are not served via AP URL" check) that skips the VisibilityFilter entirely.

## Impact

- **Data visibility**: The recipient cannot view the DM by navigating to its URL (they can only see it in notifications).
- **Federation**: A remote instance cannot fetch the DM via the AP URL.
- **Author access**: Even the author cannot view their own DM via the AP URL.
- **UI workaround**: The WASM UI renders the DM (via the local object store), so the user can still view/delete it in the UI. But the AP URL is broken.

## Repro steps

1. Signed-in as ii-a1@A. Compose a Direct-visibility note: `hello @ii-b1 this is a direct message`. Post.
2. Note the Create IRI (e.g., `06GCMW0J275AMV1YXTPZJJCHYR`) and the Note IRI (e.g., `06GCMW0J275AMV1YXTPZJJCHYW`).
3. `curl -s -w "%{http_code}" -H "Authorization: Basic $(echo -n 'ii-a1:Password1' | base64)" https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/notes/06GCMW0J275AMV1YXTPZJJCHYW` → **404**.
4. `curl -s -w "%{http_code}" -H "Authorization: Basic $(echo -n 'ii-b1:Password1' | base64)" https://qa-iris-b.luit.ink/ap/v1/u/ii-a1/notes/06GCMW0J275AMV1YXTPZJJCHYW` → **404**.
5. Verify the Note is in B's DB: `docker exec -i qa-iris-b-db psql -U iris -d iris_b -t -A -c "SELECT Id FROM Objects WHERE Id LIKE '%06GCMW0J275AMV1YXTPZJJCHY%'"` → returns the Note IRI.
6. Verify the Note's `to`/`cc` from A: `curl -s https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/creates/06GCMW0J275AMV1YXTPZJJCHYR | python3 -m json.tool` → the Note's `to` includes the followers collection, not the recipient.

## Fix direction

1. **Fix the Note's `to`/`cc` arrays for Direct posts**: the recipient's IRI must be in `to` (or `cc`), not the followers collection. The Create's audience is already correct; the Note's audience should match.
2. **Fix the VisibilityFilter for Direct posts**: even if the `to`/`cc` are correct, the author should always have access (the `IsAuthor` check should work). Investigate why the author gets 404 despite `IsAuthor` being true.
3. **Consider a dedicated "DM access" check**: for Direct posts, the recipient (and author) should always have access, regardless of the `to`/`cc` arrays. This is a semantic check, not a string match.

## Re-verify

1. Fix the Note's `to`/`cc` for Direct posts.
2. Rebuild the QA stack.
3. Signed-in as ii-a1@A: compose a Direct note mentioning `@ii-b1` → Post.
4. `GET` the Note's AP URL as ii-a1@A (author) → must return **200** with the Note.
5. `GET` the Note's AP URL as ii-b1@B (recipient, on B) → must return **200** with the Note.
6. `GET` the Note's AP URL as ii-a2@A (non-recipient follower) → must return **404** (correct — not in the audience).
7. The DM must still NOT appear in ii-a2@A's or ii-b1@B's home feed.
8. The DM must still appear in ii-b1@B's notifications.
