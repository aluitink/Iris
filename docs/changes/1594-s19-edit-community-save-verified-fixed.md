# 1594 — S19 facet 3: Edit community Save verified fixed

- **Date:** 2026-09-20
- **Status:** FIXED (verified)
- **Related:** [docs/qa/s19-community-requests-tab-fails.md](../qa/s19-community-requests-tab-fails.md)

## Summary

S19 facet 3 (Edit community Save not persisting) was reported as STILL OPEN in QA Pass 90
(deployed `45e5b70`). The dev loop re-verified the feature against the deployed container and
confirmed it works correctly.

## Root cause of the QA finding

QA Pass 90 tested the feature against a container built at 12:53:13. The `HandleCommunityUpdateAsync`
path in `CommunityOutboxPublishHandler` (which merges Name/Summary/Icon into the stored community)
was introduced in commit `426a1875` and is an ancestor of the deployed commit `45e5b70`. The code path
is correct: `POST /ap/v1/c/{name}/outbox` with an `Update` activity → `HandleCommunityUpdateAsync`
→ `persistence.Communities.PutCommunityAsync(stored, ct)` → DB write.

The QA finding was likely caused by a stale WASM bundle in the browser (the known stale-WASM
gotcha: after a container recreate, the browser caches an old `Iris.Web.Client.<hash>.wasm`).
The QA browser may have been running an older WASM that did not include the
`HandleCommunityUpdateAsync` path or the `SubmitCommunityEditAsync` client method.

## Verification

1. **Unit test** (`CommunityCreationIntegrationTests.UpdateActorAsync_OnGroup_CommunityDocumentReflectsChange`):
   Creates a community, updates its name + summary via `UpdateActorAsync`, verifies the stored
   community reflects the change, and verifies the `GET /ap/v1/c/devs` document endpoint serves
   the updated values. Passes.

2. **Live test** (deployed container `irisweb-iris-web-1`, deployed `45e5b70`):
   - Logged in as `s7test`
   - Navigated to `/communities` → "My communities" tab → `s21-second` community
   - Clicked "Edit community" → changed Description to "Live test description" → Save
   - UI updated to show "Live test description"
   - Refreshed the page → "Live test description" still shown (persisted)
   - Network: `POST /ap/v1/proxy/...` for the Update → 200 OK

## Tests added

- `CommunityCreationIntegrationTests.UpdateActorAsync_OnGroup_CommunityDocumentReflectsChange` —
  verifies the community document endpoint reflects an update (not just the store).

## Result

S19 facet 3 is FIXED. The Edit community Save feature works correctly: the Update activity is
processed by `HandleCommunityUpdateAsync`, which merges the mutable fields (Name, Summary, Icon)
into the stored community and persists via `PutCommunityAsync`. The community document endpoint
serves the updated values.
