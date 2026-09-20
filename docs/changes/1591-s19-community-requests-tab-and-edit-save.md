# S19 — Community Requests tab + Edit community Save

- **Date:** 2026-09-20
- **Status:** done (facets 1 + 3; facet 2 — notification Accept/Decline — remains open)
- **QA finding:** `docs/qa/s19-community-requests-tab-fails.md`

## Problem

Three facets reported:

1. **Requests tab dead-end:** The community Requests tab showed "We couldn't load the join requests.
   Please try again." with no network request fired and no retry button.
2. **Notification Accept/Decline:** Follow-request notifications had no action buttons.
3. **Edit community Save no-op:** The "Require approval for join requests" checkbox was not
   persisted on Save.

## Root cause (facet 1)

`LocalModerationClient.ResolveLocalHandler` threw `InvalidOperationException` when neither
`_localAuth` (Basic-auth) nor explicit credentials were configured. In the Blazor WASM client,
`IActorSessionAccessor.LocalModeration` creates a cookie-auth passthrough client (no Basic
credentials), so `ResolveLocalHandler` threw before any HTTP request was sent. The `catch` in
`CommunityDetail.razor.LoadJoinRequestsAsync` converted the exception into the dead-end error
message. Other local-moderation methods (`VoteAsync`, etc.) already handled the `_passthrough`
case; `ResolveLocalHandler` did not.

## Fix

- **`src/Iris.Client/LocalModerationClient.cs`** — `ResolveLocalHandler` now falls back to
  `_passthrough` (the cookie-auth handler) when no Basic-auth credentials are configured.
  Return type changed from `(LocalAuthHandler, bool)` to `(HttpMessageHandler, bool)` to
  accommodate the passthrough.

- **`apps/Iris.Web.Client/Components/Pages/CommunityDetail.razor`** — Added a **Retry** button
  to the Requests tab error state, so a genuine failure is recoverable.

## Verification

- 1392 server tests passed (0 failed)
- Live-verified (s7test, fresh browser):
  - Requests tab: `GET /local/v1/c/s21-verify/requests` → **200**, "No pending join requests."
  - Edit community: checked "Require approval" → Save → `GET /ap/v1/c/s21-verify` shows
    `manuallyApprovesMembers: true`. Reopened form → checkbox is **checked** (persisted).
  - Unchecked → Save → `manuallyApprovesMembers: NOT SET` (cleared correctly).
  - 0 console errors throughout.

## Remaining

- **Facet 2 (notification Accept/Decline buttons):** `NotificationRow.razor` has no action
  buttons for follow-request items. Requires adding Accept/Reject buttons wired to
  `POST /local/v1/u/{handle}/requests/accept|reject/{actorIri}`. Tracked in the Dev Queue.
