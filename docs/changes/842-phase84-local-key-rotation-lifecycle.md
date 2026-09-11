# 84.2 — Local key-rotation lifecycle

**Commit:** `36d667d`
**Status:** COMPLETE.

## The gap

Phase 82.3 hardened key management (fragment-aware key addressing, `DelegatingKeyProvider`, the
`publicKey`↔signing-key consistency invariant) and explicitly **documented local key-rotation as a
follow-up**: the platform could address `#key-1` / `#key-2` as distinct keys, and the read side
(`RemoteInboundKeyResolver`) already honors a `replaces` pointer to evict an old key, but there was no
**lifecycle to actually rotate** the local instance's signing key. An operator with no way to rotate a
leaked or end-of-life key had to hand-edit the actor document + key store and hope the signer picked up
the change.

## What was built

A single DI service, **`KeyRotationService`** (`Iris.Server.Identity`), with two methods:

- **`RotateAsync(Iri actorIri, CancellationToken ct)`** — rotates one local actor's signing key:
  1. Reads the actor document (throws `KeyNotFoundException` if no local actor is stored at that IRI).
  2. Determines the **current** key IRI via the single boundary point `actor.GetPublicKeyIri()` (falling
     back to `#key-1` for a legacy actor with no `publicKey` extension).
  3. Mints a fresh **RSA** key at the **next free fragment** — a bounded linear probe of `#key-2`,
     `#key-3`, … until it finds the first fragment not yet in the `IKeyStore` for that actor (the
     fragment-aware `Iri` (83.1) + fragment-aware key stores (82.3) make `#key-1`/`#key-2` co-exist as
     distinct entries).
  4. Stores the new key (`IKeyStore.PutKey`) and **re-registers the actor→new-key binding**
     (`IKeyProvider.RegisterKey`). The signer is already key-source-agnostic — it signs with whatever
     `IIdentity.KeyId` it is given — so **no signer change was needed**; the next outbound signature for
     this actor uses the new key.
  5. **Re-stamps the actor document's `publicKey` extension** with the new `id` + new `publicKeyPem` + a
     **`replaces`** pointer to the old key IRI (the read-side `RemoteInboundKeyResolver` already honors
     `replaces` to evict the old key).
  6. **Keeps the old key in the store** during the overlap window — old-key signatures still verify
     (fragment-aware, so `#key-1`/`#key-2` co-exist). Returns the new key IRI.
- **`RetireKey(Iri keyIri)`** — removes the old key from the store once the rotation is confirmed.
  `InMemoryKeyStore.RemoveKey` **disposes** the removed key (releasing its crypto resources), so no new
  signatures can be made with it; the **already-advertised public key** still lets a peer that
  fetched/cached the actor document verify signatures made before retirement. Returns `false` if the key
  was not in the store.

Registered in `AddActivityPubServer` as `services.TryAddSingleton<Identity.KeyRotationService>()`
(resolves the already-registered `IPersistenceProvider`, `IKeyStore`, `IKeyProvider` + a
`ILogger<KeyRotationService>`).

### Design decisions

- **The signer is untouched.** `KeyRotationService` only swaps the *binding* (actor → key IRI) and the
  *advertised* `publicKey`; the signer signs with whatever `IIdentity.KeyId` it resolves. This keeps the
  rotation logic out of the hot signing path and means every signer (delivery, operator actions) picks up
  the new key automatically.
- **Next-free-fragment probe, not a fixed `#key-2`.** Repeated rotations produce `#key-2`, `#key-3`, … —
  a bounded linear probe of `IKeyStore.TryGetKey`. The probe is bounded (a real actor holds a handful of
  keys), so it terminates.
- **Overlap window is the operator's call.** `RotateAsync` never retires the old key; `RetireKey` is a
  separate explicit step. An operator rotates, lets the new key propagate (peers re-fetch the actor
  document, learn the new key + `replaces`), then retires the old key once the overlap window has elapsed.
- **`replaces` is the eviction signal.** The `replaces` pointer names the old key IRI; the read-side
  `RemoteInboundKeyResolver` already uses it to evict a replaced key from its cache. So rotation is
  forward-compatible with the existing resolver — no resolver change.
- **`RetireKey` disposes the key (store behavior, not a rotation detail).** `InMemoryKeyStore.RemoveKey`
  disposes the removed `KeyPair` (it is `IDisposable`). This is correct store behavior (release crypto
  resources) — a test that holds a reference to the retired `KeyPair` must capture the public PEM *before*
  retirement to verify a pre-retirement signature (the key is gone after).

## Tests

`tests/Iris.Server.Tests/Identity/KeyRotationServiceTests.cs` — **4 new tests** (seeded via
`TestSeeder.SeedPersonWithKey`, which stores a real RSA key at `#key-1` + stamps the `publicKey`
extension; the rotation service uses the persistence's own key store so the service and the assertions
share one source of truth):

- **`Rotate_ProducesResolvableNewKey_AndActorDocumentAdvertisesIt`:** rotation returns `#key-2` (distinct
  from `#key-1`), the actor is re-bound to the new key (`IKeyProvider.TryGetIdentity`), the actor document
  advertises the new key IRI, and the `publicKey` extension carries a `replaces` = old key IRI.
- **`OverlapWindow_OldKeySignature_StillVerifies`:** a signature made with the original key *before*
  rotation still verifies *after* rotation — the old key remains in the store during the overlap window.
- **`RetireKey_RemovesOldKey_ButPreRetirementSignature_StillVerifies`:** after `RetireKey`, the old key is
  removed from the store, but a signature made before retirement still verifies against the
  already-advertised public key (captured as PEM before retirement, since `RetireKey` disposes the key).
- **`SecondRotation_UsesNextFreeFragment_Key3`:** a second rotation uses `#key-3` (the next free fragment);
  the actor is bound to + advertises the latest key.

## Suite impact

- `dotnet build -c Release` — 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test -c Release --no-build --filter "Category!=Slow"` — **1761 passed, 0 failed, 1 skipped**
  (Iris.Server.Tests 1005 → 1009). The new 4 tests pass in isolation; the one `FollowEdgeConvergence`
  failure seen under heavy parallel full-suite load is the known federation timing/contention flake
  (passes 1/1 in isolation, 16 s under load vs 1 s alone).
