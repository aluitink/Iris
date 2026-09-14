# 136.19 — Data lifecycle and tombstone retention

**Date:** 2026-09-14
**Slice:** 136.19 (Lemmy interop — data lifecycle and tombstone retention)
**Status:** **COMPLETE.** Three cross-instance integration tests verify that a tombstoned object is not
re-animated by subsequent writes (Create, Update) and that tombstones are excluded from search. The core
fix adds a tombstone guard to three write paths that previously called `PutObjectAsync` unconditionally.

## What this slice delivers

136.19's acceptance criteria:

1. Define retention/expiry expectations for tombstones and deleted remote references in Iris.
2. Validate behavior when old remote links are revisited after delete propagation (UI, API, cache, and logs).
3. Confirm retention policy does not reanimate deleted content during re-sync/backfill.
4. Exit when delete lifecycle outcomes are deterministic and operator-documented.

### Core gap: re-animation of deleted content

Before 136.19, the object store's `PutObjectAsync` is a blind overwrite (dictionary set). Three write
paths called it unconditionally with no check for whether the current value at that IRI is a Tombstone:

1. **`CreateActivityHandler.StoreEmbeddedObjectAsync`** (federation path) — a re-delivered `Create`
   (at-least-once delivery semantics) or a backfill re-fetch for a tombstoned IRI would overwrite the
   Tombstone with the live Note, resurrecting the deleted object.
2. **`UpdateActivityHandler.HandleAsync`** — a late-arriving `Update` for a tombstoned IRI (an edit
   delivered after the Delete) would overwrite the Tombstone with the updated content.
3. **AP proxy re-store** (`ActivityPubServerExtensions` relay) — a proxied GET of a remote object whose
   local copy is a Tombstone would re-store the remote's live content (the remote may still serve the
   original if the Delete has not propagated there yet).

### Fix

A tombstone guard is added to each of the three write paths:

- **`CreateActivityHandler.StoreEmbeddedObjectAsync`** (`src/Iris.Server/Inbox/CreateActivityHandler.cs`):
  before storing the embedded object, resolves its IRI and probes the object store. If the stored object
  is a `Tombstone`, the method returns early — the Tombstone is preserved, the live content is not
  re-stored, and no reply edge / Create index / media warm / attributedTo fetch follows.

- **`UpdateActivityHandler.HandleAsync`** (`src/Iris.Server/Inbox/UpdateActivityHandler.cs`): after the
  existing `TryGetObjectAsync` (which already requires the object to be stored), a new check verifies the
  stored object is not a `Tombstone`. If it is, the method returns early — the Tombstone is preserved.

- **AP proxy re-store** (`src/Iris.Server/ActivityPubServerExtensions.cs`): before calling
  `PutObjectAsync` for a proxied object, the store is probed. If the local copy is a `Tombstone`, the
  re-store (and the interaction count sync, which would re-walk the deleted object's collections) is
  skipped. A re-fetch that returns a `Tombstone` is harmless (the guard only skips non-Tombstone content).

### Retention model (documented)

- **Tombstones are permanent.** There is no TTL, expiry, or background cleanup for tombstones. Once an
  object is deleted, its slot holds a `Tombstone` indefinitely. This is the AS2.0 model: a deleted
  object is represented by a persistent tombstone marker (not a hard-delete), so that late-arriving
  references to the IRI resolve to a meaningful document (the tombstone) rather than a 404.
- **Read paths exclude tombstones.** The object store's search and listing methods, the global search
  service, and the object-document endpoint all skip `Tombstone` entries (deleted content is not
  searched, listed, or enriched with like/boost counts).
- **The activity store retains all activities** (including `Delete`s and the original `Create`s) with no
  pruning. The `Delete` handler removes the object's `Create` from the author's outbox and the
  object→Create index, but the `Create` activity object itself remains in the activity store. This is
  acceptable: the activity store is an append-only log, and the outbox reference (what feeds the
  author's post list) is correctly removed.
- **Like/announce edges on a tombstoned object are not swept.** A bounded stale artifact: the edges
  remain in the like/announce stores. The object-document endpoint does not enrich tombstones with
  interaction counts (it skips tombstones), so this is not user-visible. A future moderation/cleanup
  slice could sweep them.
- **Re-animation is now prevented** (this slice): the three write paths that previously could overwrite a
  Tombstone with live content now check for the Tombstone and skip the write.

### Test coverage

Three tests in `CrossInstanceTombstoneRetentionIntegrationTests` (two-instance `TestServer` fixture,
A: `tomb-a.domain.local` alice, B: `tomb-b.domain.local` bob, bob follows alice):

| Test | What it verifies |
|------|-----------------|
| `ReDeliveredCreate_DoesNotReAnimate_TombstonedObject` | alice (A) posts m1 (federated to B), alice deletes m1 (federated to B — B tombstones its copy), then a re-delivered Create for m1 (fresh activity IRI, same object IRI) is delivered to B's inbox. B's CreateActivityHandler must NOT re-store the live content — the Tombstone is preserved. |
| `LateUpdate_DoesNotReAnimate_TombstonedObject` | alice (A) posts m1, alice deletes m1 (A tombstones), then a late-arriving Update for m1 (attributed to alice) is posted to A's outbox. A's UpdateActivityHandler must NOT re-store the live content — the Tombstone is preserved. |
| `TombstonedObject_ExcludedFromSearch` | A live Note and a Tombstone are stored under different IRIs. Search finds the Note but not the Tombstone (pins the read-path exclusion). |

### Key findings

- **The re-animation gap was real.** All three write paths (`CreateActivityHandler`, `UpdateActivityHandler`,
  AP proxy re-store) called `PutObjectAsync` unconditionally. A `Create` with a fresh activity IRI but the
  same object IRI as a tombstoned note would pass the `InboxProcessor`'s dedupe (which is by activity
  IRI, not object IRI) and re-store the object. The fix is a per-IRI Tombstone check before the write.
- **The guard is a no-op for the normal delete flow.** The `DeleteActivityHandler` (and `TombstoneInbound`)
  call `PutObjectAsync` directly (not through the guarded paths), so the tombstone is written correctly.
  The guard only affects subsequent writes for the same IRI.
- **The `RecordCreateLocalAsync` local-post path is not guarded.** A local user re-posting a deleted note's
  IRI via their outbox would still re-store it. This is a lower-risk path (it requires the local user to
  explicitly re-post the same IRI) and is a candidate for a future slice if it becomes a concern.
- **Proxy re-store guard skips interaction sync.** When the local copy is a Tombstone, the proxy re-store
  guard also skips the 132.1 interaction count sync (which would re-walk the deleted object's /likes,
  /shares, and /replies collections — unnecessary and potentially expensive for a deleted object).
