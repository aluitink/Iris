# 072 — Phase 8 S3: SampleBlazorClient as a Blazor WASM server explorer (Deliverable B, scaffold + composition root)

> 2026-08-29 · Phase 8 (Sample) · Slice S3

## What was built

`samples/SampleBlazorClient/` is no longer a console composition root — it is a **Blazor WebAssembly
host** (Deliverable B of [SAMPLE_PLAN.md](../SAMPLE_PLAN.md)): a routed WASM app that logs on to an
ActivityPub instance by WebFinger address and explores the seeded instance over the real
`Iris.Client.Extensions` pipeline. The existing `SampleBlazorClient.CreateClientService` /
`ClientService` composition (Phase 7) is kept as-is and is now what the WASM session wraps. The
pre-existing `tests/SampleBlazorClient.Tests` pipeline facts stay green, and a new `ExplorerTests`
suite covers the new explorer surfaces in-process.

## What changed

- **`SampleBlazorClient.csproj`** — `Microsoft.NET.Sdk.BlazorWebAssembly`, net10.0. Adds the
  `Microsoft.AspNetCore.Components.WebAssembly` and `.DevServer` packages (versions in
  `Directory.Packages.props`; DevServer is build-time only, `PrivateAssets=all`, no runtime dep).
  Entry points are mutually exclusive by build tag so exactly one `Main` is ever in the assembly:
  the default build is the WASM host (`Program.cs`); `-p:ConsoleSmoke=true` compiles the console
  smoke entry (`ConsoleSmoke.cs`) and drops the `.razor` markup (the components reference the WASM
  host's `Program` type, so the console build is pure pipeline).
- **`Program.cs`** — the WASM host entry: `AddIrisExplorer()` DI + the routed `App` root component.
- **`ConsoleSmoke.cs`** — the original console pipeline `Main` (login → signed community feed →
  proxy fallback), now tagged so `dotnet run -p:ConsoleSmoke=true` still smoke-tests the pipeline.
- **`Explorer/WebFingerAddress.cs`** — the "log on by address" input: parses `@handle@host` (with
  optional `acct:` / leading-`@` prefixes and surrounding whitespace), exposes the dial `Scheme`
  (default `https`) and the `acct:` resource, and builds the actor IRI from the **address host**
  (not the dial base URI) — the base-URL-vs-IRI-host split (SAMPLE_PLAN §4.4).
- **`Explorer/ExplorerSession.cs`** — the WASM composition root. `AddIrisExplorer` registers a
  singleton `ExplorerSession` + the transport factory; the session wraps the `IrisClientBundle` and
  exposes log on by address (tearing down any prior identity), log out, `GetClient` (the signed,
  cache- and proxy-enabled client), and `RecentInstances` (newest-first, de-duplicated) so a UI can
  offer one-click instance switching.
- **App shell** — `_Imports.razor`, `App.razor` (`Router` + `ErrorBoundary`), `Layouts/MainLayout.razor`,
  `Pages/Home.razor`, `wwwroot/index.html`, `wwwroot/css/app.css` (minimal, no heavy UI framework).
- **`Directory.Packages.props`** — `Microsoft.AspNetCore.Components.WebAssembly` 10.0.0 and
  `Microsoft.AspNetCore.Components.WebAssembly.DevServer` 10.0.0.
- **`tests/SampleBlazorClient.Tests/ExplorerTests.cs`** — 17 in-process tests: `WebFingerAddress`
  parsing/IRI-building (plain/`@`/`acct:`/remote-host/whitespace/invalid/null; the address-host-not-
  dial-host IRI), `ExplorerSession` (log on by address → `IsLoggedIn`/`ActorIri`/`DialBaseUri` + a
  signed community-feed read; wrong password stays logged out; instance switching + recents
  de-dup; log out), and the `AddIrisExplorer` DI registration (transport factory + singleton). All
  run against an in-process `SampleServer` via an injected transport — no real port.

## Decisions

- **Keep the existing composition.** `CreateClientService` / `ClientService` are the correct bundle
  wiring and are already exercised by the Phase 7 tests, so the WASM `ExplorerSession` *wraps* them
  rather than re-implementing the pipeline. The transport is a `Func<HttpMessageHandler>` (injected)
  so the same session runs against the wire (WASM) or an in-process `TestServer` (tests).
- **Two entry points, one assembly.** The SDK default-globs every `.cs`, so the csproj only *removes*
  the entry point that does not belong to the active configuration (and, for the console build,
  disables the `.razor` item globbing). This avoids the `NETSDK1022` duplicate-`Compile` error an
  explicit `<Compile Include>` would cause, and keeps `dotnet run` (WASM) and `dotnet run
  -p:ConsoleSmoke=true` (pipeline smoke) both working from the same project.
- **S4 overlap noted, not over-claimed.** S3's stated scope is the scaffold + composition root + DI +
  app shell. Because the scaffold is meaningless without a way to log on, the session's log-on-by-
  address + recents + the `WebFingerAddress` parser (the S4 core, minus the WebFinger *resolve* step
  and the UI screens) are included and covered in-process. The remaining S4 surface (WebFinger
  resolve → `IDiscoveryService`, and the actual logon UI) is left for S4's screens (S6/S7).

## Verification

- `dotnet build Iris.slnx` — 0 warnings / 0 errors; `samples/SampleBlazorClient` builds as a WASM app
  (default) **and** under `-p:ConsoleSmoke=true`.
- `dotnet test Iris.slnx` — all green: `SampleBlazorClient.Tests` 4 → 21 (17 new), 644 total.
