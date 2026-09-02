# 055 — The hosting server is the sole authority for object ids; clients send content, not identity

> Resolved 2026-09-02. See the change doc that implements it (Phase 19, outbox id-mint rework).

## Context

When an actor authors an activity (a Note, Follow, Undo, Accept, …) it is published to the
actor's own outbox (`POST /ap/v1/u/{actor}/outbox`) and the server records it and federates it.
The open question is **who decides the activity/object `id`**.

The current (pre-decision) behavior is that the **client mints a deterministic id by formula**:
`{actor}/notes/{content-hash}`, `{actor}/creates/{content-hash}`, `{actor}/follows/{target-iri}`,
`{actor}/accepts/{follow-iri}`, etc. (`src/Iris.Client/ActivityPubClient.cs`). This was a
pragmatic early choice that gave client-side retry idempotency for free (recompute the same
content → same id → receiver dedupes).

As the project matures that choice is a liability. An `id` in ActivityPub is **the object's
permanent, canonical, federated URL**: other instances store it and reference it forever, and the
hosting instance must be able to serve `GET` on it authoritatively. Letting an untrusted client
choose that URL has four concrete problems:

1. **Cross-namespace spoofing.** A client posting to its own outbox could set `id` to a URL in
   *another* actor's or *another instance's* namespace (e.g. `https://victim.example/u/mallory/notes/x`).
   The handler only checks the signer owns the activity's *actor* — it never checks the *id* is in a
   namespace the author is allowed to claim.
2. **Id collision / overwrite.** The activity store keys by `id`; a client that can control or guess
   ids could clobber or shadow existing objects.
3. **Fragile shared-formula invariant.** The reference chain (a Follow, its Undo, and its
   Accept/Reject) resolves by *recomputing* the same deterministic IRI on both sides
   (`RecordFollowDecisionLocalAsync`, `UndoFollowAsync`). If the formula ever changes, every prior
   reference breaks. Correctness depends on all parties running the identical formula — a latent
   cross-version bug.
4. **It diverges from every canonical server.** Mastodon, Pleroma, and Misskey all mint the object
   id server-side; the client sends content, not identity. Interop is cleaner when we match.

Because the project is in early development and reinitializing users is acceptable, this is a
clean-slate decision: we pick the *right* ownership model rather than patching the current one.

## Decision

**The hosting server is the sole authority for the `id` of any object or activity it creates.**

Concretely, for the outbox write path:

1. **The client does not send an `id`.** It sends the activity *shape* — type, actor, and object
   content / references — with no `id` (or, optionally, an idempotency key; see below). A Follow is
   sent as `{actor, object: <target IRI>}`; a Note is sent as a `Create` carrying the embedded
   `Note` content but no note id.
2. **The server mints the id** in a fixed namespace with a collision-resistant, non-sequential
   suffix (ULID or UUIDv7 — time-ordered, unguessable, sortable):
   - Note → `{actorBase}/notes/{ulid}`
   - Create → `{actorBase}/creates/{ulid}`
   - Follow → `{actorBase}/follows/{ulid}`
   - Accept/Reject → `{actorBase}/accepts/{ulid}` / `{actorBase}/rejects/{ulid}`
   The server stores the object under that id so `GET` resolves, records the outbox entry and the
   implied local edge, and **returns the created object (with its minted id)** in the `202` body
   (and/or a `Location` header).
3. **The client stores the returned id** for later reference. The id is *learned*, not computed.
4. **Reference-carrying activities carry the referenced object's id by IRI** — the id the authoring
   side already learned. An `Undo` references the follow id the client learned from its own Follow's
   `202` response. An `Accept`/`Reject` is minted by the *followed* actor's server and references the
   *inbound follow's* id, which that server learned from the Follow it received. No party recomputes
   an id by formula.
