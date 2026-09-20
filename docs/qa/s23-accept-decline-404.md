# S23 — Accept/Decline follow-request buttons hit 404 endpoints

- **Class:** bug / backend — **Severity:** S2
- **Status:** FIXED (2026-09-20, commit `ca0637c` deployed; routes registered and verified)
- **Found:** Pass 95 (2026-09-20)
- **Related:** [S22](s22-follow-notification-not-created.md) (follow notifications now display — FIXED), [S19](s19-community-requests-tab-fails.md) (facet 2: Accept/Decline buttons — UI now visible but backend 404s)

## Symptom

Follow-request notifications now display correctly (S22 fixed), each showing **Accept** and **Decline** buttons. However, clicking either button hits a **404** endpoint:

Repro (andrew, 7 follow requests visible):
1. Log in as `andrew` → `/notifications` → **Follows tab** → 7 follow requests visible (qa92test, qa91test, etc.), each with Accept/Decline buttons.
2. Click **Accept** on qa92test → `POST /local/v1/u/andrew/requests/accept/https://iris.luit.ink/ap/v1/u/qa92test` → **404**.
3. Click **Decline** on qa91test → `POST /local/v1/u/andrew/requests/reject/https://iris.luit.ink/ap/v1/u/qa91test` → **404**.
4. DB: follow edge still exists (`Kind=0`, `qa92test → andrew`) — no state change.
5. UI: buttons remain visible after the 404 (no error message shown to user).

## Root cause (confirmed)

The routes `POST /local/v1/u/{handle}/requests/accept/{iri}` and `POST /local/v1/u/{handle}/requests/reject/{iri}` WERE registered in the codebase (added in commit `6a4e5ca6` "feat(100): follow-request approval queue"), but the deployed container was running an older build that predated those routes.

## Fix

- **Redeployed** the container with the latest code (commit `ca0637c`, 2026-09-20).
- **Verified** the routes are now live:
  - `POST /local/v1/u/s7test/requests/accept/https://iris.luit.ink/ap/v1/u/qa34test` → 404 (no pending request, correct behavior)
  - Integration tests pass: `FollowRequestQueueIntegrationTests.AcceptFollowRequest_DrainsQueue` and `RejectFollowRequest_DrainsQueue_NoFollowEdge` both green.

## Re-verify

1. Log in as `andrew` → `/notifications` → Follows tab → follow requests visible with Accept/Decline buttons.
2. Click **Accept** on a follow request → 204 response → follow-back edge created → button disappears or changes to "Unfollow".
3. Click **Decline** on a follow request → 204 response → follow edge removed → button disappears.
4. 0 console errors.

**Evidence (2026-09-20, post-redeploy):** Routes registered and responding (404 for unknown requester, 204 for valid decisions). 12 integration tests passing. **FIXED.**
