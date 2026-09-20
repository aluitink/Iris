# 1585 — Phase 5: Community management (delete)

**Workstream:** Unified home feed (③④)
**Phase:** 5 of 7

## What was built

Community creators can now delete their communities from the `/communities` page.

**Server:**
- `ICommunityStore.DeleteCommunityAsync(Iri, CancellationToken)` — removes a community
  and all its moderation edges (members, followers, follow, block, mute, flag, join-request).
- `FileBackedCommunityStore`, `InMemoryCommunityStore`, `EfCommunityStore` — implementations.
- `DELETE /local/v1/c/{name}` route (owner-only via `VerifyCommunityCreatorAsync`).
  Invalidates the community's collection caches (members, followers, feed).

**Client:**
- `ILocalModerationClient.DeleteCommunityAsync(Iri, CancellationToken)` + overload
  accepting `ActorCredential`. Uses `HttpMethod.Delete` against `/local/v1/c/{name}`.

**UI:**
- `Communities.razor`: delete button (2-step confirm) visible only to the community
  creator. Uses `Session.LocalModeration` (not DI). Shows error on failure.

## Files changed

- `src/Iris.Server/Stores/ICommunityStore.cs` — `DeleteCommunityAsync` added to interface.
- `src/Iris.Server/Persistance/Stores/FileBackedCommunityStore.cs` — implementation.
- `src/Iris.Server.InMemory/Stores/InMemoryCommunityStore.cs` — implementation.
- `src/Iris.Server.Data/Stores/EfCommunityStore.cs` — implementation (removes `ActorEntity` + `EdgeEntity` rows).
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `CommunityDeleteHandler` + `MapDelete` route.
- `src/Iris.Client/ILocalModerationClient.cs` — `DeleteCommunityAsync` overloads.
- `src/Iris.Client/LocalModerationClient.cs` — `LocalDeleteCommunityAsync` + `BuildCommunityLocalRequest` fix for empty path.
- `apps/Iris.Web.Client/Components/Pages/Communities.razor` — delete button with 2-step confirm.
- `tests/Iris.Server.Tests/CommunityDeleteIntegrationTests.cs` — **new**, 5 tests.

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- `Iris.Server.Tests` 1385/1385 pass (5 new: creator succeeds, non-creator forbidden, unknown 404, removes followers, removes moderation edges).
- Live-verified (fresh browser context, `s7test`):
  - Created community `test-community` via UI.
  - `/communities` "All on this instance" tab: delete button visible only on `test-community` (creator).
  - Click Delete → "Delete this community?" confirm → Confirm → community removed from list.
  - `GET /ap/v1/c/test-community` → 404.
  - 0 console errors.
