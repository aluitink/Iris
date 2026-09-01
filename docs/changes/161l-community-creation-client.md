# 161l — Client create-community one-call method: `CreateCommunityAsync` (19.5.1 / 19.6.1)

## Summary

Phase 19.5.1 (community-creation surface) + 19.6.1 (management via ActivityStream only): the client gains
the one-call **create-community** operation — `CreateCommunityAsync` — and the server gains the matching
**community-creation write path** (materialize a community from a `Create` of a `Group` published to the
creator's outbox). This **resolves the create-community deferral** recorded in change 161k: community
creation is now expressible as a genuine AP-native ActivityStream `Create` (a person authors a `Create`
whose embedded object is a `Group` and publishes it to their own outbox), so it fits the 19.6.1 invariant
that *every management operation is a one-call client method* (no side channel).

## The 161k deferral, and why it is now resolvable

Change 161k deferred create-community because it appeared to have **no federated ActivityStream activity
type and no server route** to accept one — a community cannot publish a `Create` of itself to its own
not-yet-existent outbox (chicken-and-egg), and there was no bootstrap route. The deferral missed that the
`Create` does **not** have to be authored by the community: a **person** (the operator) can author a
`Create` of a `Group` to **their own** outbox. The server's existing outbox-publish handler already
processes `Create` activities (it stores the embedded object and fans out to followers); it simply did not
recognize that the embedded object *is a community to materialize*. This slice adds that recognition. No
non-AP activity is invented — a `Create` of a `Group` is a perfectly valid ActivityStream activity (a
person creates a community object).

## What changed

### `IActivityPubClient` / `ActivityPubClient`

- **`CreateCommunityAsync(Iri actorId, string name, string displayName, CancellationToken ct)`** — builds
  a `Create` whose embedded object is a `Group` (IRI `{instanceBase}/ap/v1/c/{name}`, derived from the
  origin of `actorId`; `preferredUsername = name`, `name = displayName`) with `actor = actorId` and a
  deterministic IRI `{actorId}/creates/community-{name}`, and publishes it to `actorId.OutboxOf()` through
  the signed `DeliverAsync`.
  - **Outbox-publish pattern (the AP-native convention).** Unlike the membership methods (which post
    directly to the community's inbox because the community outbox publish endpoint accepts only
    Follow/Undo/Accept/Reject), community creation is a `Create` authored by a *person* to their *own
    outbox* — the chicken-and-egg of a community publishing to its own (not-yet-existent) outbox is
    avoided by having the creator's outbox carry the `Create`. The deterministic Create IRI makes a
    repeated create of the same community a no-op re-store (idempotent by IRI).

### Server: the community-creation write path (`ActivityPubServerExtensions`)

The outbox-publish handler's `Create` arm (`RecordCreateLocalAsync`) now recognizes a local community:

- **`RecordCreateLocalAsync`** gains a `baseUrl` parameter. After storing the embedded object (the
  existing note/reply path is unchanged), if the embedded object is a `Group` whose IRI is this
  instance's `{base}/ap/v1/c/{name}` (validated by `TryParseLocalCommunityIri`), it calls
  `StoreCreatedCommunityAsync`.
- **`TryParseLocalCommunityIri(baseUrl, groupIri, out communityIri)`** — reports whether a `Group` IRI is
  a *local* community IRI (same host as `baseUrl`, single path segment, no query/fragment). A `Group` on a
  foreign host (a remote group) is left as a plain object-store entry (not materialized as a local
  community).
- **`StoreCreatedCommunityAsync(persistence, group, communityIri, ct)`** — materializes the community:
  - **Key reuse, not re-mint.** The community's key is `{communityIri}#key-1` (the seeder/sample-host
    convention). If it already exists (`IKeyStore.TryGetKey`), it is **reused** (a re-creation must not
    re-key a live community — re-keying would break existing signatures); otherwise a new RSA key is minted
    and stored.
  - **`publicKey` extension stamped** on the `Group` document (`id`, `owner = communityIri`,
    `publicKeyPem`), the form the inbound key resolver reads when verifying a community-signed request.
  - **Stored in the community store** (`ICommunityStore.PutCommunityAsync`), so the new community's
    document endpoint, `members`, `feed`, and collections resolve (they previously 404'd for an
    unmaterialized community).

### Test stubs

The three test stubs that implement `IActivityPubClient` now implement the new member (no-op 202s,
matching their existing `AddMemberAsync`/`RemoveMemberAsync` stubs from 161k):

- `tests/Iris.Server.Tests/Services/FeedServiceTests.cs` (`StubClient`)
- `tests/Iris.Server.Tests/Caching/IrisRemoteCollectionFetcherTests.cs` (`StubCollectionClient`)
- `tests/Iris.Server.Tests/Security/IrisActorDocumentFetcherTests.cs` (`StubActivityPubClient`)

## Tests

A new integration test class `CommunityCreationIntegrationTests` (the client-side counterpart to
`CommunityMembershipClientIntegrationTests`). It seeds a local person **with a real signing key**
(`TestSeeder.SeedPersonWithKey`, so the server can verify a person-signed `Create`), builds a client
**signed as the person**, and drives `CreateCommunityAsync` through the full signed pipeline:

- **`CreateCommunityAsync_AuthoredByPerson_MaterializesCommunity`** — calls `CreateCommunityAsync`;
  asserts 202 + the community is now stored (was absent before) + its `Group` document carries a
  `publicKey` extension + it has no members yet.
- **`CreateCommunityAsync_Twice_IsIdempotentAndReusesKey`** — creates the same community twice; asserts
  both are 202 + the community still resolves + the community's key is **the same** public key (reused,
  not re-minted — re-keying would break signatures).
- **`PostNoteByPerson_DoesNotMaterializeCommunity`** — a normal Note `Create` (the existing path) does
  **not** materialize a community (19.5.1 materialization is `Group`-only).

Full suite green: **1,264 tests, 0 failed** (was 1,261, +3 new tests). Build clean
(`TreatWarningsAsErrors` on).

## Scope note

This closes the **CI-testable** create-community write path under 19.5.1 / 19.6.1. Still open for full
19.5.1 (both live/UI-verification items): the **UI creation screen** (the Blazor form that calls
`CreateCommunityAsync`) and the **WebFinger / `iris:capabilities` discovery verification** for a freshly
created community (Docker env + RayvenMX). Two deliberate non-goals, recorded: (1) the creator is **not**
automatically added as a member of the community they create (membership is a separate
`AddMemberAsync` operation, keeping each management operation a single concern); (2) a `Create` of a
`Group` whose IRI is a *foreign* host is not materialized (it is a remote object, not a local community to
create).
