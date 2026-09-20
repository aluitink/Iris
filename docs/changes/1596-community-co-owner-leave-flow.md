# 1596 — Community co-owner Leave flow

- **Date:** 2026-09-20
- **Status:** COMPLETE
- **Related:** [docs/plans/community-simplification.md](../plans/community-simplification.md) Phase 5

## Summary

Added the co-owner Leave flow to the Communities management page (`/communities`). Co-owners (non-last owners) of a community can now leave the community, which demotes them from owner status.

## What was built

### UI changes (`apps/Iris.Web.Client/Components/Pages/Communities.razor`)

1. **Leave button** for co-owners (non-last owners):
   - Shows a "Leave" button on owned community cards when the user is a co-owner but not the last owner
   - Clicking "Leave" shows a confirmation dialog
   - Confirming the leave calls the server's `DemoteCommunityOwnerAsync` endpoint
   - The community is removed from the user's owner list (but not deleted)

2. **Last owner indicator**:
   - When the user is the only owner, shows "You are the only owner" text instead of a Leave button
   - This prevents the last owner from leaving (which would leave the community ownerless)

3. **Error handling**:
   - Added `LeaveError` property to display leave operation errors
   - Error messages shown in the same style as Delete errors

### Helper methods

- `IsCoOwner(Group community)`: Returns true if the current user is in the community's `AttributedTo` list
- `IsLastOwner(Group community)`: Returns true if the community has only one owner
- `LeaveCommunityAsync(Group community)`: Calls `DemoteCommunityOwnerAsync` to remove the current user from the owner list

## Server endpoints used

- `POST /local/v1/c/{name}/owners/demote/{**actorIri}` — Demotes an owner (existing endpoint, no changes needed)
- The endpoint verifies:
  - The requester is an owner (via `VerifyCommunityCreatorAsync`)
  - The actor being demoted is currently an owner
  - The demotion would not leave zero owners (returns 400 if it would)

## Tests

- No new coded tests (web-UI work — verified manually via Playwright)
- Existing server tests for owner promotion/demotion remain passing (1559 total tests)

## Verification

1. **Build**: 0 warnings, 0 errors
2. **Tests**: 1559 passed (1393 server + 108 web + 38 sample + 20 data)
3. **Deployed**: Container `irisweb-iris-web-1` recreated, healthy

## Result

Co-owners can now leave communities from the management page. The last owner is protected from leaving (which would leave the community ownerless). This completes the ④ control surfaces for community simplification Phase 5.
