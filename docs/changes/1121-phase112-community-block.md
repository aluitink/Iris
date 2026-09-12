# Phase 112 — Community-Scoped Block UI + Matrix Reconciliation

## Summary

Added community-scoped block capability (server endpoint + client methods +
UI button), and reconciled three stale 🟡 matrix entries that were already
implemented in earlier phases. The feature matrix is now fully ✅ (no
partial items remain).

## Changes

### Server — community block endpoint

- **`src/Iris.Server/ActivityPubServerExtensions.cs`**:
  - Added `CommunityBlockHandler` (mirrors `CommunityMuteHandler`):
    `POST /local/v1/c/{name}/blocks/{**target}`. Records a
    `CommunityBlock` edge; `?unblock=true` removes it. Idempotent (204).
    Authenticates via the community's Basic auth
    (`IActorCredentialValidator`); 404 for an unknown community.
    Invalidates the `blocks` collection page-1 cache on change.
  - Mapped the route in the local-moderation endpoint group.

### Client — block methods

- **`src/Iris.Client/ILocalModerationClient.cs`**: Added four methods:
  - `BlockCommunityMemberAsync(Iri, Iri, CancellationToken)`
  - `BlockCommunityMemberAsync(Iri, Iri, ProxyCredentials, CancellationToken)`
  - `UnblockCommunityMemberAsync(Iri, Iri, CancellationToken)`
  - `UnblockCommunityMemberAsync(Iri, Iri, ProxyCredentials, CancellationToken)`

- **`src/Iris.Client/LocalModerationClient.cs`**: Added
  `LocalCommunityBlockAsync` private helper (same pattern as
  `LocalCommunityMuteAsync`) that POSTs to
  `/local/v1/c/{name}/blocks/{targetId}` (with `?unblock=true` to remove).

### UI — Block button

- **`apps/Iris.Web.Client/Components/Pages/CommunityDetail.razor`**:
  - Added a Block button to the `MemberTemplate` (creator-only, shown for
    non-owner, non-self members, next to Promote/Mute).
  - Added `BlockMemberAsync` method + `IsBlockingMember` /
    `BlockingMemberIri` / `MemberBlockError` state.

### Matrix reconciliation (3 stale 🟡 → ✅)

- **View others' profile** 🟡 → ✅: Anonymous viewing was wired in Phase 88.4
  (`SameOriginApHandler` rewrites FQDN IRIs to same-origin for signed-out
  readers). Live-verified: `/actor?iri=…alice` renders the profile +
  Posts/Followers/Following tabs without login.
- **Follow-request queue (accept/reject)** 🟡 → ✅: Dedicated local
  endpoints exist (Phase 100): `GET/POST /local/v1/u/{handle}/requests[/accept|reject]/{**actorIri}`.
  Profile Requests tab + ActorDetail join-request queue both wired.
- **User list / role management** 🟡 → ✅: `AdminUsers.razor` already has
  role promote/demote (`POST /local/v1/admin/users/{id}/role`, Phase 88.5,
  refuses to demote the last admin).

## Verification

- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test` (fast): 0 failures across all test projects.
- Live (curl, Docker): `POST /local/v1/c/{name}/blocks/{target}` returns
  404 for an unknown community and 401 for unknown credentials — confirming
  the route is registered and the community exists. (The 401 is expected: the
  endpoint requires the community's own Basic auth, which the
  `LocalModerationClient` supplies via `LocalCredentials`.)

## Notes

- The `CommunityBlock` edge (kind in `EfCommunityStore`) and the `blocks`
  collection read endpoint already existed; this phase added the missing
  local POST to record/remove the edge plus the client + UI surface.
- Block is stronger than mute: it severs the relationship (the blocked
  member's content is hidden from the community feed), whereas mute only
  hides content while retaining membership.
- No new coded web tests (WASM manual-test policy).
