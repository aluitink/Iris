# 84.3 — Operator key-rotation endpoints

**Commit:** `08d2351`
**Status:** COMPLETE.

## The gap

Phase 84.2 built the `KeyRotationService` (`RotateAsync` + `RetireKey`) but it was only reachable as a
library `DI` service — an operator had **no way to trigger a rotation without a code change**. 84.3
exposes the service to an operator over HTTP, completing the local key-rotation lifecycle (82.3 → 84.2 →
84.3) with the two endpoints an operator actually needs.

## What was built

Two admin-gated endpoints, mapped under the versioned `/ap/v1` prefix (so the `Iris-Version` endpoint
filter applies) in `MapActivityPubEndpoints`:

- **`POST /ap/v1/keys/rotate`** (`KeyRotateHandler`) — rotates the **instance actor's** signing key by
  calling `KeyRotationService.RotateAsync(instanceActorId, ct)`. `200` with a JSON body
  `{ "actor": <actorIri>, "newKeyIri": <newKeyIri> }`. `404` when the instance actor is not in the actor
  store (`KeyNotFoundException` from `RotateAsync`).
- **`POST /ap/v1/keys/retire`** (`KeyRetireHandler`) — retires a specific key IRI by calling
  `KeyRotationService.RetireKey(keyIri)`. The key IRI is read from the JSON body (`{ "keyIri": "..." }`,
  matched **case-insensitively** via `JsonDocument`) or from a `?keyIri=` query param. `204` on success,
  `400` on a missing/blank `keyIri`, `404` when the key was not in the store.

**Admin gate (`AuthorizeInstanceActorAsync`)** — shared by both endpoints. The caller's authenticated
actor must be the **instance actor** (`ActivityPubServerOptions.InstanceActorId`):
- Basic auth is validated for the instance actor's IRI via the existing `IActorCredentialValidator`
  (the same validator the local-mute / outbox surfaces use).
- The Blazor WASM cookie (which cannot carry Basic auth) is honored via its `actor_iri` claim when it
  matches the instance actor's IRI (the same fallback `LocalMuteHandler` uses).
- When no instance actor is configured, no one can be authorized → `403`.
- An authenticated caller that is **not** the instance actor → `403` (authenticated, not authorized).
- No credential at all → `401`.

**Degraded mode (83.4)** — both endpoints check `IDegradedModeGate.IsDegraded` *after* the admin gate and
refuse the write with `503 application/problem+json` (a key-rotation write cannot be durably recorded
while the durable store is unreachable; the operator should retry later). The check order (auth → 401/403,
then degraded → 503, then business) matches the federation write surfaces (83.4).

**Error bodies** use `application/problem+json` via a small `Problem(statusCode, error, description)`
helper (the inline idiom the federation write surfaces use — there is no shared problem helper):
`{ "error": <title>, "description": <description> }`.

`KeysRouteSegment = "keys"` was added to `ActivityPubServerConstants`.

### Design decisions

- **Instance-actor scope (the `?actor=` option is deferred to 84.4).** The core operator action is
  rotating the *instance* actor (the actor the instance signs with). Rotating an arbitrary local actor is
  a follow-up (84.4) — it needs the same admin gate but a different target + the operator must be able to
  name the actor. Keeping 84.3 to the instance actor keeps the gate + the rotation logic minimal.
- **Body `keyIri` read case-insensitively (not a `[FromBody]` record).** The first attempt bound a
  `[FromBody] KeyRetireRequest` record; the minimal-API body binding did not map the operator's
  `keyIri` (lowercase) to the `KeyIri` property under the host's JSON options. Reading the body with
  `JsonDocument` + a case-insensitive property match is robust to the operator's casing and to an
  empty/invalid body (it falls through to `?keyIri=`), and avoids a record-binding footgun.
- **Auth is checked before degraded, before business logic.** A degraded instance still tells an
  unauthorized caller `401`/`403` (it does not leak "I'm degraded" to an unauthenticated client); only an
  *authorized* operator sees the `503`. This mirrors the inbox/outbox write surfaces (83.4).
- **`RetireKey` still disposes the key (84.2 behavior).** The endpoint inherits 84.2's contract: the
  retired key is removed + disposed from the store, but the already-advertised public key still lets a
  peer verify a pre-retirement signature.

## Tests

`tests/Iris.Server.Tests/Identity/KeyRotationEndpointIntegrationTests.cs` — **8 new integration tests**
(spun up via `ActivityPubHostFactory`; the factory's `Handle` *is* the instance actor, so
`InstanceActorId = https://{Host}/ap/v1/u/{Handle}`; a `BasicAuthCredentialValidator` keyed on the
instance actor's IRI + a `TestSeeder.SeedPersonWithKey` seed provides the `#key-1` signing key the
rotation rotates):

- **`Rotate_AuthenticatedAsInstanceActor_Returns200AndNewKeyIri`** — `200`; the body carries the actor IRI
  + `newKeyIri` = `#key-2`; the new key is in the (shared) key store; the **old** key remains (overlap
  window); the actor document advertises the new key + a `replaces` = old key IRI.
- **`Rotate_Unauthenticated_Returns401`** — no `Authorization` header → `401`.
- **`Rotate_WrongCredentials_Returns403`** — a presented-but-rejected credential → `403` +
  `application/problem+json`.
- **`Rotate_DegradedMode_Returns503`** — `IDegradedModeGate.MarkDegraded()` → `503` +
  `application/problem+json`.
- **`Retire_KnownKey_Returns204AndRemovesIt`** — `204`; the key is removed from the store; a signature
  made *before* retirement still verifies against the already-advertised public key (captured as PEM
  before retirement, since `RetireKey` disposes the key).
- **`Retire_UnknownKey_Returns404`** — a `keyIri` not in the store → `404` + `application/problem+json`.
- **`Retire_MissingKeyIri_Returns400`** — an empty body (`{}`) → `400` + `application/problem+json`.
- **`Retire_Unauthenticated_Returns401`** — a valid body but no `Authorization` header → `401`.

## Suite impact

- `dotnet build -c Release` — 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test -c Release --no-build --filter "Category!=Slow"` — **1769 passed, 0 failed, 1 skipped**
  (Iris.Server.Tests 1009 → 1017). All 8 new tests pass; no regression.
