# S43 — Cross-instance reply: Replies tab shows "No replies yet" for a remote post (replies ARE delivered to the remote instance)

- **ID:** S43
- **Found:** 2026-09-22, QA Pass 285 (build `6680704e` live on `qa-iris-a.luit.ink` + `qa-iris-b.luit.ink`)
- **Status:** OPEN
- **Class:** Bug / interop / data-visibility
- **Severity:** S3 (data-integrity — the reply IS delivered to the remote instance, but the viewing instance's UI doesn't display it)

## Summary

When a user on instance A replies to a post authored by a user on instance B, the reply activity is **federated to B successfully** (B's `/replies` collection for the post includes the reply). However, when the same user (or any user) on **instance A** opens the B post's object-detail page (`/object?iri=https://qa-iris-b.luit.ink/ap/v1/u/ii-b1/notes/…`), the **Replies tab shows "No replies yet"** — the reply is not listed, even though it exists in B's `/replies` collection.

The reply **count** in the post header correctly shows "1" (from B's `iris:replyCount` extension counter), so the count and the list are **inconsistent**: the header says there is 1 reply, but the Replies tab says "No replies yet".

## Repro

1. On instance A, sign in as `ii-a1` (or any A user who follows `ii-b1@B`).
2. On instance B, ensure `ii-b1` has a note (e.g. `06GCAVHY2NJCS32M66CV64J0V4`).
3. On instance A, open the B post: `/object?iri=https://qa-iris-b.luit.ink/ap/v1/u/ii-b1/notes/06GCAVHY2NJCS32M66CV64J0V4`.
4. Click **Reply**, type a message, and click **Post reply**. The reply is posted (HTTP 202, create IRI on A).
5. Wait ~15 s for federation.
6. **On instance A**, re-open the B post. The header shows **Reply count = 1** (correct — from B's extension counter). But the **Replies tab shows "No replies yet"** (incorrect — the reply exists in B's `/replies` collection).
7. **On instance B** (direct), `GET /ap/v1/u/ii-b1/notes/06GCAVHY2NJCS32M66CV64J0V4/replies` → **200**, `totalItems: 1`, `orderedItems: ["https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/notes/06GCK7XCVG9AT8DSD6E2P5936R"]` — confirming the reply WAS delivered to B.

## Expected

The Replies tab on instance A (viewing a B post) should proxy B's `/replies` collection and list the reply (with the "In reply to ii-b1" breadcrumb), matching the reply count in the header.

## Actual

The Replies tab shows "No replies yet" — the replies collection for the remote post is not being fetched/proxied correctly. The reply count (from B's extension counter) is correct, but the list is empty.

## Root cause (hypothesis)

The object-detail page's `LoadResolvedRepliesAsync` (ObjectDetail.razor:708) calls `client.GetRepliesAsync(objectIri, ...)`, which calls `GetCollectionItemsAsync(objectIri.RepliesOf(), ...)`. For a remote object IRI, this should proxy the `/replies` collection to the remote instance (via the same signed-fetch path the `/proxy` route uses). The proxy may be:
- Not being invoked for the `/replies` collection (only for the object doc itself), or
- Failing silently (the `try/catch` at ObjectDetail.razor:740-743 swallows the error), or
- The remote replies collection items are links (IRIs), and the follow-up `GetObjectAsync(ri)` for each reply IRI (on A) is failing (the reply note is on A, so `GetObjectAsync` should resolve it locally — but if the IRI is not cached, it may 404).

## Impact

Users on instance A cannot see the replies to a B post (even though the replies were delivered to B). The reply count is correct, but the list is empty — a data-visibility inconsistency. This breaks the "reply thread" UX for cross-instance conversations.

## Suggested fix

- Ensure `GetRepliesAsync` (or `GetCollectionItemsAsync`) proxies the `/replies` collection to the remote instance when the object IRI is remote (mirroring the S24-D4 fix for cached remote actor collections).
- For each reply IRI in the collection, `GetObjectAsync` should resolve it (if the reply is local to A, it should be in A's store; if remote, it should be proxied).
- Add a regression test: reply from A to a B post → verify A's object-detail Replies tab shows the reply (and the count matches the list).

## Verification (once fixed)

- Repro steps 1-6: the Replies tab on A should show the reply (with the "In reply to ii-b1" breadcrumb), and the count should match the list.
- 0 console errors.
- The reply should also appear in ii-b1's notifications on B (as a new reply to their post).