5. **Inbound federation is unchanged.** When a *remote* object arrives in our inbox, it keeps the
   *originator's* id verbatim — we are a read-through replica and must never rewrite it, or every
   other instance holding the original reference breaks. (This is already the inbound behavior.)
6. **Optional idempotency key.** To preserve retry safety without ceding id authority, the client may
   send an `Idempotency-Key` header. The server mints the real id, but keeps a short-lived
   `key → minted-id` map; a retried request with the same key returns the *same* minted id instead of
   creating a duplicate. The key is a de-dup token only — it is never used as, or to influence, the
   object's `id`.

The net effect: the server is the id authority (closing the spoofing/overwrite/namespace concerns
entirely — no client-chosen ids on the canonical path), ids are unguessable and permanent, and the
reference chain works via *learned* ids instead of a fragile shared formula.

## Alternatives considered

### 1. Keep client-minted deterministic ids, add a namespace-validation guard

The client keeps minting `{actor}/{type}/{content-or-target-hash}` ids; the server only validates the
`id` is in the actor's own instance base and rejects foreign-namespace ids.

- *Pros:* smallest change; preserves the existing dedup-by-formula; no client protocol change.
- *Cons:* the client still controls the id (the root concern is only mitigated, not removed — a client
  can still mint arbitrary *within-namespace* ids, guess/overwrite siblings, and the fragile
  shared-formula reference invariant remains). It keeps the design diverged from every canonical
  server.

### 2. Pure server-mint + return, no idempotency key

The server mints and returns the id; the client stores it; no `Idempotency-Key`.

- *Pros:* fully matches Mastodon; simplest server.
- *Cons:* a retried `POST` that times out before the client sees the `202` creates a **duplicate**
  object, because the client has no de-dup token. Acceptable but strictly worse than option 3 for
  reliability at negligible extra cost.

### 3. Server-mint + return + optional `Idempotency-Key` (chosen)

The server mints and returns the id (as in option 2) *and* accepts an optional `Idempotency-Key`
header for retry de-dup.

- *Pros:* server stays the sole id authority (security); ids unguessable (ULID); retry-safe;
  reference chain uses learned ids (removes the shared-formula invariant); matches canonical servers.
- *Cons:* a small `key → id` map with a TTL to maintain on the server; the client protocol must change
  to stop sending `id` and to read the minted id back (a clean-slate change, acceptable now).

## Consequences

- **Client (`src/Iris.Client`):** `PostNoteAsync`, `FollowAsync`, `UndoFollowAsync`, `AcceptAsync`,
  `RejectAsync`, `LikeAsync`, `AnnounceAsync`, `CreateCommunityAsync`, and the reply/delete paths stop
  setting `Id = …`. They send the activity shape and, where the author needs to reference a prior
  object (Undo of a Follow, delete of a Note), they send the id the client *learned* from an earlier
  `202` response. `DeliveryResult` / the client surface must expose the returned object so the minted
  id is recoverable.
- **Server (`OutboxPublishHandler`, `src/Iris.Server/ActivityPubServerExtensions.cs`):** mints the id
  per the namespace table, stores the object under it, and returns the created object in the `202`
  body. Adds the optional `Idempotency-Key` de-dup map. Keeps the existing signer-owns-actor guard;
  the id-namespace-spoofing concern becomes moot (the client no longer supplies the id).
- **Reference resolution:** `RecordFollowDecisionLocalAsync` and `UndoFollowAsync`/delete resolution
  stop recomputing the deterministic IRI and instead resolve against the id carried by the activity's
  `object` link (the learned id). This removes the cross-version formula invariant.
- **Inbound path:** no change — remote objects keep their originator ids verbatim.
- **Interop:** aligns with Mastodon/Pleroma/Misskey, where the server mints ids and the client learns
  them.
- **Migration:** because reinitializing users is acceptable, there is no need to preserve the old
  deterministic ids; seeded/test fixtures and the live-interop expectations are updated to the new
  scheme.
