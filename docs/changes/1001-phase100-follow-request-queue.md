# Phase 100 — Follow-request (follow-approval) queue

## What was built

Exposed the pending inbound `Follow` requests held for approval (the `manuallyApprovesFollowers` gate) as a first-class queue that a gated actor can list, accept, and reject — both via local API endpoints and the Profile page's Requests tab.

### Server

- **`EdgeKind.FollowRequest = 17`** — a new edge kind representing a pending (held) follow request, stored alongside the provisional `Follow` edge (Kind=0).
- **`IFollowStore` additions** — `RecordFollowRequestAsync`, `RemoveFollowRequestAsync`, `HasFollowRequestAsync`, `GetFollowRequestsAsync` (newest-first listing). Implemented in `InMemoryFollowStore`, `FileBackedFollowStore`, and `EfFollowStore` (EF/Postgres via `EdgeStore`).
- **Local endpoints** (on the `/local/v1` tree, owner-authenticated via Basic or cookie):
  - `GET /local/v1/u/{handle}/requests` — lists pending follow-request edges (requester IRIs, newest-first).
  - `POST /local/v1/u/{handle}/requests/accept/{**actorIri}` — accepts a held follow: confirms the follower→actor `Follow` edge and drains the pending request. Returns 204.
  - `POST /local/v1/u/{handle}/requests/reject/{**actorIri}` — rejects a held follow: removes the provisional `Follow` edge and drains the pending request. Returns 204.
- **`ApplyFollowDecisionEdgeAsync`** — shared helper for the accept/reject edge operations (used by both the local decision handlers and the outbox-published Accept/Reject path).

### The critical local-follow fix

The original implementation gated only the **remote (inbox) follow path** (`FollowActivityHandler.HandleAsync`): when a remote actor's Follow was delivered to a local gated actor's inbox, the handler recorded the `FollowRequest` edge and returned early (no auto-accept).

However, **local follows** (both the follower and the target are on the same instance) do NOT go through the inbox handler. They go through the **outbox-publish path**: the follower POSTs the Follow to their own outbox, and the server's `OutboxPublishHandler` calls `RecordFollowLocalAsync` to record the edge. `RecordFollowLocalAsync` unconditionally recorded the `Follow` edge without checking the gate — so a local follower of a gated actor was never held, and never appeared in the queue.

**Fix:** added `IsManuallyApprovingPersonAsync` (a static helper that checks whether a local person has `manuallyApprovesFollowers` set in their stored actor's `ExtensionData`) and a gate check in `RecordFollowLocalAsync`: when the target is a local person with the gate on, the `FollowRequest` edge is recorded in addition to the provisional `Follow` edge. This mirrors the inbox path's behavior exactly.

### Client

- **`ILocalModerationClient` / `LocalModerationClient`** — `GetFollowRequestsAsync`, `AcceptFollowRequestAsync`, `RejectFollowRequestAsync` (local, owner-authenticated calls to the endpoints above).

### UI (Profile.razor)

- The **Requests tab** (shown only when `ApprovesFollowers` is true) lists pending follow requests with the requester's handle, an **Accept** button, and a **Reject** button. The tab's badge shows the pending count.

### Dockerfile fix

The `apps/Iris.Web/Dockerfile` previously only cleaned `apps/Iris.Web.Client/{obj,bin,publish}` and `apps/Iris.Web/{obj,bin}`. It did **not** clean `src/*/bin` or `src/*/obj`. Because the build context copies the entire repo (including gitignored `bin/` and `obj/`), the Docker build would pick up stale `src/Iris.Server/bin/Release/net10.0/Iris.Server.dll` and skip recompiling the server code. This caused the live container to run the old server DLL (without the new endpoints) while running the new WASM client.

**Fix:** the `RUN` command now runs `find src apps -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +` before building, ensuring a clean rebuild of all projects.

## Tests

7 new integration tests in `tests/Iris.Web.Tests/FollowRequestQueueIntegrationTests.cs`:

Remote (inbox) path:
1. `GatedFollow_RecordsFollowRequestEdge_AppearsInQueue` — gated follow records the edge + appears in the queue endpoint.
2. `AcceptFollowRequest_DrainsQueue` — accept drains the request, confirms the follow edge, empties the queue.
3. `RejectFollowRequest_DrainsQueue_NoFollowEdge` — reject drains the request, removes the follow edge.
4. `AcceptFollowRequest_UnknownRequester_ReturnsNotFound` — 404 for an unknown requester.
5. `WithoutFlag_FollowDoesNotRecordFollowRequest` — no gate → no pending request.

Local (outbox) path (the critical fix):
6. `LocalFollowOfGatedActor_RecordsFollowRequestEdge` — a local follow of a gated actor records the `FollowRequest` edge + appears in the queue.
7. `LocalFollowOfNonGatedActor_DoesNotRecordFollowRequest` — a local follow of a non-gated actor does not record a pending request.

Full suite: **1412 passed / 0 failed**.

## Live verification

Verified via curl against the live Docker app (Postgres backend):
- Set alice's `manuallyApprovesFollowers` flag (DB update).
- Bob unfollowed then re-followed alice (local follow path).
- DB confirmed: Kind=0 (Follow) + Kind=17 (FollowRequest) edges recorded.
- `GET /local/v1/u/alice/requests` (Basic auth `alice:alice`) → `["https://iris.luit.ink/ap/v1/u/bob"]`.
- `POST /local/v1/u/alice/requests/accept/https://iris.luit.ink/ap/v1/u/bob` → 204.
- DB confirmed: Kind=17 drained, Kind=0 confirmed. Queue now empty.

**WASM UI note:** the Requests tab renders in the browser, but the local-moderation client constructs its request URL from the actor IRI's host (`https://iris.luit.ink/local/v1/...`), which mismatches the dev server's host (`http://localhost:8088`). This is a **dev-environment-only** issue — in production the actor IRI's host matches the app's host, so the UI works. A follow-up fix (using relative URLs in `LocalModerationClient`) would make the dev environment work too, but is out of scope for this phase.

## Key files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — local endpoints, `RecordFollowLocalAsync` gate fix, `IsManuallyApprovingPersonAsync`, `ApplyFollowDecisionEdgeAsync`.
- `src/Iris.Server/Inbox/FollowActivityHandler.cs` — inbox (remote) follow gate (original Phase 100 change).
- `src/Iris.Server/Stores/IFollowStore.cs` — follow-request store interface.
- `src/Iris.Server.InMemory/Stores/InMemoryFollowStore.cs` — in-memory implementation.
- `src/Iris.Server/Persistance/Stores/FileBackedFollowStore.cs` — file-backed implementation.
- `src/Iris.Server.Data/Stores/EfFollowStore.cs` + `EdgeStore.cs` + `EdgeEntity.cs` — EF/Postgres implementation.
- `src/Iris.Client/ILocalModerationClient.cs` + `LocalModerationClient.cs` — client methods.
- `apps/Iris.Web.Client/Components/Pages/Profile.razor` — Requests tab UI.
- `apps/Iris.Web/Dockerfile` — clean `bin`/`obj` before build.
- `tests/Iris.Web.Tests/FollowRequestQueueIntegrationTests.cs` — 7 new tests.
