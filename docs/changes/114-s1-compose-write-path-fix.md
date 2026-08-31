# 114 — S1: Compose (signed Create) write-path fix + port 8088 / iris.luit.ink reconfig

> 2026-08-31 · Phase 8 (Sample) · Slice S1 (fix the broken Compose write path so it works in a real browser)

## What was built

The Compose screen (a signed `Create` POST to the acting actor's outbox) did not work in a real
browser. This slice fixes the write path end-to-end and reconfigures the explorer UI for port 8088
(the public FQDN `iris.luit.ink`) for HTTPS testing.

## Root cause (revised)

The original hypothesis was a WebCrypto signing defect. It was not. The WebCrypto crypto is
byte-identical to the BCL (PKCS#8 PEM import, RSASSA-PKCS1-v1_5 + SHA-256) — confirmed by the
passing in-process repro (`WebCryptoComposeSigningTests`). There were **three** distinct defects:

1. **WebFinger discovery dialed the wrong authority.** `ExplorerSession.LogOnAsync` dialed
   `/.well-known/webfinger` at `{address.Scheme}://{address.Host}` (e.g. `https://localhost`, port
   443), ignoring the explicit `dialBaseUri` port (`http://localhost:8081`) → `ERR_CONNECTION_REFUSED`.

2. **The proxy dropped the write.** The `ProxyHandler` built a `GET` and relayed only the `Accept`
   header — it dropped the client's request **method** and **body**. A proxied `POST` (Create) arrived
   as a bodyless GET-equivalent that only listed the outbox (200) instead of creating the activity.

3. **The proxy body relay was unreliable.** Even after adding a body relay, `context.Request.Body`
   was already consumed by an upstream component (the `SignatureValidationMiddleware` / the
   `SigningHandler`), so the relay read 0 bytes. A `Content-Length`-based presence check also failed
   (Kestrel reports 0 for the proxied write).

## The fixes

### 1. WebFinger discovery: dial the explicit authority

- Added a `ResolveActorAsync(string account, Uri dialBaseUri, CancellationToken ct)` overload to
  `IDiscoveryService`, `WebFingerDiscoveryService`, and `WebFingerClient`.
- `WebFingerClient.ResolveActorFromNetworkAsync` now builds the well-known URL authority from
  `dialBaseUri` when provided (else `{dialScheme}://{addressHost}`).
- `ExplorerSession.LogOnAsync` (Basic-auth path) and the OAuth2 path now pass `dialBaseUri`.
- Removed the now-dead `DialScheme` helper.

**Files:** `src/Iris.Client/Discovery/IDiscoveryService.cs`,
`src/Iris.Client/Discovery/WebFingerClient.cs`,
`src/Iris.Client/Discovery/WebFingerDiscoveryService.cs`,
`samples/SampleBlazorClient/Explorer/ExplorerSession.cs`.

### 2. Always-proxy mode (skip the guaranteed-401 direct attempt)

When the acting actor's **advertised** host differs from the **dial** host, a direct signed attempt
always 401s (the browser's signature cannot be validated against the advertised host). The
`ProxyFallbackHandler` now has an `AlwaysProxy` mode that routes signed **writes** (POST/PUT) straight
through the home proxy (which re-signs with the actor's key), skipping the wasted direct attempt.
Reads (GET) still go direct (the proxy is not a general-purpose GET relay and is not CORS-open to it).

- Added `bool AlwaysProxy` to `ActivityPubClientOptions`, `IrisClientOptions`, and
  `ProxyFallbackHandler` (new constructor param + extracted `SendViaProxyAsync`).
- Wired through `ActivityPubClientFactory` and `IrisClientFactory`.
- Computed in `SampleBlazorClient.CreateClientService` as
  `!string.Equals(actorIri.Uri?.Host, serverBaseUri.Host, OrdinalIgnoreCase)`.

**Files:** `src/Iris.Client/Pipeline/ProxyFallbackHandler.cs`,
`src/Iris.Client/ActivityPubClientOptions.cs`, `src/Iris.Client/ActivityPubClientFactory.cs`,
`src/Iris.Client.Extensions/IrisClientOptions.cs`, `src/Iris.Client.Extensions/IrisClientFactory.cs`,
`samples/SampleBlazorClient/SampleBlazorClient.cs`.

### 3. The proxy relays the method + body (the S1 write fix)

The proxy transport is always a `POST` to `/ap/v1/proxy/{target}` (the target IRI rides in the path).
The client now signals the **real** method of the request it wants made via an `X-Iris-Proxy-Method`
header and sends the activity as the body. The `ProxyHandler` relays that method + the buffered body.

- **Client** (`ProxyFallbackHandler.SendViaProxyAsync`): sends `X-Iris-Proxy-Method: {method}` and
  relays the original request's body (the Create activity) as the proxy POST's body.
- **Server** (`ProxyHandler`):
  - Reads the `X-Iris-Proxy-Method` header (defaulting to `GET` for legacy bodyless reads) and builds
    the forwarded request with that method.
  - Calls `context.Request.EnableBuffering()` at the top of the handler so the body is re-readable
    (an upstream component may have consumed the stream).
  - For a write (POST/PUT), resets `context.Request.Body.Position = 0`, reads the body into a buffer,
    and relays it via `ByteArrayContent`. Uses `TryAddWithoutValidation` for the content type (the
    inbound content type may carry a charset parameter the `MediaTypeHeaderValue` constructor rejects).

**Files:** `src/Iris.Client/Pipeline/ProxyFallbackHandler.cs`,
`src/Iris.Server/ActivityPubServerExtensions.cs`.

### 4. Port 8088 + CORS + UI reconfig

- `docker-compose.yml`: `iris-ui` now publishes `8088:8090` (in addition to `8090:8090`).
- `.env`: `IRIS_CORS_ORIGINS` now includes `http://localhost:8088`.
- `samples/SampleBlazorClient/Pages/Home.razor`: log-on defaults to `alice@iris.luit.ink` /
  `https://iris.luit.ink` with explanatory text (the public FQDN for HTTPS testing; the local
  instance uses `alice@localhost` + `http://localhost:8081`).
- `samples/SampleBlazorClient/Program.cs`: seeds `InstanceBaseUrls` with `iris.luit.ink →
  https://iris.luit.ink` and `localhost → http://localhost:8081`.

**Files:** `docker-compose.yml`, `.env`, `samples/SampleBlazorClient/Pages/Home.razor`,
`samples/SampleBlazorClient/Program.cs`.

## Tests

- **WebFinger discovery authority** (`WebFingerClientTests.cs`):
  - `Resolve_WithDialBaseUri_DialsExplicitAuthority_AndReturnsSelfLink`
  - `Resolve_WithoutDialBaseUri_DialsAddressHostOverScheme`
- **Always-proxy** (`ProxyFallbackHandlerTests.cs`):
  - `AlwaysProxy_RoutesStraightToProxy_WithoutDirectAttempt`
  - `AlwaysProxy_ReadsGotoDirect_NotThroughProxy` (a GET read dials direct, not through the proxy)
- **Proxy-Create relay** (`ProxyFallbackIntegrationTests.cs`):
  - `Proxy_Write_PostWithBody_IsRelayedAsPostToTarget` — a proxied POST + body reaches the target's
    outbox publish handler (a 401/202, not a 404/405/200-listing).
- **In-process repro** (`WebCryptoComposeSigningTests.cs`): proves the WebCrypto/BCL signing is
  byte-identical (the real defect was dial/authority + the proxy drop, not the crypto).

## Verification (local http path, port 8088)

- **Log-on**: WebFinger dialed `http://localhost:8081` (the explicit dial authority), resolved to
  `https://iris-dev1.luit.ink/ap/v1/u/alice`, community feed fetched 3 signed items (reads go direct).
- **Compose**: the signed Create POST went **straight** through the proxy
  (`POST http://localhost:8081/ap/v1/proxy/https://iris-dev1.luit.ink/ap/v1/u/alice/outbox`) with
  `X-Iris-Proxy-Method: POST` + the Create body → **202 Accepted**. The note landed in the outbox
  (`totalItems` incremented; the new note IRI is fetchable). No direct 401 attempt (AlwaysProxy active).
- **Test counts green**: Iris.Client.Tests 110, SampleBlazorClient.Tests 56,
  Iris.Client.Extensions.Tests 29, Iris.Server.Tests 656.

## Not verified (public HTTPS)

`https://iris.luit.ink` (443 and :8088) is unreachable from this environment. The public HTTPS path
can only be verified from the user's external browser. The local (http) path exercises the identical
WASM + WebCrypto signing pipeline, so the public path is expected to work the same way.
