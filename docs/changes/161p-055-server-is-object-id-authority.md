# 161p — decision 055 close-out: the server is the sole authority for object ids (20.0)

## Summary

Phase 20.0: implement **decision 055** ("the server is the sole authority for object ids") and drive the
`Iris.Server.Tests` suite back to green. The prior (pre-055) model had *clients* compute deterministic
object ids from a guessable formula (`{actor}/{namespace}/{suffix}`, e.g. `{actor}/follows/{target}`,
`{actor}/notes/{contentHash}`), which made ids enumerable and let a peer replay a guessed id to
collide/overwrite a different actor's object. Decision 055 inverts that: **only the instance that
authorizes an object mints its id** (an unguessable, monotonic ULID), the id is returned to the author in
the 2xx response body, and references to that object always carry the **learned** (minted) id — never a
recomputed formula. Inbound federation keeps the originator's id verbatim (the originator already minted
it); the server never mints an id for an inbound object.

## Model (locked)

- **Id shape:** `{actorBase}/{namespace}/{ulid}` — e.g. `https://host/ap/v1/u/alice/creates/01J9…`.
  The ULID is monotonic per instance (time-ordered, lexicographically sortable) and unguessable.
- **Authoring surface (outbox):** a client POSTs only the activity's *shape* (no `id`). The server
  mints the id under the acting actor, records it, and returns the **created object in the 2xx body**
  (`Results.Text(json, ActivityJsonContentType, 202)`). The caller reads `DeliveryResult.MintedId` to
  learn the id for any later reference (Undo/Announce/Delete).
- **Inbound federation (inbox):** the server keeps the originator's id verbatim (it is the
  originator's, already minted). The inbox handler records the object under that id.
- **Learned-id references:** `Undo`/`Announce`/`Delete`/`Accept`-inverse references carry the id the
  client *learned* (from a prior `DeliveryResult.MintedId` or the returned body), never a recomputed
  formula.

## What changed (production)

- **`src/Iris.Core/Identity/Ulid.cs`** (new): `Ulid` — a monotonic, unguessable 128-bit ULID generator
  (time-ordered; per-instance monotonic counter guarantees strictly increasing values within the same
  millisecond). Plus `MonotonicUlid` (the stateful singleton the server uses).
- **`src/Iris.Server/Identity/IdMinter.cs`** (new, DI singleton): `IdMinter.Mint(actorIri,
  namespace)` → `{actorBase}/{namespace}/{ulid}`. The single authority that mints object ids; injected
  into the outbox/community handlers and the follow-response builder.
- **`src/Iris.Server/Stores/ICreateIndex.cs`** (new) + `InMemoryCreateIndex` / `FileBackedCreateIndex`:
  a `{actorBase}/creates/{ulid}` → stored-Create lookup so the server can resolve a minted Create id back
  to its object (needed by Undo/Delete on a learned Create id).
