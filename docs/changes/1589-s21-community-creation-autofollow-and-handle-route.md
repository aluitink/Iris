# S21 — Community creation auto-follow + `/c/{handle}` route

- **Date:** 2026-09-20
- **Status:** done
- **QA finding:** `docs/qa/s21-new-community-missing-following-tab.md`

## Problem

1. **No auto-follow on community creation:** When a user created a community, no follow edge was
   recorded from the creator to the new community. The community did not appear in the creator's
   Following tab until the creator manually followed it.

2. **`/c/{handle}` route 404:** Navigating to `/c/{handle}` (e.g. `/c/my-community`) returned a 404
   "Not found" in the Blazor SPA. Communities were only reachable via `/community?iri=…`.

## Fix

### Auto-follow on community creation

In `ActivityPubServerExtensions.RecordCreateLocalAsync`, after storing the created community
(19.5.1 write path), record a follow edge from the creator's actor to the new community:

```csharp
await persistence.Follows.RecordFollowAsync(authorIri, communityIri, ct);
```

This ensures the creator's Following collection includes the new community immediately after
creation, so it appears in the Following tab without a manual follow.

### `/c/{handle}` route

Added a new Blazor page `CommunityHandleRedirect.razor` with the route `@page "/c/{handle}"`.
On initialization, it constructs the community IRI as `{Nav.BaseUri}/ap/v1/c/{handle}` and
navigates to `/community?iri={communityIri}` using the existing community detail page (which
already has all the tabs: Feed, Members, Owners, Peers, Requests).

## Files changed

- `src/Iris.Server/ActivityPubServerExtensions.cs` — added `RecordFollowAsync` call in
  `RecordCreateLocalAsync` (S21 auto-follow)
- `apps/Iris.Web.Client/Components/Pages/CommunityHandleRedirect.razor` — new page for
  `/c/{handle}` redirect
- `tests/Iris.Server.Tests/CommunityCreationIntegrationTests.cs` — new test
  `CreateCommunityAsync_AutoFollowsCreator`

## Verification

- 1391 server tests passed (0 failed), 106 client tests passed
- Live-verified: created community `s21-verify` → appears in Following tab with "Leave" button
- Live-verified: `/c/s21-verify` → redirects to community page with all tabs (Feed, Members, Owners,
  Peers, Requests)
- 0 console errors
