# Iris.WebCrypto

Browser **WebCrypto** (`crypto.subtle`) RSA signing for Iris clients — for Blazor WebAssembly (and any
other .NET-on-JS-interop) host where the .NET BCL cannot load an RSA private key.

## Why

On a server the Iris client signs ActivityPub HTTP signatures with the BCL
(`rsa.SignData(data, SHA256, Pkcs1)`). In a Blazor WebAssembly app the .NET-on-WASM BCL has no usable
RSA implementation — `RSA.Create()` + `ImportFromPem` throws
`ArgumentException("Arg_PlatformNotSupported")` — so a browser client cannot sign with the private
key the way a server does.

The browser's **WebCrypto** is fully capable of the exact same primitive
(RSASSA-PKCS1-v1_5 + SHA-256), so `Iris.WebCrypto` delegates signing to `crypto.subtle` via JS interop.
Signatures produced in the browser verify on the server identically to BCL-produced ones.

## What it provides

- **`WebCryptoSigningKey`** — an `Iris.Core.Identity.ISigningKey` backed by `crypto.subtle`.
  `SignAsync` signs with RSASSA-PKCS1-v1_5 + SHA-256 (the BCL-equivalent primitive). The synchronous
  `Sign` and the public-key export methods are not available in the browser (the actor document
  already carries the server-issued public key).
- **`WebCryptoSigningKeyFactory`** — an `ISigningKeyFactory` (assignable to the authenticators'
  key-factory delegate) that captures your `IJSRuntime` and returns `WebCryptoSigningKey` instances.
- **`WebCrypto.js` bridge** — a small JS file (embedded in the assembly and packed as NuGet content).
  The host includes it once via a single `<script>` tag; it defines the signing surface and the named
  bootstrap entry point the C# side calls.

## Usage (Blazor WebAssembly)

**1. Include the bridge once** (one `<script>` tag in `index.html`). With the NuGet package the file
is copied into your `wwwroot/js/`; with a project reference keep your own copy (as the sample does):

```html
<script src="js/WebCrypto.js"></script>
```

**2. Build the client with the WebCrypto key factory** (in your WASM host):

```csharp
using Iris.Client.Auth;
using Iris.WebCrypto;

// Inject IJSRuntime and build the client with the WebCrypto factory:
var js = services.GetRequiredService<IJSRuntime>();
var keyFactory = new WebCryptoSigningKeyFactory(js);   // ISigningKeyFactory

// Pass it to the authenticator (the key-factory constructor parameter):
var authenticator = new BasicAuthClientAuthenticator(httpClient, actorIri, handle, password, keyFactory);
```

Or, with the Iris sample composition root, pass it through as the `keyFactory` argument of
`SampleBlazorClient.CreateClientService` / `CreateOAuth2ClientService`.

That's it — the client now signs HTTP signatures in the browser with the actor's private key.

## Notes

- **Why one `<script>` tag is required:** Blazor's `IJSRuntime` can only invoke *named* global
  functions — it rejects inline JS string expressions (verified: `InvokeAsync("() => 1 + 1")` throws
  `"is not a function"`). The library therefore cannot inject the bridge with zero host-side JS; it
  relies on the single named global (`window.webcryptoSignBootstrap`) that the host's `<script>` tag
  defines. The C# side then calls that named `install(source)` entry point, which re-injects the
  embedded bridge source if needed (covers lazy-load and post-navigation re-injection).
- **Algorithm:** RSA (RSASSA-PKCS1-v1_5 + SHA-256). This matches the BCL signer's default, so the
  server verifies browser signatures exactly as it verifies server signatures.
- **CSP:** because the bridge is a normal `<script src>` (not inline / not a `blob:` URL), a page
  Content-Security-Policy `script-src` needs no special allowance beyond what you already grant for
  your own static scripts.
- **Idempotent:** `install()` is a no-op if `window.webcryptoSign` is already defined, so calling it
  repeatedly (or after the host already loaded the bridge) is safe.
