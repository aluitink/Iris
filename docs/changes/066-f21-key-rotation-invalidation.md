# 066 — F-21 key-rotation invalidation — closes F-21

> 2026-08-29 · Slice 12.21 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes gap **F-21** (no key-rotation invalidation path — an *operational* gap, not a spec item). When a
remote actor **rotates** its key — a new RSA/Ed25519 key published at the **same key IRI** (`keyId`) in its
actor document — the receiving instance's `RemoteKeyCache` keeps serving the **old** public key until its 1h
TTL (or a manual `?refresh=true`). In that window, a validly-signed inbound request **fails verification and
is rejected (401)** even though the sender's new key is correct.

The fix is an **invalidate-on-failure** path in the `HttpSignatureValidator`: a verification *failure*
(the signature does not verify against the resolved key) is treated as the rotation signal. The validator
invalidates the signing `keyId`'s entry in the `RemoteKeyCache` **and** the owning actor's entry in the
`RemoteActorCache`, then **re-resolves once** and **re-verifies**. If the rotation is real, the re-resolve
re-fetches the actor document, picks up the new key, and the request is accepted on first contact.

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/HttpSignatureValidator.cs` | Gained two *optional* ctor params (`RemoteKeyCache?`, `RemoteActorCache?`). On a verification **failure** (distinct from a *missing* key), invalidates the `keyId` in the key cache **and** the owning actor IRI in the actor cache, then re-resolves once and re-verifies via a `VerifyAndDispose` helper. A *missing* key is not a rotation signal (no invalidation, no re-resolve). Added `OwnerActorIriFromKeyId(Iri)` (strips the `#fragment` from the `keyId` to recover the actor IRI). |
| `src/Iris.Server/RemoteKeyCache.cs` | `sealed class` → `public class`; `virtual bool Invalidate(Iri)`. Unsealed + virtual so a host/test can extend it; the DI-registered instance is the same instance the fetcher reads through. |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | The `ISignatureValidator` DI factory now injects the `RemoteKeyCache` and `RemoteActorCache` singletons (both optional at the validator's ctor — a validator without them keeps the original single-attempt behavior). |
| `tests/Iris.Server.Tests/KeyRotationInvalidationTests.cs` (NEW) | 5 unit tests covering the failure→invalidate+re-resolve path, the missing-key non-signal, the success no-invalidate, and the no-cache constructor path. |
| `tests/Iris.Server.Tests/KeyRotationFederationIntegrationTests.cs` (NEW) | 1 end-to-end test: a rotated remote key (same key IRI) is accepted on first contact across two live `TestServer`s. |

## Tests

841 → **847** (+6):

- `tests/Iris.Server.Tests/KeyRotationInvalidationTests.cs` — 5 new unit tests. Each drives the real
  `HttpSignatureValidator` with a scripted `ISignatureVerifier` and a recording fake `RemoteKeyCache` /
  `RemoteActorCache`. Coverage: a verification **failure** invalidates the `keyId` (key cache) **and** the
  owning actor IRI (actor cache) and re-resolves **once** (the re-resolved key is the one used to verify, and
  the stale cache is empty afterward); a **missing** key (no resolvable public key) does **not** invalidate
  or re-resolve; a **successful** verification does not invalidate either cache; and the **no-cache**
  constructor (neither cache supplied) still verifies and does not crash (the single-attempt behavior is
  preserved).
- `tests/Iris.Server.Tests/KeyRotationFederationIntegrationTests.cs` — 1 new end-to-end test (two `TestServer`
  instances, mirroring `FederationSignatureIntegrationTests`). Instance A hosts `alice`; instance B hosts
  `bob`. A signed `Follow` (alice's **original** key) is delivered to B and accepted (**202**), **warming
  B's caches** (B fetched A's actor document and cached alice's key under its key IRI). `alice` then
  **rotates her key** — a new RSA key at the **same key IRI**, republished in A's actor document (and A's
  `LocalActorDocumentCache` invalidated so B's re-fetch reads the rotated doc, not the cached original). A
  `Follow` signed with the **rotated** key is delivered to B and **accepted (202)**: B's validator fails
  verification against the stale cached key, invalidates the key + actor-doc entries, **re-fetches A's actor
  document** (now the rotated key), re-verifies successfully, and stores the follow. Without F-21 this second
  delivery would be **401** (the stale key is served until the 1h TTL).

## Decisions

- **A verification *failure* is the rotation signal — a *missing* key is not.** A missing key (no resolvable
  public key for the `keyId`) is a different failure mode (the actor is unknown / not yet fetched / the key
  IRI is unresolvable) and re-resolving would just re-fetch the same missing document. Only a *failure* — the
  key *was* resolved but the signature does not verify — indicates the cached key is stale. This avoids
  thrashing the caches for every unresolvable sender.
- **Invalidate the actor-doc entry, not just the key entry.** The re-resolve re-derives the key by
  *re-fetching the actor document* (the `IInboundKeyResolver` reads the actor doc through the
  `RemoteActorCache`). If only the key cache were invalidated, the re-resolve would re-read the **stale
  cached actor document** and re-derive the **old** key — defeating the rotation. So the owning actor's
  document entry must be invalidated too. The owning actor IRI is the `keyId` with its `#fragment` stripped
  (ActivityPub `keyId = actorIri + "#key-N"`).
- **Re-resolve at most once.** A single re-resolve + re-verify bounds the work on a failure path and prevents
  a retry loop (a second failure is surfaced as a 401, as before). At-least-once delivery (C-07) means a
  genuinely-rotated request that still fails will be retried by the sender and succeed on a later attempt.
- **Optional caches — backward compatible.** The `RemoteKeyCache` / `RemoteActorCache` ctor params are
  optional (default `null`). A validator constructed without them (as in most existing unit tests) keeps the
  original single-attempt behavior exactly. The DI factory supplies both in the real server, so production
  gets the invalidation path automatically.
- **The test rotates the key at the *same* key IRI.** The whole point of F-21 is the case where the actor
  keeps the same `keyId` and only the key material changes — the realistic rotation a server performs
  (re-sign the `publicKey` with a fresh key at the unchanged IRI). The test's `RotateAliceKeyInActorDoc`
  re-publishes the actor document with the new `publicKeyPem` under the same `id`. A `?refresh=true` actor
  doc fetch on A confirms the rotated doc is served (the re-fetch reads the new key).

## Result

**F-21 is resolved.** A rotated remote key (same key IRI, new material) is now accepted on first contact: the
stale cached key + actor doc are invalidated on the verification failure and re-fetched, so a validly-signed
inbound request is no longer rejected (401) for up to an hour after a rotation. A federation rotation test
locks the behavior in.
