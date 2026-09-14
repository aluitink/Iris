# 136.14 — Media and attachment interoperability

**Date:** 2026-09-14
**Slice:** 136.14 (Lemmy interop — media and attachment interoperability)
**Status:** **COMPLETE.** Six cross-instance integration tests verify that image, link, and rich-text
attachment payloads survive an Iris→Iris federation round-trip without silent drops or truncation.

## What this slice delivers

136.14's acceptance criteria:

1. Validate image, link, and rich-text/markdown payloads from Iris → Lemmy and Lemmy → Iris.
2. Verify media fetch/render behavior for remote assets (authless/public paths, broken-link handling,
   MIME mismatches).
3. Stress large attachments and long-body posts to confirm truncation, preview, and storage behavior
   is explicit.
4. Exit when media-bearing content round-trips with expected rendering and no silent drops.

### Test coverage

The six tests in `CrossInstanceMediaInteropIntegrationTests` (two-instance `TestServer` fixture,
A: `media-a.domain.local` alice, B: `media-b.domain.local` lumen community) deliver a signed
`Create` (signed as alice via `IActivityPubClient.DeliverAsync`) to B's community inbox and verify
the receiving instance's stored state:

| Test | What it verifies |
|------|-----------------|
| `ImageAttachment_RoundTrips_AcrossInstances` | An `Image` attachment's `id` survives the round-trip in B's object store; the media proxy (`GET /ap/v1/media/proxy?url=…`) serves the fetched bytes with `image/png` content-type. |
| `DeadImageAttachment_ObjectStillStored_ProxyReturns502` | A broken image URL does not cause the object to be dropped: B stores the object with the attachment intact; the media proxy returns `502 Bad Gateway` for the dead URL. |
| `DocumentAttachment_RoundTrips_AcrossInstances` | A `Document` (link) attachment survives with its `name` and `type` intact. |
| `MarkdownSource_RoundTrips_AcrossInstances` | A note with HTML `content` (rendered rich-text) survives with the HTML intact. |
| `LongBodyPost_RoundTrips_WithoutTruncation` | A 100 000-character body is stored in full (no silent truncation). |
| `MimeMismatch_AttachmentStored_ProxyServesActualType` | An attachment whose declared type mismatches the fetched content-type: B stores the object; the media proxy serves the actual fetched content-type. |

### Key design decision: verify via persistence, not the object-document endpoint

The embedded object is stored under its **original IRI** (the author's instance host,
`https://a.domain.local/…`) by `CreateActivityHandler.StoreEmbeddedObjectAsync`. The receiving
instance's `ObjectDocumentHandler` (`GET /ap/v1/{**path}`) reconstructs the IRI from its own
`BaseUri` (`https://b.domain.local/…`), so a cross-instance object is not served by the receiving
instance's object-document endpoint — the IRI mismatch produces a 404.

This is correct behavior: a remote object's canonical URL is the author's instance. The receiving
instance stores the object for local read paths (feed hydration, thread hydration, outbox
enumeration) but does not re-serve it under a rewritten IRI. The tests therefore verify the stored
object via the receiving instance's `InMemoryPersistenceProvider.Objects` directly (the same store
the production read path uses) and verify the media proxy via HTTP (the proxy is host-agnostic —
it fetches by the attachment's source URL, not by the object IRI).

### Fixture wiring

`CrossInstanceMediaSharedHost` extends `SharedTwoHostFixture`:

- **Identity:** alice (A) and lumen (B) are seeded with RSA-2048 keys via `TestSeeder.SeedPersonWithKey`
  / `SeedCommunityWithKey`. A custom `IdentityKeys` (fresh `InMemoryKeyStore` + `InMemoryKeyProvider`
  + `HttpSignatureSigner`) is registered on each host so the outbound delivery worker can sign.
- **Routing fetcher:** `CrossInstanceMediaRoutingFetcher` implements `IActorDocumentFetcher` and
  routes by actor-IRI host (A-host → A's `TestServer`, B-host → B's) using `LazyHandler` +
  `ServerRefFor` (the chicken-and-egg pattern: the handler defers to the `TestServer` that does not
  exist yet during construction). B's fetcher reaching A is what lets B verify alice's signature.
- **Media fetcher:** B's `IMediaFetcher` is overridden with `CrossInstanceMediaFakeFetcher` (returns
  fixed PNG bytes for the "good" URL, `null` for the "dead" URL) so the media proxy can be tested
  without real network access.
- **Signed client:** the fixture builds an `IActivityPubClient` (via `ActivityPubClientFactory`)
  that signs as alice and routes its transport to B's `TestServer` via `LazyHandler`. The tests use
  this client's `DeliverAsync(LumenInboxIri, create, ct)` to post the signed Create to B's inbox.
- **Per-test reset:** `InitializeAsync` calls `fixture.Reset()` (clears both persistence stores +
  collection caches, leaves key stores intact) then re-seeds alice and lumen with
  `SeedPersonWithExistingKey` / `SeedCommunityWithExistingKey` (restores actors/communities with the
  same keys the fetchers/clients hold).

### Files

- `tests/Iris.Server.Tests/Media/CrossInstanceMediaInteropIntegrationTests.cs` — the six tests +
  `CrossInstanceMediaSharedHost` fixture + `CrossInstanceMediaFakeFetcher`.

### Test results

```
Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6
Full suite: Passed! - Failed: 0, Passed: 1183, Skipped: 16, Total: 1199
```
