# Phase 151 — Background Processing: object interaction counters

**Status:** COMPLETE (live-verified, all suites green)
**Date:** 2026-09-17

## What

A background service pre-computes per-object interaction counters and persists them onto the stored
object documents, so the read paths serve cached counters instead of walking the reverse indexes
per read.

Previously, every object-document read and every collection-page enrichment computed
`iris:likedCount`/`sharedCount`/`repliedCount`/`dislikedCount`/`score` on the fly by sweeping the
like/announce/reply/dislike reverse indexes for that object — an O(n) walk per read. For a busy feed
page that is one sweep per item.

Now a background `IHostedService` keeps those counters current on the stored documents, and the read
paths read the cached counters (falling back to the per-read sweep only for objects that lack them —
e.g. a freshly proxied object not yet reached by a refresh pass).

## How

### New service — `src/Iris.Server/Stores/ObjectInteractionCountRefreshService.cs`

- A `BackgroundService`. On startup it runs a refresh pass, then repeats every
  `ActivityPubServerOptions.ObjectInteractionRefreshInterval` (default 30s). A non-positive interval
  disables the periodic pass (the startup pass still runs once).
- One pass:
  1. `IObjectStore.ListObjectsAsync` — enumerate every stored object.
  2. Batch-read the three reverse indexes for the whole object set in a single round-trip each
     (57.4 — avoids an N+1 per object): `ILikeStore.GetLikersBatchAsync`,
     `IAnnounceStore.GetAnnouncersBatchAsync`, `IReplyStore.GetRepliesBatchAsync`. Dislikes have no
     batch method, so `IDislikeStore.GetDislikersAsync` is read per object.
  3. For each object with a resolvable IRI, compute the five counters and write them onto
     `obj.ExtensionData` (the `https://iris.luit.ink/ns#` namespace terms: `likedCount`,
     `sharedCount`, `repliedCount`, `dislikedCount`, `score` = liked − disliked).
  4. Re-store the object **only if a counter actually changed** (`WriteCountsIfChanged`) — so a
     converged pass writes 0 rows (idempotent, no write amplification).
- Tolerates a null `IPersistenceProvider` (inert — the in-memory test harness and any host without
  persistence is unaffected). Never throws: a failing pass is logged (`LogWarning`) and the loop
  continues.

### Option — `src/Iris.Server/ActivityPubServerOptions.cs`

- New `TimeSpan ObjectInteractionRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);` (bound
  from `Iris:ObjectInteractionRefreshInterval` in `AddActivityPubServer`).

### Read path — `src/Iris.Server/ActivityPubServerExtensions.cs`

- The object-document handler now calls `TryReadStoredCounts` (new helper) to serve the pre-computed
  counters when present, falling back to the existing per-read sweep otherwise.
- The collection-page enrichment builds a `needsCountFallback` list — only objects lacking stored
  counters are batch-fetched for the sweep — and prefers `TryReadStoredCounts` per item.

### Registration — `src/Iris.Server/ActivityPubServerExtensions.cs`

- Registered with `services.AddHostedService(sp => new ObjectInteractionCountRefreshService(sp
  .GetService<IPersistenceProvider>(), ...))` in the **main** `AddActivityPubServer(services,
  configure)` overload (the one `Iris.Web` calls via `WebAppFactory.cs:300`).
- **The key bug fixed:** the service was first registered with
  `services.TryAddSingleton<IHostedService>(...)`. `TryAdd` only adds if the service type
  `IHostedService` is not already registered — so with many hosted services each using that pattern,
  **only the first one is registered**. `AddHostedService` (a `TryAddEnumerable` of `IHostedService`)
  is the correct pattern so multiple hosted services coexist. This is why the service silently never
  ran under the Web host until the registration was corrected.

## Verification

- **Build:** `dotnet build -c Release` — 0 warnings / 0 errors (whole solution incl. `Iris.Web` +
  all test projects).
- **Tests:** full `dotnet test -c Release` — all suites green (0 failures):
  Iris.Core.Tests 446, Iris.Web.Tests 106, Iris.Client.Tests 187, SampleServer.Tests 38,
  Iris.Server.Data.Tests 11, Iris.WebCrypto.Tests 3, Iris.Testing 12, SampleBlazorClient.Tests 17,
  Iris.LiveInterop.Tests 24, Iris.Client.Extensions.Tests 29, Iris.Server.Tests 1300 (16 skipped).
  Targeted read-path filters: object-document/interaction/collection/engagement 128/128;
  feed/enrich/outbox/directory 208/219 (11 skipped).
- **Live (docker, `irisweb-iris-web-1`, `irisweb-db-1`):**
  - The service runs (DIAG-confirmed during development, then removed): startup pass listed 5823
    objects, 5816 with resolvable IRIs; **pass 1 updated 5807, pass 2 (30s later) updated 0** —
    idempotent convergence.
  - `SELECT count(*) FROM "Objects" WHERE "Document" ? '…/ns#likedCount'` = **5816/5816**.
  - Object doc served over the wire carries the cached counters:
    `likedCount=1, sharedCount=0, repliedCount=1, dislikedCount=0, score=1` — matching the `Edges`
    table for that object (Kind 1 = Like ×1, Kind 3 = Reply ×1).
  - 0 new console errors in the browser (only the pre-existing favicon 404).

## Files

- `src/Iris.Server/Stores/ObjectInteractionCountRefreshService.cs` (new)
- `src/Iris.Server/ActivityPubServerOptions.cs`
- `src/Iris.Server/ActivityPubServerExtensions.cs`
- `PLAN.md`, `docs/ROADMAP.md`, `docs/plans/production-app-feature-matrix.md`
