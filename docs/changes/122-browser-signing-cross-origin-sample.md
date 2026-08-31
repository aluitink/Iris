# 122 — Browser (WebCrypto) signing + cross-origin sample wiring

**Status:** DONE — full solution green (`dotnet build` 0 warnings; `dotnet test` 870/870).

## Objective

Make the Blazor WebAssembly explorer able to **sign outgoing requests in the browser** (so a logged-on
actor can compose/post through the WASM host) and make the **sample server reachable cross-origin** by
the WASM app (CORS + a decoupled advertise host). This is the S1-era feature set that the
already-committed `Iris.WebCrypto` library + `WebCryptoComposeSigningTests` were built against; the
committed library and test had no core plumbing to call into until this change.

## The problem

- **No browser RSA.** The .NET-on-WASM BCL cannot load an RSA private key, so a key whose signing
  backend is the browser's `crypto.subtle` (WebCrypto) must sign *asynchronously*. The signing pipeline
  (`ISignatureSigner.Sign` / `ISigningKey.Sign`) was synchronous-only, so a WebCrypto-backed key had no
  way to sign.
- **Fixed key loader.** `BasicAuthClientAuthenticator` / `OAuth2ClientAuthenticator` hard-coded
  `KeyPem.Load` (BCL/BouncyCastle), which cannot run on WASM — there was no seam to swap in a WebCrypto
  loader.
- **Same-origin only.** The WASM explorer dials the instance from the host-published port / public proxy
  origin (not the server's own origin), so the server had no CORS policy and the browser blocked the
  preflights. The server also advertised its IRIs from the *listen* host, which is wrong behind a reverse
  proxy that terminates TLS.

## Changes

### 1. Asynchronous signing surface (`src/Iris.Core`)

- **`ISigningKey.SignAsync(byte[], CancellationToken)`** — new interface method with a default
  implementation that defers to the synchronous `Sign` (correct for every BCL/BouncyCastle key). A
  WebCrypto key overrides it to await `crypto.subtle`.
- **`ISignatureSigner.SignAsync(metadata, identity, profile, ct)`** — new interface method with a default
  that defers to `Sign`.
- **`HttpSignatureSigner.SignAsync`** — override that awaits the key's `SignAsync`, so a WebCrypto key
  signs through the browser without touching the synchronous path; for a BCL key it is identical to
  `Sign`.
- **`SigningHandler`** (the client's async pipeline stage) now calls `_signer.SignAsync(…, ct)` instead of
  `Sign`.

### 2. Pluggable private-key loader (`src/Iris.Client/Auth`)

- **`BasicAuthClientAuthenticator`** + **`OAuth2ClientAuthenticator`** gain an optional
  `keyFactory` constructor parameter:
  `Func<string, KeyAlgorithm, Iri, CancellationToken, Task<ISigningKey>>?`. When supplied it replaces the
  default `KeyPem.Load` (a WASM host supplies the `WebCryptoSigningKeyFactory`); when `null` the default
  BCL/BouncyCastle loader is used (all existing callers keep working — the old constructor delegates with
  `keyFactory: null`). The `catch (ArgumentException)` that treats a load failure as "no key" now also
  covers the WASM BCL's `Arg_PlatformNotSupported` for RSA.

### 3. Cross-origin + advertise-host sample server (`samples/SampleServer/Program.cs`)

- **CORS:** `AddCors` with origins from `Iris__CorsOrigins` (comma-separated; default
  `http://localhost:8090`), any header/method, credentials enabled (so Basic/Bearer headers go out);
  `app.UseCors()` added after `UseRouting`.
- **Advertise host decoupled from listen host:** the container always binds `http://+:{port}` (so the
  compose port mapping + in-network peer base keep working); the *advertised* actor/community IRIs use
  `Iris__AdvertiseHost` / `Iris__AdvertiseHttps` / `Iris__AdvertisePort`, defaulting to the legacy
  `Iris:HostName` / `Iris:Https` / `Iris:Port` when unset (so a reverse proxy can terminate TLS for a
  public hostname without changing the listen address).

### 4. Serve the WASM platform's `.dat` ICU data (`samples/IrisStaticHost/Program.cs`)

`UseStaticFiles(ServeUnknownFileTypes = true)`: the WASM runtime's `icudt_*.dat` uses `.dat`, which has
no known MIME type, so a bare `UseStaticFiles()` 404'd it and the platform silently failed to start (the
index page still 200'd). Known types (`.js`/`.css`/`.wasm`) keep their content types; anything else is
`application/octet-stream`.

### 5. The browser signing bridge is wired (`samples/SampleBlazorClient`)

- `wwwroot/index.html` now loads `js/WebCrypto.js` (the `window.webcryptoSign` crypto surface + the
  `webcryptoSignBootstrap.install` entry point the C# side calls) before the Blazor bootstrap.
- `Actors.razor` / `Community.razor` / `Instance.razor`: Razor code-behind expression fixes
  (`disabled="@(Busy)"`, `@(Session.… ?? …)`) so boolean/nullable expressions bind correctly.

### 6. Docker smoke test proves the WASM platform assets are served (`scripts/docker-smoke-test.sh`)

The smoke test now fetches `_framework/blazor.webassembly.js` *and* an `icudt_*.dat` from the `iris-ui`
container and fails on a non-200 — guarding the regression where a 200 index page masked a 404'd `.dat`
that broke platform startup.

## Test coverage

- The committed **`WebCryptoComposeSigningTests`** is the in-process proof: it logs on as an actor and
  posts a note through the client pipeline using a stand-in `ISigningKey` whose `SignAsync` performs
  RSASSA-PKCS1-v1_5 + SHA-256 (exactly what WebCrypto's `crypto.subtle.sign` does), loaded through the
  `keyFactory` seam — and the server's `HttpSignatureValidator` accepts the signature. A second case
  (`keyFactory: null`) proves the default BCL path still validates. Both pass with this plumbing.
- Full solution green: **870/870**.

## Notes

- The async surface is additive (interface methods with defaults), so server-side signers and every
  existing caller compile unchanged; `SigningHandler` is the only call site that moved to `SignAsync`.
- `Iris.WebCrypto` (the browser library) + its test were committed earlier; this change is the core
  plumbing they call into.
