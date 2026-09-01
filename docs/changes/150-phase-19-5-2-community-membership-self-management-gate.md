# 150 — Phase 19.5.2: Community membership management (self-management gate)

> 2026-09-01 · Slice 19.5.2 (membership management) · Phase 19.5 (community creation & management)

## What was built

The `Add`/`Remove` community-membership mechanism already existed (F-09, Slice 12.20):
`AddActivityHandler`/`RemoveActivityHandler` interpret an inbound `Add`/`Remove` whose recipient is a
local community as a membership edit (add/remove the activity's `object` to/from the community's member
set via `ICommunityStore`). **But it had no authorization** — any signature-validating actor could add
or remove members of a local community, because the handlers gated only on "recipient is a local
community," not on who was acting.

This slice closes the authorization gap and proves the end-to-end "membership change is reflected"
behavior:

- **Self-management gate (the 19.5.2 authorization).** `AddActivityHandler` and
  `RemoveActivityHandler` now also require the activity's **actor to be the recipient community itself**.
  An `Add`/`Remove` delivered to a local community whose actor is *some other* actor is stored (the
  signature is validated) but does **not** modify the community's member set. Only the community
  manages its own membership. This mirrors the existing gate on the community outbox publish endpoint
  (`CommunityOutboxPublishHandler`), which rejects any published activity whose `actor` is not the
  community with 403.
- **Feed + members reflection (now proven).** The community feed (the union of the local members'
  outboxes) and the `members` collection already read the member set live, so they reflect membership
  changes automatically. New integration tests pin this: a community-signed `Add` makes the member's
  outbox content appear in the feed and the member appear in `members`; a community-signed `Remove`
  reverses both.

## Key types & files

- `src/Iris.Server/Inbox/AddActivityHandler.cs` — after the "recipient is a local community" check, now
  resolves the activity's `actor` and returns (no-op) unless `actor == delivery.RecipientIri`. Class
  `<remarks>` updated to document the self-management gate.
- `src/Iris.Server/Inbox/RemoveActivityHandler.cs` — the same gate (actor must be the recipient
  community).
- `tests/Iris.Server.Tests/CommunityMembershipManagementIntegrationTests.cs` — **new** integration test
  class (3 tests): a community-signed `Add` to its own inbox adds the member (feed + `members` reflect
  it); a community-signed `Remove` removes the member (feed + `members` reflect it); and an `Add` whose
  actor is a *different* actor (but signed by the community, so the signature still validates) is stored
  but does not modify the membership.
- `tests/Iris.Server.Tests/AddRemoveFederationIntegrationTests.cs` — the two "remote actor's signed
  `Add`/`Remove`" tests were repurposed to assert the new gate (the activity is still stored after
  signature validation, but the membership is **not** modified); class `<remarks>` updated.

## Tests

1208 → **1211** passing (+3 new `CommunityMembershipManagementIntegrationTests`). Full `dotnet test`
green; `dotnet build` clean (`TreatWarningsAsErrors`).

- `Add_SignedByCommunity_DeliveredToOwnInbox_AddsMemberAndReflectsInFeedAndMembers` — a community
  (a `Group` with a real key) signs an `Add` (actor = itself, object = alice) and posts it through its
  own inbox; the server resolves the community's key (fetching its own `Group` document), validates the
  signature, and the handler's gate passes → alice becomes a member; her post appears in the feed and
  her IRI in `members`.
- `Remove_SignedByCommunity_DeliveredToOwnInbox_RemovesMemberAndReflectsInFeedAndMembers` — the inverse:
  alice starts as a member; a community-signed `Remove` removes her; her post disappears from the feed
  and `members`.
- `Add_SignedByCommunityButActorIsAnotherActor_DoesNotModifyMembership` — the community signs the
  request (valid signature), but the activity's actor is alice; the gate rejects it → the activity is
  stored but the membership is unchanged.

## Decisions

- **The community manages its own membership (actor == recipient).** The chosen model is that a
  community's membership is edited only by the community itself, via an `Add`/`Remove` the community
  posts through its own inbox (self-management). This is consistent with how a community publishes
  `Follow`s through its own outbox (the outbox publish endpoint already gates on `actor == community`,
  403 otherwise), so the `Add`/`Remove` gate is the membership analogue of the same rule. An `Add`
  posted to a community's inbox by any other actor — even a remote actor with a valid signature — is
  no longer a membership edit.
  - *Why not allow any signed actor to edit membership?* That was the F-09 behavior (any actor whose
    signature validates). It is a security gap: a remote actor could add or remove members of a local
    community at will. The 19.5.2 gate closes it.
  - *Why actor == recipient (the community) rather than an admin/owner?* Iris's community model does not
    currently distinguish an admin/owner from the community actor (a `Group` is a single actor). The
    community actor *is* the management authority for its own collections (its outbox, its membership).
    Introducing a separate admin/owner role is out of scope for this slice; if a future slice adds a
    distinct admin identity, the gate generalizes to "actor is the community or its admin."
  - *What about a remote actor wanting to join?* A join is a **request**, not an edit: a remote actor
    expresses interest by following the community (or the community accepting a follow) — the
    Follow/accept flow, not an `Add` the remote actor posts to the community inbox. The community then
    records the membership itself (a community-signed `Add`), or the follow-lifecycle records it. This
    is the "Follow-based join" alternative the ROADMAP mentions; the `Add`-based self-management is the
    primary path for an existing local member, and the join-request flow is a separate 19.5.x item.
- **Kept the store-level idempotency.** `AddMemberAsync`/`RemoveMemberAsync` remain idempotent, so a
  re-delivered (at-least-once) `Add`/`Remove` from the community is safe to re-apply — unchanged.