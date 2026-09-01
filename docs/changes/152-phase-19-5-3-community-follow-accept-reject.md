# 152 — Community follow accept/reject: the community variant of the operator follow-decision endpoint

> 2026-09-01 · Slice 19.5.3 (community peers — inbound-follow reject/accept) · the community analogue of change 151 (the person path)

## What was built

Change 151 added the operator follow-accept/reject endpoint for a local **person** (the live outbound half
of `manuallyApprovesFollowers`, Resolved Decision #46 / J-10): a single catch-all route
`POST /ap/v1/u/{handle}/follows/{**followId}` that builds + records + server-delivers the deterministic
`Accept` (ensures the edge) or `Reject` (removes the edge). **19.5.3** asks for the community analogue —
"reject/undo flows for inbound follows of the community (we reject a follow → the peer sees `Reject`)" —
but there was no community variant: a community always auto-accepted its inbound follows, there was no
community manual-approve gate, no community decision endpoint, and no way for a community operator to
enumerate (let alone act on) an inbound follow.

This slice adds the community variant, reusing the person path's logic:

- **A shared follow-decision core.** The person endpoint's decision logic (resolve the follow by IRI from
  the activity store, target check, remote-follower guard, build + record + ensure/remove the edge,
  server-deliver the deterministic `Accept`/`Reject`) is extracted into `HandleFollowDecisionCoreAsync`.
  The only actor-type-specific pieces — the follow-edge store and the local-follower check — are passed in
  as delegates, so the core serves both the person and the community endpoints unchanged. `FollowIris`
  needs no change (its `Accept`/`Reject` builders are actor-IRI-agnostic; a community IRI works exactly
  like a person IRI).
- **A community follow-decision endpoint.** `POST /ap/v1/c/{name}/follows/{**followId}` (Basic-auth, the
  community's IRI is the credential seam; a trailing `/accept` selects acceptance). For an accept it builds
  + records the `Accept` in the activity store + the community's outbox, **ensures the community's
  follower edge** (`ICommunityStore.AddFollowerAsync`), and server-delivers the `Accept`; for a reject it
  does the same with the `Reject` and **removes** the follower edge (`RemoveFollowerAsync`). The
  provisional follower edge lives in the community's **followers set** (the community store has no point
  query, so the reject's "is the edge present" check reads `GetFollowersAsync` and checks membership).
- **The community manual-approve gate.** `FollowActivityHandler`'s community branch now (a) surfaces the
  inbound follow in the **community's own outbox** (so a UI can enumerate it, mirroring the person's
  "Inbound follows" surface) and (b) consults `manuallyApprovesFollowers` on the community's
  `ExtensionData` — when set, the community does **not** auto-accept (the edges are still recorded; the
  operator responds via the endpoint). `IsManuallyApprovingAsync` was extended to read the community store
  in addition to the actor store.

## Key types & files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `LocalFollowDecisionHandler` now delegates to a new
  shared `HandleFollowDecisionCoreAsync` (follow resolution + target/follower guards + build/record/edge/
  deliver, with the edge store + local-follower check injected as delegates); a new
  `CommunityFollowDecisionHandler` (resolves the community by name, Basic-auth, the community-store edge
  delegates, the combined local-follower check = local actor **or** local community) is registered at
  `POST /c/{name}/follows/{**followId}` (`community-follow-decision-endpoint`).
- `src/Iris.Server/Inbox/FollowActivityHandler.cs` — the community branch now adds the inbound follow to
  the community's outbox and applies the manual-approve gate (skipping the auto-`Accept` when the
  community manually approves); `IsManuallyApprovingAsync` reads the community store in addition to the
  actor store (shared `IsManuallyApproving` extension-data reader).
- `tests/Iris.Testing/TestSeeder.cs` — adds `SeedManuallyApprovingCommunityWithKey` (a `Group` with the
  `manuallyApprovesFollowers` flag + a real RSA-2048 signing key, mirroring
  `SeedManuallyApprovingPersonWithKey`).
- `tests/Iris.Server.Tests/CommunityFollowDecisionEndpointIntegrationTests.cs` — **new** integration test
  class (12 tests): accept (202 + `Accept` + follower edge ensured in the community store; idempotent
  re-accept; 401; 409 wrong target; 403 local follower — a local **community** following the community;
  410 not recorded) and reject (202 + `Reject` + follower edge removed; 401; 409; 403; 410; idempotent
  re-reject).
