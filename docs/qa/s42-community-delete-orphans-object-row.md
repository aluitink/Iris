# S42 — Deleting a community orphans its `Objects` (Group doc) row: the actor row + edges are removed, but the live Group document is never removed/tombstoned

- **Class:** bug / data-integrity — **Severity:** S3
- **Status:** open (found Pass 280, 2026-09-22, QA stack rebuilt to `6680704e`)
- **Found:** Pass 280 (2026-09-22) — surfaced while exploring the Communities area; the 4 `qa-pass268*-test` communities (created + deleted during Pass 268, pre-S40-fix) still carry **live** `Objects` rows
- **Related:** [S40](s40-community-delete-orphaned-follow-edge.md) (the follow-edge orphan this is the sibling of — S40 fixed the *edge*, S42 is the *object-doc* orphan the same delete path leaves behind), [S21](s21-new-community-missing-following-tab.md) (the create path that writes both the `Actors` row and the `Objects` Group doc), [S5](s05-search-localhost-orphan-actor.md) (same "orphaned row survives deletion" data-integrity family)

## Symptom

After an owner deletes a community they created (`/communities` → the community card's **Delete** → **Confirm**, HTTP 204), the community's **`Actors` row and all its edges are removed** (S40 now removes the inbound `Follow` edge too), but the community's **`Objects` row — the live `Group` document — is neither removed nor tombstoned**. It remains a fully live object (`ObjectType=Group`, `IsTombstoned=f`) forever.

Observed live (instance A, `ii-a1`):
- The 4 `qa-pass268{b,c,d,e}-test` communities were **created then deleted** during Pass 268 (on the pre-S40-fix build). Their `Actors` rows are gone and their follow edges are gone, but **each still has a live `Objects` row**:
  ```
  https://qa-iris-a.luit.ink/ap/v1/c/qa-pass268b-test | Group | f
  https://qa-iris-a.luit.ink/ap/v1/c/qa-pass268c-test | Group | f
  https://qa-iris-a.luit.ink/ap/v1/c/qa-pass268d-test | Group | f
  https://qa-iris-a.luit.ink/ap/v1/c/qa-pass268e-test | Group | f
  ```
  (Control: the *undeleted* `qa-pass261-test` community has both an `Actors` row and a live `Objects` row — as expected.)
- The user-visible consequence (the Following-tab stale render + 404 re-fetch) is already covered by S40 (now fixed). S42 is the **residual data-integrity defect**: a deleted community's document persists as a live object, so the deletion is incomplete at the storage layer.

## Root cause

The community delete path removes the **actor row** and **edges** but never touches the **object document**:

- `DeleteCommunityAsync` (`src/Iris.Server.Data/Stores/EfCommunityStore.cs:185-226`) removes the `ActorEntity` row (`db.Remove(entity)`) + the community-scoped edges + (S40) the inbound `Follow`/`CommunityFollower` edges. It does **not** remove or tombstone the community's `Objects` row.
- `CommunityDeleteHandler` (`src/Iris.Server/ActivityPubServerExtensions.cs:11392-11451`) removes the per-owner inbound `Follow` edges + invalidates caches, then calls `DeleteCommunityAsync`. It likewise never removes/tombstones the object.
- The create path, by contrast, writes **both** rows: `RecordCreateLocalAsync` (the S21 fix) persists the Group actor row **and** the Group object document, so a created community has a matching `Objects` row that delete never cleans up.

The `IObjectStore` already has the needed primitive — `TryDeleteObjectAsync(Iri, ct)` (`src/Iris.Server/Stores/IObjectStore.cs:47`) — which is used by the note-delete path (`DeleteActivityHandler` → Tombstone) but is **not called** by the community delete path.

## Fix

In the community delete path (`DeleteCommunityAsync` and/or `CommunityDeleteHandler`), also remove (or tombstone) the community's `Objects` row for the community IRI — i.e. call `persistence.Objects.TryDeleteObjectAsync(communityIri, ct)` (or the tombstone path used by note deletion, so the IRI still resolves to a `Tombstone` rather than a hard 404, matching the platform's delete semantics). Apply it consistently across the EF, in-memory, and file-backed stores (the S40 fix had to mirror the edge removal in the handler for the in-memory/file-backed providers that can't reach the Follows store — the object removal should be checked for the same provider-agnostic gap).

Regression test: create a community (actor row + object row present), delete it, assert the community's `Objects` row is gone (or tombstoned) — not a live `Group`.

## Re-verify

Clean entry, logged in as the creator:
1. Create a community via `/communities` → "+ Create a community".
2. Confirm both an `Actors` row **and** an `Objects` (Group, `IsTombstoned=f`) row exist for it.
3. Delete it (Delete → Confirm, HTTP 204).
4. DB: the `Actors` row is gone **and** the `Objects` row is gone (or a `Tombstone`).
5. `GET /ap/v1/c/{handle}` → 404 (or a Tombstone), not a stale live Group.
6. 0 console errors on the delete + reload.

## Data cleanup (one-off, separate from the code fix)

The 4 `qa-pass268*-test` orphan `Objects` rows (and any residual `qa-pass269*` if present) are pre-fix test data — a one-off cleanup, not part of the code fix. They are harmless (no UI surface renders them — the Following-tab symptom is S40's, now fixed) but they are the durable evidence of this incomplete-delete defect.
