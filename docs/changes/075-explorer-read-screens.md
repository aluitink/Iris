# 075 — Phase 8 S6: Explorer read screens + NodeInfo client

> 2026-08-30 · Phase 8 (Sample) · Slice S6

## What was built

The Blazor WASM explorer's **read screens** are complete: every read surface the SAMPLE_PLAN slice
S6 names now has a routed page that calls the Iris client against the live (in-process) SampleServer,
and each screen's call is pinned by an in-process test. This is the "explore" half of the
boot → explore → interop story: a user logs on (S3–S5) and can now read the instance, browse/search
actors, open an actor, open an object, and open a community.

## What changed

- **New screens** (`samples/SampleBlazorClient/Pages/`), each a routed `.razor` page:
  - `Instance.razor` `/instance` — instance overview via `GetNodeInfoAsync` (software/instance
    names, open-registration, usage).
  - `Actors.razor` `/actors` — actors directory + search (`SearchAsync`).
  - `ActorDetail.razor` `/actor` — actor document (`GetObjectAsync`) + outbox feed
    (`GetCollectionAsync`) + moderation (mutes/blocks/flags).
  - `ObjectPage.razor` `/object` — object view (`GetObjectAsync`) + replies
    (`GetRepliesAsync`).
  - `Community.razor` `/community` — community feed + members + search.
  - `Layouts/MainLayout.razor` — nav links for each screen; `wwwroot/css/app.css` — nav/table/
    object-list styles.
- **`samples/SampleBlazorClient/Components/ObjectView.razor`** (new) — shared renderer for an
  `IObjectOrLink` (pattern-matches `IObject` vs `ILink`). Uses `@if`/`else if`/`else` — a
  `<switch>` in `.razor` markup fails with RZ10012, so the screens fall back to the supported
  conditional pattern.
- **`samples/SampleBlazorClient/_Imports.razor`** — `@using Iris.Samples.SampleBlazorClient.Components`
  so pages reference `ObjectView` without a per-file using.
- **New library surface** (`src/Iris.Client/`):
  - `NodeInfo.cs` (new) — the `NodeInfo` record (software name/version, instance names,
    open-registration, usage) + `FromJson` parser.
  - `IActivityPubClient.cs` / `ActivityPubClient.cs` — `GetNodeInfoAsync(instanceBase, ct)` served at
    `{base}/nodeinfo/2.0`.
- **Sample seed** (`samples/SampleServer/Program.cs`) — the seeded notes were written only to the
  outbox, but the object document endpoint (`GET /ap/v1/{**path}`) and global search read the *object
  store*. The seed now also `PutObjectAsync`s each note (and the reply), so the object view and the
  search screens have real, fetchable data.
- **Tests** — `tests/SampleBlazorClient.Tests/S6ScreenTests.cs` (new, 7 facts, one per screen call),
  each driving a `TestServer`-hosted SampleServer through the real client.
  - `Iris.Server.Tests` stub clients (`IrisRemoteCollectionFetcherTests`,
    `IrisActorDocumentFetcherTests`, `FeedServiceTests`) updated to implement the new
    `GetNodeInfoAsync` (the only other `IActivityPubClient` implementers in the repo).

## Decisions

- **Store the seeded content, don't special-case the screens.** The object view and search screens
  read the object store; the seed now puts the notes there. This is the faithful fix — the alternative
  (re-deriving the IRI from the outbox response in the test) would only mask that the sample's own
  object/search endpoints had no data, which is a real gap a user would hit.
- **`@if/else` over `<switch>` in markup.** RZ10012 means a Razor `<switch>` is not available in
  `.razor`; the shared `ObjectView` and every screen use the supported conditional pattern for the
  `IObjectOrLink` → `IObject`/`ILink` dispatch.
- **One shared object renderer.** All screens render objects through `ObjectView`, so the
  object/link type dispatch lives in one place (and is covered once by the in-process object test).

## Verification

- `dotnet build Iris.slnx` — 0 warnings / 0 errors; `samples/SampleBlazorClient` builds as a WASM app
  (default) **and** under `-p:ConsoleSmoke=true`.
- `dotnet test Iris.slnx` — all green: 872 total (`SampleBlazorClient.Tests` 29 → 36, 7 new S6 facts;
  all other projects unchanged).
