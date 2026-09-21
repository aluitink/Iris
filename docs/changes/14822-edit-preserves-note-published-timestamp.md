# 14822 — Preserve a Note's `published` timestamp across an edit (S31)

- **Date:** 2026-09-21
- **Fixes:** [S31 — editing a Note clears its `published` timestamp](../qa/s31-edit-clears-published-timestamp.md) (S3-sev, data-integrity)
- **Commits:** `45f3038`
- **Deployed:** base `docker-compose.yml` app `irisweb-iris-web-1`, healthy (redeployed 2026-09-21 on the user-requested base compose, no dev/fed2 overlays)

## Problem

Editing a Note **cleared its `published` timestamp** (the original creation time was lost), and the `Update` activity's `object` omitted `updated`/`published`. The edit **content** federated correctly (no stale copy), but the Note lost its publication time — and the cleared value propagated to peers (B's proxy copy of the note showed `published = None`).

QA reproduced it on the two-instance federation stack (Interop A9): `ii-a1` posted a note (`published` = `19:27:48`, no `updated`), then edited the body → `GET A <note>` showed `content` updated + `updated` set, but **`published` = None**; A's outbox `Update` object omitted `updated`; B's proxy of the note showed `published` = None.

## Root cause

Both the **local edit** path and the **inbound federation** path route through `Inbox/UpdateActivityHandler.HandleAsync` (the local outbox publish routes the `Update` to the same handler). That handler stores the **incoming** embedded object directly (`PutObjectAsync(updated)`):

```
if (updated is ActivityObject contentObj && contentObj is not Actor)
{
    var now = DateTime.UtcNow;
    var published = contentObj.Published;          // null — the edit's object carries no `published`
    contentObj.Updated = published is { } pub && now < pub ? pub : now;   // → now
}
await _persistence.Objects.PutObjectAsync(updated, ct);   // stored WITHOUT `published`
```

The client builds the edit's embedded object as a **bare** `Note` (only `id` / `content` / `attributedTo` / `to` — see `ObjectDetail.razor` `SaveEditAsync`), so it carries **no `published`**. ActivityPub treats `published` as immutable after creation, so the client never re-sends it — but the handler dropped the stored object's `published` by overwriting with the bare incoming object. The result: `published` cleared, `updated` stamped.

## Fix

In `UpdateActivityHandler.HandleAsync`, **carry the stored object's `published` onto the incoming object** before storing it (then stamp `updated` as before):

```
if (updated is ActivityObject contentObj && contentObj is not Actor)
{
    contentObj.Published ??= (stored as ActivityObject)?.Published;   // preserve the creation time
    var now = DateTime.UtcNow;
    var published = contentObj.Published;
    contentObj.Updated = published is { } pub && now < pub ? pub : now;
}
await _persistence.Objects.PutObjectAsync(updated, ct);
```

Because `stored` is the object this instance already holds (looked up at the top of the method) and the incoming `updated` is the same instance the `Update` activity's `object` refers to, this single change fixes **both** the stored Note (a later `GET <note>` serves `published` preserved + `updated` set) **and** the `Update` activity's object (which is the same instance, so it now carries `published` + `updated`), so the correct value is what propagates to peers. The `??=` preserves any `published` a client *did* send (defensive); it only back-fills from the stored object when the incoming object has none. Actor profile updates go through `HandleActorUpdateAsync` (field-merge) and are unaffected.

## Tests

- `UpdateActivityHandlerTests.HandleAsync_LocalOwnerEditsNote_PreservesPublishedAndStampsUpdated` (new): stores a Note with a known `published`, edits it (the edit's embedded object carries no `published`), and asserts the stored Note's `published` is **unchanged** (not cleared) and `updated` is set to the edit time (after `published`).
- `UpdateActivityHandlerTests.HandleAsync_LocalOwnerUpdatesNote_StampsUpdated` (strengthened): now also asserts `published` is preserved across the edit (it previously only asserted `updated` was stamped — which is exactly the gap S31 exposed).

## Verification

- Fast suite: Iris.Server.Tests **1410 pass, 0 fail** (was 1409; +1 new test); Iris.Web.Tests **108 pass, 0 fail**. (The 5 `Iris.LiveInterop.Tests` failures are **environmental** — they require a live Lemmy on `localhost:8091`, which is not running in this environment; they are unrelated to this change and fail identically on the pre-change build.)
- **Live (note):** the S31 defect is in the server's `UpdateActivityHandler`, which is fully covered by the unit tests above. Live verification of the note-edit round-trip (post → record `published` → edit → `published` preserved + `updated` set on `GET <note>` and on the outbox `Update` object) requires an authenticated note edit, which on the prod app needs the signed ActivityPub outbox POST (the browser session cookie). The pre-existing `alice` account's cookie password is not the documented dev seed, so a scripted note-edit live-verify was not feasible this turn — **deferred to QA on the two-instance federation stack** (where the interop A9 entry is the clean reproduction), consistent with the S2-sev cross-instance re-verify items.