- **`src/Iris.Server/ActivityPubServerExtensions.cs`**:
  - `OutboxPublishHandler` — mints the activity's id (and the embedded object's id) via `IdMinter`
    before recording, and returns the **created object in the 2xx body** via `Results.Text` (a JSON
    *string*, not a quoted string — the first attempt serialized the body as a JSON string literal, so
    `MintedId` parsed to null).
  - `MintActivityIds(idMinter, actorIri, activityToMint)` — mints a missing activity id **and** preserves
    any client-set embedded-object id (only mints the embedded object when its id is null/empty).
    Mutates the embedded `Create.Object` list in place **and** reassigns `Create.Object` (the
    ActivityStreams model's collection mutation does not persist through the serializer otherwise).
  - `CommunityOutboxPublishHandler` — now takes `IdMinter`; handles Add/Remove (community membership) in
    addition to Follow/Undo; mints the activity id before recording; delegates to the extracted
    `FinishCommunityOutboxPublishAsync` which records outbox + activity store, delivers only to a
    non-local recipient, and returns the created object in the 2xx body.
- **`src/Iris.Server/FollowIris.cs`** + **`Inbox/FollowActivityHandler.cs`** / **`FollowResponseActivityHandlerT.cs`**:
  the inbound-follow response (Accept/Reject) is now **minted** under `{actor}/accepts|rejects/{ulid}`
  (the handler injects `IdMinter`), not computed from the follow id. `BuildUndo` (unfollow) likewise
  mints.
- **`src/Iris.Client/ActivityPubClient.cs`**: every authoring method now sends **no id** (the server
  mints): `FollowAsync`, `AcceptAsync`, `RejectAsync`, `PostNoteAsync` (no Create id, no embedded-note
  id), `LikeAsync`, `AnnounceAsync`, `AddMemberAsync`, `RemoveMemberAsync`. The five "inverse" methods
  (`UndoFollowAsync`, `UnlikeAsync`, `UnannounceAsync`, `RemoveMemberAsync`, `DeleteAsync`) take the
  **learned** id of the original object as a parameter (the client never recomputes the server's ids).
  `AddMemberAsync`/`RemoveMemberAsync` were repointed from the community's *inbox* to its *outbox* (the
  authoring surface where the server mints).
- **`src/Iris.Client/DeliveryResult.cs`** / **`IActivityPubClient.cs`**: `DeliveryResult` gained
  `MintedId` (parsed from the 2xx body's `id`) and `Body`; the interface/XML docs updated to the
  learned-id model.
- **`src/Iris.Server/Stores/IActivityStore.cs`** + in-memory/file-backed impls: new
  `GetAllActivitiesAsync(ct)` — enumerates all stored activities (needed because a minted id is
  unguessable, so a received Accept/Reject can't be located by a computed IRI; tests/lookups enumerate).
- **Samples** (`SampleBlazorClient/…/ActorDetail.razor`, `ObjectPage.razor`): callers switched to the
  learned-id flow (read `DeliveryResult.MintedId` after an authoring call, pass it to the inverse
  call).

## Test changes (driving the suite to green)

The pre-055 tests built id-bearing helpers and asserted the deterministic formula. Under 055 the helpers
are **id-less** and the tests **learn the minted id** from the 2xx body / the stored object. Key pattern:
a `LearnMintedIdAsync(HttpResponseMessage)` helper parses the 202 body's `id`. (The `Iri` type is a
class; the non-null-asserted id is bound via `Iri x = nullableIri.Value;` — the `!` null-forgive produced
CS1503/CS0266 in this codebase.)

- **`tests/Iris.Server.Tests/BlocksCollectionIntegrationTests.cs`**, **`FlagsCollectionIntegrationTests.cs`**:
  `Unblock_AfterBlock` / `Unflag_AfterFlag` capture `blockResult.MintedId` / `flagResult.MintedId` and
  pass the learned id to `UnblockAsync`/`UnflagAsync`.
- **`tests/Iris.Server.Tests/CommunityOutboxPublishIntegrationTests.cs`** (8 tests): `BuildFollow`/
  `BuildUndo` id-less; `BuildUndo(Iri actor, Iri originalFollowId)` takes the learned follow id; each
  test learns the minted id via `LearnMintedIdAsync`.
- **`tests/Iris.Server.Tests/OutboxPublishServerDeliversIntegrationTests.cs`**: `BuildBlock` id-less;
  the Block test learns the minted id for the outbox comparison.
- **`tests/Iris.Server.Tests/MutualFollowDeliveryLoopIntegrationTests.cs`**: `BuildCreate` id-less
  (Create + embedded Note); the loop test learns the minted Create id. `RedeliveredCreate_IsRecordedOnce`
  builds a **directly-delivered inbound** Create *with* a deterministic id (inbound federation keeps the
  originator's id; the inbox handler requires one) — distinct from the outbox-publish (id-less) path.
- **`tests/Iris.Server.Tests/Security/FederationSignatureIntegrationTests.cs`**: added
  `FindAcceptForObjectAsync(persistence, followIri)` — locates the stored Accept **by its object ref**
  (the Accept's id is now minted/unguessable, so it can't be computed); `Follow_ThenAccept` and
  `Follow_TwoActors_AcceptIsSigned…` use it via `WaitForAsync`.
- **`tests/Iris.Server.Tests/Delivery/FollowEdgeConvergenceIntegrationTests.cs`**: `BuildFollow` id-less;
  `BuildUndo(Iri actor, Iri originalFollowId)`; added `FindFollowIriInOutboxAsync` — reads the actor's
  outbox to learn the minted follow id (the `DeliveryService.DeliverAsync` used by the test returns void,
  so the id is learned from the stored outbox rather than a `DeliveryResult`).
- **`tests/Iris.Client.Tests/ActivityPubClientTests.cs`**, **`UnlikeDeliveryTests.cs`**,
  **`DeleteDeliveryTests.cs`**: assertions on the posted body changed from "deterministic id == formula"
  to "no `id` present" (the client no longer sets ids). `UnlikeAsync_ReferencesTheMintedLikeIdLearnedFromLikeAsync`
  and `DeleteAsync_ReferenceIsThePostedNoteIri` now simulate the round-trip: the fake returns a 202 body
  carrying the created object, the test learns the minted id (from `DeliveryResult.MintedId` / the body's
  `object`), and asserts the Undo/Delete references that learned id.

## Verification

- `dotnet build` — clean, 0 warnings / 0 errors (TreatWarningsAsErrors on).
- `dotnet test` — **1,111 tests, 0 failed** across the three suites: `Iris.Core.Tests` 210,
  `Iris.Client.Tests` 135, `Iris.Server.Tests` 766 (was 5 failing / 761 passing at the start of 20.0).
- No stray diagnostics in `ActivityPubServerExtensions.cs` / `SignatureValidationMiddleware.cs` (the
  temporary `/home/ubuntu/opencode_diag/` logging added during the 202-body and minted-id investigations
  was removed; `SignatureValidationMiddleware.cs` is byte-identical to before).

## Deployment note

The two 055 production bugs found and fixed during bring-up: (1) the outbox 202 body was serialized as a
JSON *string* literal (quoted), so the client's `MintedId` parse returned null — fixed with
`Results.Text(json, …)`; (2) `MintActivityIds`'s in-place mutation of the embedded `Create.Object` list
did not persist through the serializer (and clobbered a client-set embedded id) — fixed by building a new
list, reassigning `Create.Object`, and only minting the embedded object when its id is null/empty.
