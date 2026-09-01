# 161k — Client community-maintenance one-call methods: `AddMemberAsync` / `RemoveMemberAsync` (19.6.1)

## Summary

Phase 19.6.1 (management via ActivityStream only): the client gains the two missing one-call
community-membership operations — **add member** (`AddMemberAsync`) and **remove member**
(`RemoveMemberAsync`) — completing the 19.6.1 invariant that *every management operation is
expressible as a one-call client method* (no side channel). Both are signed ActivityStream activities
(an `Add` / a `Remove`), delivered through the signed pipeline to the community's own inbox.

## What changed

### `IActivityPubClient` / `ActivityPubClient`

- **`AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct)`** — builds an `Add` with
  `actor = communityId`, `object = memberId`, and `Id = {communityId}/add-{guid}` (a unique,
  guid-suffixed IRI — not a deterministic dedupe IRI — because a member can be added/removed repeatedly
  and each operation is a distinct stored activity) and delivers it to `communityId.InboxOf()` through
  the signed `DeliverAsync`.
- **`RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct)`** — the inverse: builds a
  `Remove` (`actor = communityId`, `object = memberId`, `Id = {communityId}/remove-{guid}`) and delivers
  it to `communityId.InboxOf()`.

### Two design decisions (recorded here, per the loop's Open-Questions rule)

1. **Self-management: the activity's `actor` is the community, not a calling person.** The server's
   `AddActivityHandler`/`RemoveActivityHandler` apply a 19.5.2 gate — only an `Add`/`Remove` whose
   *actor is the recipient community* edits that community's member set (an `Add` from any other actor
   is stored but does not change membership). So the client sets `actor = communityId`, and the request
   must be signed as the community (the client's signing identity must be the community so the `actor`
   and the signature agree).
2. **Direct-inbox target — a documented deviation from the outbox convention.** Every other one-call
   method publishes to `actorId.OutboxOf()`. Community membership *cannot*: the community outbox publish
   endpoint accepts only `Follow`/`Undo`/`Accept`/`Reject`, so a membership `Add`/`Remove` is posted
   directly to `communityId.InboxOf()` (where the membership handlers run). This is called out in both
   methods' `<remarks>`.

### `CreateCommunityAsync` — **deferred (decision recorded)**

The 19.6.1 list also names *create community*. This is **not** added as a one-call client method in this
slice: community creation has **no federated ActivityStream activity type and no server route** to accept
one. Communities are created server-side / admin-side (seeded via `TestSeeder.SeedCommunityWithKey`), and
the only community write surfaces are the outbox (Follow/Undo/Accept/Reject) and the inbox (membership
`Add`/`Remove` + federation). There is no spec'd "Create Community" activity to express, so forcing one
would invent a non-AP activity — violating the governing principle (specialized capabilities are not
invented as AP activity types). Community creation therefore remains a server-side/admin concern, out of
scope for the "one-call client management method" invariant. (If a federated community-creation surface is
later required, it would land under a future 19.x slice with its own decision doc.)

### Test stubs

Three test stubs that implement `IActivityPubClient` now implement the two new members (no-op 202s,
matching their existing `LikeAsync`/`AnnounceAsync` stubs):

- `tests/Iris.Server.Tests/Services/FeedServiceTests.cs` (`StubClient`)
- `tests/Iris.Server.Tests/Caching/IrisRemoteCollectionFetcherTests.cs` (`StubCollectionClient`)
- `tests/Iris.Server.Tests/Security/IrisActorDocumentFetcherTests.cs` (`StubActivityPubClient`)

## Tests

A new integration test class `CommunityMembershipClientIntegrationTests` (the client-side counterpart to
the existing `CommunityMembershipManagementIntegrationTests`, which drives the same primitives over raw
signed HTTP). It seeds a community with a real signing key + two local actors, builds a client **signed as
the community** (its key), and drives the one-call methods through the full signed pipeline:

- **`AddMemberAsync_SignedAsCommunity_AddsMember`** — calls `AddMemberAsync`; asserts 202 + the member is
  recorded (the 19.5.2 gate passed because the activity's actor is the community).
- **`RemoveMemberAsync_SignedAsCommunity_RemovesMember`** — seeds a member, calls `RemoveMemberAsync`;
  asserts 202 + the member is removed.
- **`AddThenRemove_RoundTrip_EachOperationIsStoredAndMembershipToggles`** — the full add→remove round-trip
  through the one-call methods, asserting membership toggles.

Full suite green: **1,261 tests, 0 failed**. Build clean (`TreatWarningsAsErrors` on).

## Scope note

This closes the **client one-call** gap for community membership under 19.6.1. There is no sample-client
UI screen that creates a community or manages its membership (the community screen is read-only), so there
is no UI button to wire — the one-call methods are the deliverable, pinned by the integration tests. The
raw-inspector (UI) half of 19.6.1 remains a live/UI-verification item (Docker env + RayvenMX).
