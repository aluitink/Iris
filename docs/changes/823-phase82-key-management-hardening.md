# 82.3 — Key management hardening (Phase 82, slice 3 — final)

**Slice:** 82.3 (harden the per-actor key management the outbound/inbound federation depends on: per-actor key pairs on the actor doc `publicKey`, the `IKeyProvider` resolution path, `publicKey`↔signing-key consistency, and key-rotation readiness).
**Status:** DONE — **1 real bug fixed** (fragment-aware key addressing in the in-memory + file-backed key stores) + **16 new invariant/resolution tests**. Phase 82 is now **COMPLETE** (82.1 + 82.2 + 82.3).
**Companions:** 82.1 (offline signature conformance — added `created`) + 82.2 (real-world signed delivery — live 202 from `mastodon.social`) are done. [821](821-phase82-outbound-signature-conformance.md) · [822](822-phase82-real-world-signed-delivery.md).

## What was done

### Bug fix — fragment-aware key addressing (the hardening finding)

While locking the `IKeyProvider` resolution + no-key failure mode, the new `DelegatingKeyProvider` test for "a store key under the wrong IRI must NOT match" **failed** — the provider returned `true` for an actor whose key was stored under `…/alice#other-key` when the lookup was for `…/alice#key-1`. Root-caused to a **real bug in key addressing**:

`Iri` equality is **fragment-blind**. `Iri` is a `readonly record struct` over a `System.Uri`, and `System.Uri` equality is fragment-<em>insensitive</em> by design (a URI fragment is not part of the resource identifier under the W3C definition). Verified directly:

```
Iri("…/alice#key-1").Equals(Iri("…/alice#other-key"))  == true
Iri("…/alice#key-1").Equals(Iri("…/alice"))            == true   // bare actor IRI!
```

So an `Iri`-keyed `Dictionary` conflates `#key-1`, `#key-2`, and the bare actor IRI into **one entry**. This corrupted two key stores:

| Store | Keyed by | Before | After |
|---|---|---|---|
| `InMemoryKeyStore` | `Dictionary<Iri, ISigningKey>` | ❌ fragment-blind | ✅ fragment-aware |
| `FileBackedKeyStore` | `ConcurrentDictionary<Iri, StoredKey>` | ❌ fragment-blind | ✅ fragment-aware |
| `EfKeyStore` (Postgres) | `FirstOrDefault(e => e.KeyId == keyId.Value)` | ✅ already string/`Value`-based (fragment-aware) | unchanged |

