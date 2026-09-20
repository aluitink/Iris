# 1001 — Inbox ②: Community simplification (Unify Members with Followers)

**Date:** 2026-09-20
**Status:** Done (Phases 1–6 committed; Phase 7 dead-code removal landed in this change)
**Scope doc:** [docs/plans/community-simplification.md](../plans/community-simplification.md)

## Summary

Collapsed Iris's separate community **members** axis (`Join`/`Leave` → `CommunityMember` edge) into the community's **followers** axis (`Follow`/`Undo` → `CommunityFollower` edge), matching Lemmy's single-relationship model: **a community's members are its followers.** Joining a community is a Follow; leaving is the Undo of that Follow. The community's inbound Follow is gated on `manuallyApprovesMembers`. Peering (community-follows-community, `CommunityFollow` edge) is untouched.

## What changed

### Phase 1 — Server: members = followers (commit `cb9330f`)
- `MembershipActivityHandler` `AddMemberAsync`→`AddFollowerAsync`, `RemoveMemberAsync`→`RemoveFollowerAsync`; `AddActivityHandler`/`RemoveActivityHandler` member add/remove → follower ops.
- `ActivityPubServerExtensions`: `RecordCommunityAddAsync`/`RemoveAsync`/`RecordJoinDecisionLocalAsync`/local member-remove/accept/promote → follower ops; `/members` endpoint serves from `GetFollowersAsync`.
- `CommunityContentRecorder`, `DeleteActivityHandler`, `CommunityFeedService`: member reads → `GetFollowersAsync`.
- `ICommunityStore` + all store implementations: one-time idempotent `MigrateMembersToFollowersAsync` wired into `EnsureCreatedAsync` (re-keys any legacy `CommunityMember` edge into the followers set).
- `samples/SampleServer/Program.cs`: seed → `AddFollowerAsync`.
- Test remapping across 37+ files; 4 semantic assertion flips (follow IS membership, change 221).

### Phase 3 — Gate community follows on `manuallyApprovesMembers` (commit `abc1b33`)
- `FollowActivityHandler` community branch now gates on `manuallyApprovesMembers` (not `manuallyApprovesFollowers`): when set, the membership (followers) edge is withheld and a pending join request is recorded (no Accept); when off, the followers edge is granted immediately. The community's follows edge is always recorded (drives the federated feed).
- `ActivityPubServerExtensions.RecordFollowLocalAsync` community branch: same gate via new `IsManuallyApprovingMembersAsync`.
- Test fixes: `FollowActivityHandlerTests`, `CommunityGatedPeeringIntegrationTests`, `TestSeeder`.

### Phase 4 — Client: Join/Leave drive Follow/Undo (commit `68ae703`)
- `ActivityPubClient.RequestJoinAsync` → delegates to `FollowAsync`; `RequestLeaveAsync(actorId, originalFollowId)` → delegates to `UndoFollowAsync` (signature changed from `communityIri` to `originalFollowId`).
- `JoinButton.razor` (now deleted) tracked the join's Follow IRI for the Undo.
- 6 mock impls updated to the new `RequestLeaveAsync` signature.

### Phase 5 — Single Join/Leave button on community detail (commit `c4511c0`)
- `CommunityDetail.razor`: removed the redundant `FollowButton` next to `JoinButton` (joining IS following).

### Phase 6 — Lemmy interop (verified offline)
- Live Lemmy interop is gated behind `IRIS_LIVE_INTEROP=1` + a configured target FQDN (no live target available). The offline Lemmy-facing path is verified consistent: `GET /ap/v1/c/{name}/members` serves from `GetFollowersAsync`; `ResolveMembersIri` maps Lemmy→`/followers`. No code change required.

### Phase 7 — Dead-code removal + UI consolidation (this change)
- **Deleted `JoinButton.razor`.** The canonical `FollowButton` is now the single Join/Leave control, labeled **"Join"/"Leave"** for communities via a new `IsCommunity` parameter (labeled "Follow"/"Unfollow" for people). `CommunityDetail.razor` and `CommunityCard.razor` now render `<FollowButton … IsCommunity="true" />`.
- **Retired the `ICommunityStore` member methods** (`AddMemberAsync`, `RemoveMemberAsync`, `IsMemberAsync`, `GetMembersAsync`) from the interface + all four implementations (EF, InMemory, FileBacked, and the migration). No callers remained (all membership state is now the followers axis). The `EdgeKind.CommunityMember` enum value is **retained** — `MigrateMembersToFollowersAsync` still needs it to re-key any legacy persisted membership rows at startup.
- Fixed two stale XML-doc `cref`s (`AddActivityHandler.cs:43`, `RemoveActivityHandler.cs:43`) that referenced the removed `AddMemberAsync`/`RemoveMemberAsync` (this was the committed build break QA flagged in Pass 23).
- The **client** `IActivityPubClient.AddMemberAsync`/`RemoveMemberAsync` (Add/Remove AP activities to a community's outbox) are **retained** — they are a live admin path (used by `SampleBlazorClient`'s member management and still processed server-side by `AddActivityHandler`/`RemoveActivityHandler` as follower ops).

## Verification
- Full solution build: green (0 warnings / 0 errors), including the previously-broken `AddActivityHandler`/`RemoveActivityHandler` `cref`s.
- All test suites green: Server 1371, Web 106, Client 190, Client.Extensions 29, Core 467, WebCrypto 3, Server.Data 20, SampleBlazorClient 17, SampleServer 38.
- No `JoinButton` references remain in `apps/` or `src/`.
- No `ICommunityStore` member-method callers remain.

## Notes
- The live container must be **rebuilt** to pick up the committed Phase 1–7 work (the running container predates it).
- Live Lemmy interop (Phase 6) is deferred to a manual/live verification pass (gated by `IRIS_LIVE_INTEROP`).
