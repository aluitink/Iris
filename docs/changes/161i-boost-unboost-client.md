# 161i — Client boost/unboost: `AnnounceAsync` / `UnannounceAsync` via the outbox (19.6.1)

## Summary

Phase 19.6.1 (management via ActivityStream only): the client gains the two missing one-call
management operations — **boost** (`AnnounceAsync`) and **unboost** (`UnannounceAsync`) — both
published to the acting actor's own outbox through the signed pipeline. The server already handled
`Announce` in the outbox publish handler (fan-out to remote, non-blocked followers) and `Undo`
generically; the only missing piece was the client's one-call entry points.

## What changed

### `IActivityPubClient` / `ActivityPubClient`

- **`AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct)`** — builds a deterministic
  `Announce` (actor = `actorId`, object = `objectId`, `Id` =
  `{actorId}/announces/{objectId}` — matching the server's `AnnounceIris.AnnounceIri`) and publishes
  it to `actorId.OutboxOf()` through the signed `DeliverAsync`. The server records the Announce in
  the actor's outbox (so the boost surfaces in the actor's feed) + the activity store, and fans it
  out to the actor's remote, non-blocked followers (mirroring the Create fan-out). Unlike a `Like`,
  an `Announce` carries no embedded object — it is a reference to an existing object IRI — so no
  object-store write is needed.
- **`UnannounceAsync(Iri actorId, Iri objectId, CancellationToken ct)`** — builds an `Undo` whose
  `object` references the original `Announce` by its deterministic IRI (the same
  `{actorId}/announces/{objectId}` `AnnounceAsync` mints) and publishes it to `actorId.OutboxOf()`.
  The `Undo` itself gets a deterministic unique-per-(actor,object) IRI
  (`{actorId}/unannounces/{objectId}`) so a retried unboost dedupes.

### Test stubs

Three test stubs that implement `IActivityPubClient` now implement the two new members (no-op
202s, matching their existing `LikeAsync`/`UnlikeAsync` stubs):

- `tests/Iris.Server.Tests/Services/FeedServiceTests.cs` (`StubClient`)
- `tests/Iris.Server.Tests/Caching/IrisRemoteCollectionFetcherTests.cs` (`StubCollectionClient`)
- `tests/Iris.Server.Tests/Security/IrisActorDocumentFetcherTests.cs` (`StubActivityPubClient`)

## Tests

Two new integration tests in `OutboxSingleSourceOfTruthIntegrationTests` (which already authors
every supported management operation and verifies the outbox is the single source of truth):

- **`ClientAnnounce_PublishesAnnounceToOutbox`** — calls the client's `AnnounceAsync` end-to-end
  (full signed pipeline against the live outbox endpoint) and asserts the Announce is recorded in
  the outbox + activity store with the deterministic IRI `{actor}/announces/{object}`.
- **`ClientUnannounce_PublishesUndoOfAnnounceToOutbox`** — boosts then unboosts and asserts both the
  Announce and the Undo-of-Announce are in the outbox, and that the Undo references the exact
  Announce by its deterministic IRI.

These exercise the **client's** one-call boost/unboost (the raw-inspector / wire half of 19.6.1 is
already covered by the existing `EveryAuthoredActivity_AppearsInTheOutbox_OnceInStableOrder` test,
which authors an Announce + Undo-of-Announce through raw outbox POSTs).

Full suite green: **1,256 tests, 0 failed**. Build clean (`TreatWarningsAsErrors` on).

## Scope note

This closes the **client-side** gap for boost/unboost under 19.6.1 (every management operation is
now expressible as a one-call client method that publishes a signed ActivityStream activity to the
outbox). The **UI** boost button (wiring `AnnounceAsync`/`UnannounceAsync` into the object view)
remains a live/UI-verification item — the client capability it would call is now in place and
pinned.
