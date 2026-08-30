# 074 — Phase 8 S5: Base URL vs IRI host separation + instance base-URL config surface

> 2026-08-30 · Phase 8 (Sample) · Slice S5

## What was built

The "Docker-only routable" rule (SAMPLE_PLAN.md §4.4) is now a first-class config surface, and the
canonical two-host fact is pinned by a test. The **IRI host** (what the server *advertises* — the host in
IRIs in documents, which Docker DNS resolves between containers) and the **base URL** (what the
*browser* dials — a host-published port reachable outside the Docker network) are distinct, and the
explorer configures them separately. S3/S4 already implemented the *mechanism* (the session carries a
separate `dialBaseUri` and a WebFinger-resolved `ResolvedActorIri` whose host may differ); S5 adds the
**instance base-URL config surface** that lets a UI pre-fill the browser base URL for a known local
instance, plus the test that proves the split end-to-end.

## What changed

- **`samples/SampleBlazorClient/Explorer/InstanceBaseUrls.cs`** (new) — the instance base-URL map:
  advertised host (e.g. `iris-a`) → browser base URL (e.g. `http://localhost:8081`). Case-insensitive
  host key, `TryGet`/indexer read, `Set`/overwrite write, `Count`/`Hosts` for a UI picker. This is the
  config surface: a user logs on to `@alice@iris-a` and the UI looks up `iris-a` in the map to pre-fill
  the dial base, so the user only enters the address + password.
- **`samples/SampleBlazorClient/Explorer/ExplorerSession.cs`** — gains an optional
  `InstanceBaseUrls` (ctor param, defaulting to an empty map) exposed as the `BaseUrls` property, so the
  session and its UI share one map. `AddIrisExplorer(services, baseUrls)` overload registers the map and
  wires the `ExplorerSession` to it (falling back to a registered or empty map when none is supplied).
- **`samples/SampleBlazorClient/Pages/Home.razor`** — before logon, resolves the address's host against
  `Session.BaseUrls`; when the host has a known browser base URL it uses that (pre-fill), otherwise it
  uses the entered base. The transport-base vs advertised-IRI split is preserved: the dial base is what
  the browser reaches, the resolved actor IRI carries the advertised host.
- **`samples/SampleBlazorClient/packages.lock.json`** — gains the SDK-internal WASM assets entry (a
  by-product of the default WASM build; no new NuGet package).
- **Tests** — `tests/SampleBlazorClient.Tests/ExplorerTests.cs` gains 4 S5 facts:
  - `InstanceBaseUrls_TryGet_KnownHost_ReturnsBrowserBase` (known host, case-insensitive, unknown → false).
  - `InstanceBaseUrls_Set_OverwritesAndCounts` (re-set overwrites, count stays 1).
  - `LogOn_DialsOneBaseUrl_RequestsIrisCarryingAnotherHost` — **the canonical §4.4 fact**: the client
    dials `http://localhost` (browser base, port stripped) while the actor IRIs it requests carry
    `http://iris-a` (the advertised host). WebFinger resolves the address to the advertised-host IRI; the
    session authenticates as that IRI and a signed community-feed read succeeds over the single in-process
    transport, with the feed's community IRI carrying the advertised host.
  - `AddIrisExplorer_WithBaseUrls_RegistersMapAndSession` (DI overload wires the map into the session).

## Decisions

- **Config surface, not a global setting.** The base-URL map is a per-instance dictionary, not a
  single "browser base" setting, because a UI may talk to several local instances (iris-a, iris-b) each
  published on a different port. An empty map is valid — a user-supplied base URL at logon is always
  accepted, so the explorer remains usable for instances not in the map.
- **Case-insensitive host key.** Hostnames are case-insensitive; the map uses
  `StringComparer.OrdinalIgnoreCase` so `IRIS-A` and `iris-a` are the same entry.
- **Pre-fill, don't force.** `Home.razor` uses the map only to *pre-fill* the dial base; the entered base
  still wins when the host is unknown (or the user overrides). The advertised IRI host is never touched
  by the map — it comes from the WebFinger resolve (S4), keeping the two concerns independent.
- **The split already existed; S5 makes it a config + a test.** S3 introduced the separate
  `dialBaseUri` and S4 the resolved actor IRI; S5 does not change the request path — it surfaces the
  mapping in config and pins the behavior with the "dials one base, requests IRIs carrying another
  host" fact, which is the acceptance test the SAMPLE_PLAN slice calls for.

## Verification

- `dotnet build Iris.slnx` — 0 warnings / 0 errors; `samples/SampleBlazorClient` builds as a WASM app
  (default) **and** under `-p:ConsoleSmoke=true`.
- `dotnet test Iris.slnx` — all green: 865 total (`SampleBlazorClient.Tests` 25 → 29, 4 new S5 facts;
  all other projects unchanged).
