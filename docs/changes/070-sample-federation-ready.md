# 070 — Phase 8 S1: federation-ready SampleServer + rich seed

> 2026-08-29 · Phase 8 (Sample) · Slice S1

## What was built

The `SampleServer` sample is now a **federation-ready** ActivityPub instance with a **rich seeded
graph**, so both the full client pipeline and the inbound federation path can be exercised against a
single, running in-process instance. `CreateWebHostBuilder` remains the single composition root (the
existing `SampleServer.Tests` + `SampleBlazorClient.Tests` keep building against it unchanged).

## Key types & files

| Area | Change |
|---|---|
| `SampleServer.Program` | Rich seed: three actors (alice/bob RSA local, **carla Ed25519 on the remote host label** `remote.example`), a `Group` community (`/ap/v1/c/iris`, members alice+bob, follows carla), follow edges, per-actor outbox notes, a bob→alice reply, and a carla→alice note like (with the corresponding `ILikeStore` edge). Per-actor Basic-auth credentials (handle : shared password); the actor-doc handler unlocks the `privateKey` extension for the acting actor. |
| `SampleServer.LocalActorDocumentFetcher` | New `IActorDocumentFetcher` that serves **local-host** actor documents from the sample's own in-process store instead of over the network. A remote-host IRI (carla) is *not* served — the sample has no knowledge of true remote actors, exactly as a real instance would for an unknown host. Registered as the DI `IActorDocumentFetcher` so the inbound key resolver resolves seeded local senders in-process. |
| Inbound federation wiring | `UseSignatureValidation()` enables the signature middleware (unsigned inbox POST → 401); `ISignatureSigner` is registered explicitly (`AddActivityPubServer` only registers the *inbound* verifier/resolver/fetcher, not the client-side signer). Every seeded key is registered with `IKeyProvider` so the client pipeline can sign as any seeded actor. |
| `SeedMetadata` / helpers | `SeedSampleData` returns the seeded IRIs + key IRIs (`GetSeededKeyIri`, `ActorIriFor`) so tests can target a specific actor without re-deriving the IRIs. |

## What was deliberately kept / bounded

- **Carla is a remote-host stand-in, not a federable peer.** Her IRI is on `remote.example` so the
  seeded follow edges *read* as cross-instance federation, but the sample's inbound key resolver cannot
  resolve her key (no network fetch to an unknown host) — a signed delivery *from* carla would be
  rejected. This is the honest federation boundary, and it is asserted by a test. True two-instance
  interop (instance→instance + instance→external) is the compose/smoke path (S9–S10).
- **No new NuGet packages.** Ed25519 uses the existing BouncyCastle-backed `Ed25519Key`; the local
  fetcher is ~30 lines over the existing `IPersistenceProvider`.

## Tests

`tests/SampleServer.Tests` gained **8** new facts (10 → 18) in `SampleServerFederationTests`:

| Test | Proves |
|---|---|
| `InboxPost_UnterminatedSignature_Returns401` | Unsigned inbox POST is rejected (signature validation is on). |
| `InboxPost_SignedFollow_IsAcceptedAndRecorded` | A signed RSA follow from alice → bob is accepted (202) and the follow edge is recorded — the full inbound pipeline. |
| `ActorDoc_SecondActor_AuthenticatesWithOwnHandle` | bob authenticates with his own handle and unlocks his `privateKey`. |
| `RemoteHostActor_IsNotResolvable_LikeARealRemote` | carla's (remote-host) key is unresolvable by the inbound resolver — the honest boundary. |
| `Seed_Follows_AreRecorded` | The seeded alice↔bob and alice↔carla follow edges are present. |
| `Community_FollowsRemoteActor_AndHasMembers` | The community follows carla and has alice+bob as members. |
| `Seed_Reply_And_Like_AreStored` | The seeded reply (bob→alice) and like (carla→alice note) edges are recorded. |
| `InboxPost_SignedFollow_FromEd25519LocalActor_IsAccepted` | A signed **Ed25519** follow from bob → alice is accepted — the non-RSA verification path, end to end. |

Full-solution build **0 warnings / 0 errors**; all tests green. SampleServer.Tests: **10 → 18**.

## Decisions

- **Local fetcher serves only local-host IRIs.** The first cut served *every* seeded actor (including
  carla) by remapping to the local base, which made the remote-host case artificially resolvable and
  masked the real federation boundary. Scoping to local-host IRIs makes carla behave like a true remote
  actor (unresolvable), which is the honest and testable boundary.
- **Ed25519 delivery test swaps the stored doc key.** The seed gives bob an RSA key; the test generates a
  fresh Ed25519 key for bob, registers it with `IKeyProvider`, and *replaces bob's stored
  `publicKeyPem`* with the Ed25519 PEM so the inbound resolver (which reads bob's local document)
  resolves the Ed25519 key — exercising the non-RSA path without re-seeding.
