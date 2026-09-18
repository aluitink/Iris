# Phase 109 — Community-Scoped Mute UI

## Summary

Added community-scoped mute capability: client methods for muting/unmuting
members within a community, and a Mute button in the CommunityDetail
Members tab (creator-only).

## Changes

### Client library

- **`src/Iris.Client/ILocalModerationClient.cs`**: Added four new methods:
  - `MuteCommunityMemberAsync(Iri, Iri, CancellationToken)`
  - `MuteCommunityMemberAsync(Iri, Iri, ProxyCredentials, CancellationToken)`
  - `UnmuteCommunityMemberAsync(Iri, Iri, CancellationToken)`
  - `UnmuteCommunityMemberAsync(Iri, Iri, ProxyCredentials, CancellationToken)`

- **`src/Iris.Client/LocalModerationClient.cs`**: Added `LocalCommunityMuteAsync`
  helper (same pattern as `LocalCommunityMemberRemoveAsync`) that POSTs to
  `/local/v1/c/{name}/mutes/{targetId}` (with `?unmute=true` to remove).

### Community UI

- **`apps/Iris.Web.Client/Components/Pages/CommunityDetail.razor`**:
  - Added Mute button to the `MemberTemplate` (creator-only, shown for
    non-owner, non-self members alongside Promote/Remove).
  - Added `MuteMemberAsync` method + `IsMutingMember` / `MutingMemberIri` /
    `MemberMuteError` state.

### Matrix fixes

- **`docs/plans/production-app-feature-matrix.md`**: Fixed two more stale
  entries:
  - Infinite-scroll/pagination: 🟡 → ✅ (Phase 98 implemented true infinite scroll).
  - Filter by type: 🟡 → ✅ (Phase 108 added the Mentions tab).

## Verification

- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test` (fast): 0 failures across all test projects.
- Live Playwright (fresh context, WASM hash `xhajlhmxb4`):
  - Community mute endpoint returns 401 via cookie-auth fetch (expected —
    the endpoint requires Basic auth with the community's credentials,
    which the `LocalModerationClient` handles via the session's
    `LocalCredentials`).
  - No members in the test community (the Mute button only renders when
    the creator views a non-owner, non-self member).

## Notes

- The server endpoint (`CommunityMuteHandler` in `ActivityPubServerExtensions.cs`)
  already existed — it validates the community exists, authenticates via
  Basic auth, resolves the target IRI, and records/removes the `CommunityMute`
  edge (kind 14). This phase added the missing client + UI surface.
- No new coded web tests (WASM manual-test policy).