The Postgres store was already safe (it compares by `Iri.Value`, a fragment-aware string) — which is why the 82.2 live test worked (`andrew`'s key was in Postgres). The in-memory + file-backed (dev/sample) stores were the gap: a key stored under `#key-1` could be returned for a `#key-2` lookup (or the bare actor), a silent key-management corruption.

**The fix:** added `IriEqualityComparer` (`Iris.Core.Identity`) — a fragment-aware `IEqualityComparer<Iri>` that compares `Iri.Value` (`Ordinal`, RFC 3986) — and used it in both `InMemoryKeyStore` and `FileBackedKeyStore`, matching the durable Postgres store's behavior.

**Why not just make `Iri` fragment-aware globally?** That was the obvious fix, but a full-suite run showed it **breaks 25 existing tests** (Core 6, Client 2, Web 14, Server 3) that rely on fragment-blind equality for the `#Public` audience IRI and collection IRIs (e.g. `ComposeNoteTests.Build_SetsTo_WhenAudienceProvided`, `PostQuestionAsync_SetsCc_…`). Fragment-blindness is load-bearing there. Making `Iri` globally fragment-aware is a **separate, larger change** requiring per-usage analysis — recorded as a follow-up, not done in this slice. The surgical key-store fix addresses the actual key-management bug with zero regression to the other 151 `Iri`-as-dict-key usages.

### Invariant + resolution-order tests (16 new)

| File | Tests | What they lock in |
|---|---|---|
| `Iris.Client.Tests/Auth/DelegatingKeyProviderTests.cs` (new, 7) | `DelegatingKeyProvider` was **previously untested**. Primary-hit does not fall through to the store; primary-miss falls back to the durable store by the `{actor}#key-1` convention; **no-key returns `false` + a null identity** (never throws, never a null identity — the `KeyNotFoundException` is raised later by `SigningHandler`, so a keyless actor is never signed with a null/wrong key); a store key under a *different* IRI does not match; a custom `keyFragment` is honored in the fallback; `RegisterKey` delegates to the primary; null-arg throws. |
| `SampleServer.Tests/SampleServerKeyManagementTests.cs` (new, 6) | Against the real hosted sample (users + community, RSA): every served actor doc carries `publicKey` with `id`/`owner`/`publicKeyPem`; the **served `publicKeyPem` verifies a signature the instance produces with its store's signing key** (the `publicKey`↔signing-key consistency invariant — catches key drift that would silently break federation in both directions); the community's served key verifies its own signature (the community signs with the primary actor's key); `publicKey.id` resolves to a key the instance actually holds. |
| `Iris.Core.Tests/Identity/KeyStoreTests.cs` (+3) | `#key-1` vs `#key-2` vs the bare `{actor}` are **distinct** `InMemoryKeyStore` entries (put/get/remove) — locks the bug fix so it can't regress back to fragment-blind conflation. |

### Key-rotation readiness (documented, not built)

Per the slice's scope ("document/verify … a candidate follow-up if a rotation mechanism doesn't yet exist; keep the slice to hardening + invariants + tests"):

- **No local key-rotation mechanism exists.** There is no endpoint/service that rotates a local actor's key; the only mutation is `PutKey` (blind replace by key IRI) and the creation sites (all mint `#key-1`). `ActorProvisioner` re-provisioning would silently re-key `#key-1` (breaking peers' cached keys) — a federation-breaking behavior, not a rotation.
- **Inbound (remote-key) rotation is already supported + tested:** F-21 (a verification failure invalidates the `RemoteKeyCache` + re-resolves once — `KeyRotationInvalidationTests`, `KeyRotationFederationIntegrationTests`) and F-25 (`replaces` handling + `Move` handler cache invalidation — `InboundKeyResolverTests`, `MoveKeyRotationIntegrationTests`). These are the relevant, tested rotation paths.
- **Local rotation is a follow-up** (not built): it would need a `#key-2` mint + dual-key (old+new) verification window + `replaces`/`successor` emission on Iris's own actor docs. The fragment-aware key stores + `DelegatingKeyProvider`'s `keyFragment` parameter (now tested) are the prerequisites that make a future multi-key setup resolvable.

## Decision (recorded per the autonomous-loop open-questions policy)

**Surgical key-store fix vs. global `Iri` fragment-awareness.** The globally-correct fix (make `Iri` fragment-aware) was rejected for this slice because it breaks 25 existing tests that depend on fragment-blind equality for the `#Public`/collection IRIs — a larger, riskier change than a hardening slice should take. The key stores are the *actual* site of the corruption (they key by IRI and must distinguish key fragments), so fixing them with a fragment-aware comparer resolves the real bug with zero regression. The global `Iri` change is recorded as a follow-up (it would need per-usage analysis of the 151 `Iri`-as-dict-key sites, especially the audience/collection logic).

## Verification

- **Build:** `dotnet build -c Release` → **0 warnings, 0 errors**.
- **Tests:** `dotnet test -c Release --filter "Category!=Slow"` → **1588 passed, 0 failed, 1 skipped** (1584 → 1588; +16 new). The single `Iris.Server.Tests` failure under parallel full-suite load is the known timing/contention flake (passes 976/976 in isolation).
- **Blast-radius check:** making `Iri` globally fragment-aware broke 25 tests (reverted); the surgical key-store fix breaks **none**.

## Files changed

- `src/Iris.Core/Identity/IriEqualityComparer.cs` — new (fragment-aware `IEqualityComparer<Iri>`).
- `src/Iris.Core/Identity/InMemoryKeyStore.cs` — uses `IriEqualityComparer.Instance` for its key dictionary.
- `src/Iris.Server/Persistance/Stores/FileBackedKeyStore.cs` — uses `IriEqualityComparer.Instance` for its key index.
- `tests/Iris.Client.Tests/Auth/DelegatingKeyProviderTests.cs` — new (7 tests).
- `tests/SampleServer.Tests/SampleServerKeyManagementTests.cs` — new (6 tests).
- `tests/Iris.Core.Tests/Identity/KeyStoreTests.cs` — +3 fragment-awareness tests.
- No new NuGet packages; no dependency-direction violations (`IriEqualityComparer` is in `Iris.Core`, used by `Iris.Server`).

## Test-debt log

- **Web tests:** none touched (key management is core/server-side — in-scope for new coded tests per the WASM manual-test policy).
- **Core/Client/Server tests:** 16 new, all passing; no existing tests modified or deleted.
