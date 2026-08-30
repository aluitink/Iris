# 085 — Phase 12: community outbox write surface for outbound community follow (gap G-3)

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Gap closure (G-3)

## What was built

A community outbox write surface (`POST /ap/v1/c/{name}/outbox`) that lets a local `Group` community
initiate or undo a `Follow` of a remote actor/community. This closes gap **G-3** ("No outbound
group-follow"): previously a local community could **receive** follows (inbound `FollowActivityHandler`
records the community's follow/follower sets + auto-schedules an `Accept`), but could **not initiate** a
`Follow` of a remote `Group` — the only outbound `Follow` construction was the client's `FollowAsync`
(a local person), and there was no `POST /c/{name}/outbox` write surface for a community actor.

## The community outbox endpoint

- **Route:** `POST /ap/v1/c/{name}/outbox` (`CommunityOutboxPublishHandler`), in
  `src/Iris.Server/ActivityPubServerExtensions.cs`.
- **Auth:** HTTP signature (server-to-server profile) — the signing key must resolve to this community
  (else **401**). The community must exist (else **404**).
- **Body:** an ActivityStreams activity. Must be a `Follow` or `Undo` (else **400**). The activity's
  actor must be this community (else **403**).
- **Behavior:**
  1. Verify the signature resolves to the named community (else **401**).
  2. Resolve the community (else **404**).
  3. Read the body (buffered, position-reset for the signature middleware); empty → **400**.
  4. Deserialize to `IObjectOrLink`; must be a `Follow` or `Undo` (else **400**).
  5. The activity's actor must be this community (else **403**).
  6. **`Follow`:** record the follow in the community's follows set + activity store + outbox. If the
     target is a **remote** community/actor, server-deliver the signed `Follow` to the target's inbox
     (signed as the community). If the target is **local**, no cross-instance hop is needed.
  7. **`Undo`:** resolve the original `Follow` from the activity store (referenced by IRI in the Undo's
     object). Remove the follow from the community's follows set + activity store + outbox. If the
     original target was **remote**, server-deliver the signed `Undo` to the target's inbox.
  8. Return **202 Accepted**.

## Supporting changes

- **`UndoActivityHandler`** (`src/Iris.Server/Inbox/UndoActivityHandler.cs`):
  - `ResolveFollowTargetAsync` → `ResolveFollowPartiesAsync` returning `(Iri? Target, Iri? Follower)`
    (target = `follow.Object`, follower = `follow.Actor`).
  - Community-follower branch now removes the correct `(community → otherParty)` edges where
    `otherParty` is the target if this community made the follow, the follower if a remote party
    followed this community.
- **`FollowResponseActivityHandler<T>`** (`src/Iris.Server/Inbox/FollowResponseActivityHandlerT.cs`):
  new `virtual IsLocalRecipientAsync` so subclasses can extend the local-recipient check.
- **`AcceptActivityHandler`** (`src/Iris.Server/Inbox/AcceptActivityHandler.cs`): overrides
  `IsLocalRecipientAsync` + `ApplyAsync` to accept a local community as a recipient (the base guard only
  checked the person store).
- **`ReadAsBufferedStringAsync`** (Stream overload): resets `body.Position = 0` before reading (the
  signature middleware's `EnableBuffering` + `CopyToAsync` drains the body).

## Tests

8 integration tests in `tests/Iris.Server.Tests/CommunityOutboxPublishIntegrationTests.cs`:
1. `CommunityOutbox_InvalidSignature_Returns401` — bad signature → 401.
2. `CommunityOutbox_UnknownCommunity_Returns404` — valid signature, unknown community name → 404.
3. `CommunityOutbox_NonFollowOrUndo_Returns400` — a `Create` activity → 400.
4. `CommunityOutbox_ActorIsNotThisCommunity_Returns403` — actor is a different community → 403.
5. `CommunityOutbox_SignedFollow_RecordsEdgeActivityAndOutbox` — signed follow of a local target records
   the community's follows edge + activity store + outbox entry.
6. `CommunityOutbox_FollowOfRemoteCommunity_FederatesAndAcceptFinalizesBothSides` — signed follow of a
   remote community: B's server delivers the Follow to A; A's `FollowActivityHandler` records the edge +
   auto-schedules an Accept; B's `AcceptActivityHandler` finalizes B's side.
7. `CommunityOutbox_SignedUndoRemovesTheFollowEdge` — signed Undo of a local follow removes the edge.
8. `CommunityOutbox_UndoOfRemoteFollow_RemovesEdgeOnBothSides` — signed Undo of a remote follow: B's
   server delivers the Undo to A; A's `UndoActivityHandler` removes A's edge.

All 1003 tests pass (0 failures).

## Gap register

- **G-3** (No outbound group-follow): **MITIGATED**.