- `tests/Iris.Server.Tests/Inbox/FollowActivityHandlerTests.cs` — adds
  `HandleAsync_LocalCommunityManuallyApproves_RecordsEdgesAndSchedulesNoAccept` (the community gate
  suppresses the auto-`Accept`) and
  `HandleAsync_LocalCommunity_InboundFollowLandsInCommunityOutbox` (the inbound follow is surfaced in the
  community's outbox, both gate paths); adds a `SeedCommunityWithFlagAsync` helper.

## Tests

1217 → **1231** passing (+14: 12 community decision-endpoint integration tests + 2 handler unit tests).
Full `dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`).

- `Accept_Authenticated_RemoteFollow_IsAcceptedAndRecordsEdgeAndAccept` — a recorded inbound follow of the
  community is accepted via the `/accept` half: `202`, the deterministic `Accept` is in the activity store
  + the community's outbox, and the follower edge is present in the community's followers set.
- `Accept_LocalFollower_Is403` / `Reject_LocalFollower_Is403` — a local **community** (delta) following the
  managed community is a local relationship, not a pending remote follow; the endpoint forbids it (the
  community's local-follower guard covers a local community, not just a local person).
- `Accept_NotRecordedLocally_Is410` / `Reject_NotRecordedLocally_Is410` — a follow never recorded locally is
  `410 Gone` (the accept half checks the activity store; the reject half checks the followers set + an
  already-recorded `Reject`).
- `HandleAsync_LocalCommunityManuallyApproves_RecordsEdgesAndSchedulesNoAccept` — a manually-approving
  community records both community edges (follows + followers) but schedules **no** `Accept`.
- `HandleAsync_LocalCommunity_InboundFollowLandsInCommunityOutbox` — an inbound follow of a local community
  is recorded in the community's own outbox under its own IRI (both the manual-approve and auto-approve
  paths).

## Live verification (deferred — a UI/live item)

The endpoint's local-side behavior is pinned by the 12 integration tests (status codes + activity/outbox/
edge effects). The **cross-instance** half (a signed inbound follow of a gated community over the wire →
operator Accept finalizes the edge on both sides / Reject removes it; unauthenticated → 401) is the same
delivery model the person path already proved end-to-end in the two-instance Docker env (change 151) and in
`FederationSignatureIntegrationTests`; the community variant shares that delivery + `FollowIris` path.
Driving it in the sample UI (a community "Inbound follows" card) is the remaining UI item for 19.5.3.

## Decisions

- **Reuse the person path's core; inject the actor-specific pieces.** Accept/reject is the same decision
  (the followed side's response to an inbound follow) whether the followed actor is a person or a
  community; the only differences are the follow-edge store (a person's edge is in the
  `IFollowStore`, a community's follower edge is in the `ICommunityStore`'s followers set) and the
  local-follower check (a person is local per the actor store; a community's local follower can be a local
  actor *or* a local community). Passing those as delegates keeps a single tested decision core rather than
  duplicating the ~60-line handler.
- **The community's provisional follower edge is the `ICommunityStore` followers set.** On receipt,
  `FollowActivityHandler` records both the community→follower (follows) and follower→community (followers)
  edges. The operator's Accept/Reject decides the follower→community relationship, so it operates on the
  followers set (`AddFollowerAsync`/`RemoveFollowerAsync`). The community→follower (follows) edge is left
  in place (it drives the federated feed; an Undo — not an Accept/Reject — is the inverse of a follow the
  community *initiated*, and is out of scope here).
- **The community's local-follower guard covers a local community too.** A community that follows this
  community is a local relationship (an `Undo` unwinds it), so it is forbidden here just as a local person
  is. The person path's guard (actor store only) is a special case of the community path's combined check
  (actor store **or** community store).
- **Basic auth (the community's IRI) is the credential seam — not HTTP-signature.** The community outbox
  *publish* endpoint authenticates by HTTP signature (the community signs as itself) because it is the
  community's *authoring* surface. The follow *decision* is the operator's owner-only action on the
  community (mirroring the person endpoint and the mute/relay endpoints), so it uses the same Basic-auth
  `IActorCredentialValidator` (which is actor-IRI-agnostic, so a community IRI works). This keeps the
  decision seam uniform across person and community.
