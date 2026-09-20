# 14821 — Withhold a gated follow request from the public `followers` collection until acceptance (S34)

- **Date:** 2026-09-20
- **Fixes:** [S34 — gated follow added to public `followers` before acceptance](../qa/s34-gated-follow-not-withheld-from-public-followers.md) (S2-sev, privacy/authorization)
- **Commits:** `d8d4303` (inbound path), `47fae1a` (local outbox path)
- **Deployed:** `47fae1a` (dev container `irisweb-iris-web-1`, healthy)

## Problem

A local person with `manuallyApprovesFollowers = true` who receives a `Follow` correctly **queues** the request (the requester appears in the owner's pending-requests list `/local/v1/u/{h}/requests` and in their notifications), but the requesting actor **immediately appeared in the public `followers` collection** (`GET /ap/v1/u/{h}/followers`) *before* the owner accepted. The public collection should only surface **accepted** followers for a gated account.

QA reproduced it on the two-instance federation stack (Interop A3): `ii-a1` gated, `ii-a2` follows → `GET /ap/v1/u/ii-a1/followers` already included `ii-a2` pre-accept.

## Root cause (two paths)

The public `followers` collection is backed by the **Follow edge** (`EdgeKind.Follow`, read via `IFollowStore.GetFollowersAsync`). Two write paths recorded that edge **unconditionally before the gate check**, so a pending (gated) follow was publicly listed:

1. **Inbound federation** — `Inbox/FollowActivityHandler.HandleAsync` (person branch): recorded `RecordFollowAsync` unconditionally, then checked `manuallyApprovesFollowers` and recorded a request edge. The community branch already did it correctly (withheld the membership edge when `manuallyApprovesMembers`, recorded only a join request).
2. **Local outbox publish** — `ActivityPubServerExtensions.RecordFollowLocalAsync`: recorded `RecordFollowAsync` unconditionally, then (when the target was a gated person) recorded a request edge. The follow was a **local** one (both actors on the same instance — the UI "Follow" path), so it never went through the inbox handler.

## Fix

In both paths, check the gate **first** and, when the target is a gated person, **withhold the Follow edge** and record **only** the pending `FollowRequest` edge:

- **Inbound** (`FollowActivityHandler.HandleAsync`): reorder the person branch — surface the outbox entry, then if `IsManuallyApprovingAsync` is true record only `RecordFollowRequestAsync` and return (no Follow edge); otherwise record `RecordFollowAsync` + invalidate the followers cache.
- **Local** (`RecordFollowLocalAsync`): restructure into three arms — community target (unchanged: records the community's follow/follower sets, withholds the membership edge + records a join request when `manuallyApprovesMembers`); gated person target (records only `RecordFollowRequestAsync`, Follow edge withheld); otherwise (records `RecordFollowAsync` as before).

Accept materializes the Follow edge (`ApplyFollowDecisionEdgeAsync` → `RecordFollowAsync`); Reject drains the pending request (its `RemoveFollowAsync` is now a no-op on the person path since the edge was withheld). The public `followers` endpoint and the follow store needed **no change** — the fix is purely at the edge-record sites.

## Tests

- `FollowActivityHandlerTests`: replaced `HandleAsync_LocalPersonManuallyApproves_RecordsEdgeAndSchedulesNoAccept` (which asserted the buggy `IsFollowingAsync == true`) with `HandleAsync_LocalPersonManuallyApproves_HoldsFollowRequestWithholdsFollowEdge` (asserts `HasFollowRequestAsync` true, `IsFollowingAsync` false, `GetFollowersAsync` does-not-contain, empty delivery).
- `FederationSignatureIntegrationTests.ManuallyApprovingActor_OperatorRejectsFollow_…`: updated — B (manually-approving) now withholds the Follow edge and holds a pending request (was asserting the provisional edge).
- `FollowRequestQueueIntegrationTests.LocalFollowOfGatedActor_RecordsFollowRequestEdge`: added a withheld-edge assertion (`IsFollowingAsync` false) for the **local** gated-follow path.

## Verification

- Fast suite **1409 pass, 0 fail** (Iris.Server.Tests); web **108 pass, 0 fail** (Iris.Web.Tests).
- **Live (dev, `47fae1a`):** registered `s34a` (gated via profile edit) + `s34b`. `s34b` follows `s34a`:
  - **Pending:** `Edges` kind=17 (FollowRequest) `s34b → s34a` recorded; kind=0 (Follow) **empty**; `GET /ap/v1/u/s34a/followers?refresh=true` → **totalItems = 0** (withheld).
  - `s34a` Requests tab lists `s34b` ("wants to follow you") with Accept/Reject.
  - **After Accept:** kind=0 (Follow) `s34b → s34a` recorded; kind=17 drained; `GET /ap/v1/u/s34a/followers?refresh=true` → **totalItems = 1** (`s34b`).

## Re-verify (QA — two-instance stack)

Re-run Interop A3 on a fresh two-instance stack: `ii-a1` gated, `ii-a2` follows → `GET /ap/v1/u/ii-a1/followers` must **exclude** `ii-a2` pre-accept; after Accept it includes `ii-a2` and the request is drained. (The same-instance local path is already live-verified on dev.)
