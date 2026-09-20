# 1574 S5 fix — drop the stale orphaned `localhost` actor from Search

**Date:** 2026-09-20
**Scope:** Server-only (`GlobalSearchService`). No client change.

## What was built

Closes [S5](../qa/s05-search-localhost-orphan-actor.md) (S2-sev, data-integrity): a local actor row
persisted under a **dev base** (`http://localhost:8088/ap/v1/u/alice`) — the dev default written while
the container booted with `Iris:AdvertiseBase` unset — surfaced in **Search** (the mixed,
`localOnly=false` path) as a ghost duplicate next to the same handle's canonical public-base row, but
not in **Directory** (which is local-only and already filtered by the instance base). Clicking the ghost
502'd (the container cannot reach the foreign base).

### Root cause

`GlobalSearchService` had two actor paths:

- **local-only** (Directory) — filtered local actors by the instance base IRI prefix, so the stale
  `localhost` row was already excluded there.
- **mixed** (Search, `localOnly=false`) — merged local + remote actors with only a `preferredUsername`
  heuristic and **no instance-base filter**, so the stale row leaked through.

### The fix

The mixed path now drops a local actor whose IRI is not **canonical for this instance**:

- New `IsSameInstanceActor(Actor)`: a **remote** actor (no `preferredUsername`) is always kept. A
  **local** actor (carries a `preferredUsername`) is kept only when its IRI begins with the instance
  base IRI (trimmed of a trailing `/`, case-insensitive). A local actor on a foreign base — the stale
  `localhost:8088` ghost — is dropped.
- When the instance base IRI is unavailable there is no canonical IRI to compare against, so every
  actor is allowed (the store's `preferredUsername` heuristic, if any, still applies at the store
  layer).

This is the precise S5 shape: the stale row is on the **same public host**, persisted under the dev
scheme/authority instead of the advertised `https` base. It is dropped; the same handle's canonical
public-base row remains. A genuine remote actor (different origin) is unaffected.

## Key types / files

- `src/Iris.Server/Services/GlobalSearchService.cs` — `IsSameInstanceActor` (NEW); the mixed actor
  path filters through it.
- `tests/Iris.Server.Tests/Services/GlobalSearchServiceTests.cs` — 3 new S5 tests + corrected the two
  pre-existing "remote actor with preferredUsername" expectations (a `preferredUsername` marks a local
  actor; a local actor on a foreign origin is not canonical, so it is dropped — that expectation was
  the S5 defect).

## Verification

- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test --filter "Category!=Slow"`: green across all projects —
  Iris.Core.Tests 467, Iris.Client.Tests 190, Iris.Server.Tests 1380, Iris.Web.Tests 106,
  SampleBlazorClient.Tests 17, SampleServer.Tests 38, Iris.Client.Extensions.Tests 29,
  Iris.Server.Data.Tests 20, Iris.WebCrypto.Tests 3, Iris.Testing 12.
- **Live verification via MCP Playwright (2026-09-20) — COMPLETE.** Rebuilt + redeployed the container
  (`irisweb-iris-web-1` recreated). Signed-in (registered a throwaway account):
  - **Search "alice"** → exactly **one** local alice card (the canonical
    `https://iris.luit.ink/ap/v1/u/alice`); the stale `http://localhost:8088/...` ghost is gone.
  - **Clicking it** → renders the full actor profile + **Posts (17)** tab (outbox), **no 502**.
  - The only console error is an unrelated 504 proxying a note from a *different, unreachable* dev
    instance (`iris-dev1.luit.ink`) — not the S5 stale row.

## Decision (recorded)

- A `preferredUsername` is the marker of a **local** actor. A local actor whose IRI is not under the
  instance base is, by definition, a stale/orphaned row and is dropped from the mixed path. A remote
  actor (no `preferredUsername`) is never dropped by this rule, so the search still lists legitimate
  cached remote actors. This is narrower than an origin comparison (which would have wrongly dropped a
  remote Mastodon user that carries its own `preferredUsername`) and exactly matches the S5 shape
  (same public host, dev base).
