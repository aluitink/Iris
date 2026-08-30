# 080 — Phase 8 S9: WASM Dockerfile + `iris-ui` compose service

> 2026-08-30 · Phase 8 (Sample) · Slice S9 (WASM Dockerfile + `iris-ui` compose service)

## What was built

The Blazor WebAssembly "server explorer" (Deliverable B) is now a buildable, runnable Docker image and
a first-class member of the `docker-compose` stack: a multi-stage `Dockerfile` produces the
`iris-ui` container — a minimal ASP.NET Core static-file host serving the published WASM site on port
`8090` — and `iris-ui` joins `iris-a`/`iris-b` on the internal `iris-net` network (routable as
`iris-ui`, host-published on `8090`). The stack is now three deployable, healthy services: the two
ActivityPub instances and the explorer UI.

This is the slice the SAMPLE_PLAN §5 calls out as the **ASP.NET Core static-file host** (not nginx):
the WASM app is a *static* site (the browser downloads `index.html` + `_framework` and runs the app
client-side), so the container's only job is to serve those files over HTTP. The `iris-ui` service makes
no outbound network calls of its own — all ActivityPub I/O is browser → server (the explorer dials the
two instances by their host-published base URLs, SAMPLE_PLAN §4.4).

## Key types & files

- **`samples/SampleBlazorClient/Dockerfile`** (new) — multi-stage:
  - *build* (`mcr.microsoft.com/dotnet/sdk:10.0`): restores then publishes **two** artifacts — the Blazor
    WASM site (`samples/SampleBlazorClient`, default build → `/wasm-site`, i.e. `index.html` +
    `_framework` + `css`) and the static-file host (`samples/IrisStaticHost` → `/app`).
  - *runtime* (`mcr.microsoft.com/dotnet/aspnet:10.0`): copies the host to `/app` and the WASM site's
    `wwwroot` to `/app/wwwroot` (the host's WebRoot); sets `ASPNETCORE_URLS=http://+:8090`; entrypoint
    `dotnet IrisStaticHost.dll`; `EXPOSE 8090`.
- **`samples/IrisStaticHost`** (new project) — a **minimal plain ASP.NET Core static-file host**
  (`Program.cs` + `IrisStaticHost.csproj`): `WebApplication.CreateBuilder` → Kestrel bound to
  `ASPNETCORE_URLS` (default `http://+:8090`), then `UseDefaultFiles` (resolves `/` → `index.html`) +
  `UseStaticFiles` (serves `index.html`, `_framework/*`, `css/*`) + `MapFallbackToFile("index.html")`
  (the SPA catch-all, so the explorer's client-side routes work on a hard reload). No Blazor markup, no
  outbound calls — it only serves the published WASM site.
- **`docker-compose.yml`** — new `iris-ui` service: builds from `samples/SampleBlazorClient/Dockerfile`,
  `hostname iris-ui`, `ports 8090:8090`, a TCP-connect health check on `8090`, on `iris-net`.
- **`Iris.slnx`** — adds the `IrisStaticHost` project to the `/samples/` folder.
- **`.dockerignore`** — no longer excludes the sample projects' sources (both sample Dockerfiles build
  from the root context, so both must be present in the build context).
- **`docs/reference/DEPLOYMENT.md`** — 3-service topology, routable-address table, files, and running
  notes updated from nginx to the ASP.NET Core static-file host.

## Why a separate static-host project (not a build tag of the WASM project)

The first attempt wired the static host in as a third build tag of `SampleBlazorClient`
(`-p:StaticHost=true`). The Blazor WebAssembly SDK pins the `browser-wasm` target
(`RuntimeIdentifier=browser-wasm` + `UseMonoRuntime=true`) and its own `Microsoft.AspNetCore.*`
extension-method namespace (its `UseUrls`/`UseDefaultFiles` extensions conflict with the real ASP.NET
Core ones), so overriding those to a CoreCLR server app was fragile and produced a runtimeconfig with
leftover `wasmHostProperties`. The static host is a plain *server* app (the WASM app runs in the
browser, not the container), so it is a separate `Microsoft.NET.Sdk.Web` project
(`samples/IrisStaticHost`) with no WASM-SDK involvement — it builds, publishes, and runs as a normal
CoreCLR app in the `aspnet` base image.

## Verification

- `IrisStaticHost` publishes cleanly to a `IrisStaticHost.dll` (CoreCLR, `Microsoft.AspNetCore.App`
  shared framework — no new NuGet package; the `aspnet` base image carries it).
- The `iris-ui` image builds and, when run, serves on `8090`:
  - `GET /` → `200 text/html` (the WASM `index.html`),
  - `GET /_framework/blazor.webassembly.js` → `200 text/javascript`,
  - `GET /css/app.css` → `200 text/css`,
  - `GET /_framework/Iris.Client.<hash>.wasm` → `200 application/wasm`,
  - `GET /actors` (a non-file SPA route) → `200 text/html` (the fallback to `index.html`),
  - host logs `Now listening on: http://[::]:8090`.
- `docker compose config` reports three services (`iris-a`, `iris-b`, `iris-ui`).
- Full solution builds with **0 warnings**; **883 tests green** (the new project adds no tests — the
  WASM explorer itself is already pinned by the S3–S8 in-process suites; per the SAMPLE_PLAN, no
  automated browser test is added in S9).

## Decisions

- **ASP.NET Core static-file host, not nginx.** The SAMPLE_PLAN §5 names a "minimal ASP.NET Core
  static-file host" on the `aspnet` base image. It matches the repo's image convention (the same
  `mcr.microsoft.com/dotnet/aspnet:10.0` base `SampleServer` uses), needs no new runtime, and the
  health check is the same TCP-connect probe the two server services use.
- **The host is dumb by design.** It serves files and a SPA fallback; it performs no ActivityPub
  I/O. The explorer's interop (signed federation, proxy fallback) is exercised from the browser
  against the two instances' host-published addresses, and is pinned in-process by the S3–S8 test
  suites that the Docker stack mirrors at the network boundary.
