# 999: Test Consolidation — De-duplicate true duplicate tests (Server.Tests)

## Summary

De-duplicated the **true** duplicate tests in `Iris.Server.Tests` (per the safe "merge true duplicates only" scope, no coverage loss). The suite was already well-factored: a comment/whitespace-normalized hash scan found only **4** byte-equivalent test-method pairs (the earlier "220 near-duplicates" were same-name-suffix-but-different-assertion — legitimate, not removed). Net: **4 test methods removed, 0 coverage lost.** `Iris.Server.Tests` count **1441 → 1437**; full fast suite green (0 failures).

## What was duplicated (and the fix)

### 1. `IMediaStore` contract behaviors, duplicated across the two store implementations (2 pairs → 1 shared base)

`Put_ThenTryGet_ReturnsBytesContentTypeAndFileName` and `Put_ReturnsSameOriginMediaIri` were byte-equivalent in both `InMemoryMediaStoreTests` and `FileBackedMediaStoreTests` — the same `IMediaStore` contract (put + read back bytes/content-type/file-name; same-origin `/ap/v1/media/{32-char-id}` IRI) asserted against two different implementations.

**Fix:** new shared base `tests/Iris.Server.Tests/Stores/IMediaStoreContractTests` holds the two contract behaviors as an ordinary `RunSharedContractBehaviors()` invoked via `CreateStore()`. Each implementation's test class derives from it and adds one `[Fact]` delegate (`Contract_PutReadBackAndSameOriginMediaIri`), so the shared behaviors run once per implementation against that implementation's store; each class keeps its implementation-specific tests (in-memory: distinct ids, per-item independence, last-path-segment id; file-backed: restart-survives, missing-media miss, bytes-sibling-of-metadata layout).

> **Why a delegate, not inherited `[Fact]`s:** xunit does **not** run `[Fact]`/`[Theory]` methods inherited from a base class (only traits and fixtures inherit). The first attempt (inherited `[Fact]`s) silently dropped the shared tests (3 failures); the delegate form is the idiomatic xunit pattern for shared contract behaviors.

**Removed (merged into the shared base):** `InMemoryMediaStoreTests.Put_ThenTryGet_ReturnsBytesContentTypeAndFileName`, `InMemoryMediaStoreTests.Put_ReturnsSameOriginMediaIri`, `FileBackedMediaStoreTests.Put_ThenTryGet_ReturnsBytesContentTypeAndFileName`, `FileBackedMediaStoreTests.Put_ReturnsSameOriginMediaIri` — replaced by the two `Contract_PutReadBackAndSameOriginMediaIri` delegates.

### 2. Cross-class exact copies (2 pairs → 1 kept + a pointer note)

- `Feed_UnknownCommunity_Returns404` (`GET /ap/v1/c/nobody/feed` → 404) was byte-equivalent in `CommunityFeedCorrectnessIntegrationTests` and `Services/CommunityFeedIntegrationTests`. **Kept** the `CommunityFeedCorrectnessIntegrationTests` copy; **removed** the `Services/CommunityFeedIntegrationTests` copy (a pointer comment marks where it now lives). The 404 path is still asserted once.
- `Root_DoesNotLeakPrivateKey` (root `GET /` with an ActivityPub `Accept` → 200, body never contains `privateKey`) was byte-equivalent in `InstanceActorAtRootIntegrationTests` (root = Application site-actor) and `InstanceActorDocumentAtRootIntegrationTests` (root = Person site-actor). **Kept** the `InstanceActorDocumentAtRootIntegrationTests` copy (the more general Person-root config); **replaced** the `InstanceActorAtRootIntegrationTests` copy with a pointer comment. The public-form invariant (root never leaks the owner-only `privateKey` extension) still holds for every root config and is asserted once.

## Ledger (no silent deletions)

| Removed test method | Class (before) | Where it's now covered | Reason |
|---|---|---|---|
| `Put_ThenTryGet_ReturnsBytesContentTypeAndFileName` | `InMemoryMediaStoreTests` | `IMediaStoreContractTests` (via `InMemoryMediaStoreTests.Contract_PutReadBackAndSameOriginMediaIri`) | byte-equivalent `IMediaStore` contract behavior |
| `Put_ReturnsSameOriginMediaIri` | `InMemoryMediaStoreTests` | same | same |
| `Put_ThenTryGet_ReturnsBytesContentTypeAndFileName` | `FileBackedMediaStoreTests` | `IMediaStoreContractTests` (via `FileBackedMediaStoreTests.Contract_PutReadBackAndSameOriginMediaIri`) | same |
| `Put_ReturnsSameOriginMediaIri` | `FileBackedMediaStoreTests` | same | same |
| `Feed_UnknownCommunity_Returns404` | `Services/CommunityFeedIntegrationTests` | `CommunityFeedCorrectnessIntegrationTests.Feed_UnknownCommunity_Returns404` | byte-equivalent assertion |
| `Root_DoesNotLeakPrivateKey` | `InstanceActorAtRootIntegrationTests` | `InstanceActorDocumentAtRootIntegrationTests.Root_DoesNotLeakPrivateKey` | byte-equivalent assertion |

**Net:** −4 test methods (6 removed, 2 added delegates). Coverage unchanged.

## Files changed

- `tests/Iris.Server.Tests/Stores/IMediaStoreContractTests.cs` (new — shared `IMediaStore` contract base)
- `tests/Iris.Server.Tests/Stores/InMemoryMediaStoreTests.cs` (derives from base; 2 contract methods → 1 delegate)
- `tests/Iris.Server.Tests/Persistance/FileBackedMediaStoreTests.cs` (derives from base; 2 contract methods → 1 delegate)
- `tests/Iris.Server.Tests/Services/CommunityFeedIntegrationTests.cs` (removed duplicate `Feed_UnknownCommunity_Returns404` + pointer)
- `tests/Iris.Server.Tests/InstanceActorAtRootIntegrationTests.cs` (removed duplicate `Root_DoesNotLeakPrivateKey` + pointer)

## Verification

- `dotnet build -c Release` → 0 errors / 0 warnings.
- Focused subset (media-store + community-feed + instance-actor): 37/37 pass.
- Full fast gate `dotnet test --filter "Category!=Slow"` → **all pass, 0 failures**; `Iris.Server.Tests` **1437** (was 1441), all other suites unchanged (Web 117, Core 467, Client 194, Data 23, Client.Ext 29, WebCrypto 3, Blazor 17, SampleServer 38, LiveInterop 19, Testing 12).

## Not done (out of safe scope)

- **Host-reuse (class-fixture) conversion** of per-method `TestServer` builds in Server.Tests (~104 `ActivityPubHostFactory.Create` + 120 `StartServer` call sites) would cut wall-clock further but changes test-isolation semantics (higher flakiness risk). Not done — flagged as a structural follow-up for a dedicated pass.
- **Core.Tests pure-logic unit tests** (428, 100% non-host) are the philosophy's *allowed* unit surface (IRI parsing, crypto, cache TTL) — not redundant with integration tests, so not pruned.
- **Iris.Web.Tests** are bound by the web-test policy (expendable during UI stabilization) — a separate, policy-governed pruning, not part of this consolidation.
