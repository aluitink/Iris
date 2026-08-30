# 108 — Phase 15.2 (remaining): OAuth2 `/oauth2/authorize` endpoint + Blazor WASM integration

> 2026-08-30 · Phase 15.2 (remaining) · `Iris.Server` + `samples/SampleBlazorClient` + `samples/IrisStaticHost`

## What was built

Completed the remaining Phase 15.2 work — the OAuth2 authorization endpoint (server) and the
Blazor WASM browser-flow integration (sample client):

### Server: `GET /ap/v1/oauth2/authorize`

`OAuthAuthorizeHandler` (in `ActivityPubServerExtensions.cs`) — the browser-redirect half of the
OAuth2 authorization-code flow (RFC 6749 §4.1):

- Reads `client_id` (the actor handle — the v1 model has no separate OAuth2 client registration),
  `redirect_uri` (must be absolute), and `state` (required, RFC 6749 §10.12) from the query.
- Auto-approves (no interactive consent screen — the v1 model), builds the actor IRI
  (`{base}/ap/v1/u/{client_id}`), and checks the actor exists in persistence.
- Issues a random 32-byte Base64 authorization code, stores it in the `IOAuthTokenStore` keyed by
  the actor IRI (one-time), and 302-redirects to `redirect_uri?code=…&state=…` (using `&` when the
  redirect URI already has a query).
- Error paths: missing `client_id`/`redirect_uri`/`state` or a non-absolute `redirect_uri` → 400
  `invalid_request`; an unknown actor → 400 `invalid_client`.

The code is redeemed exactly once at `POST /ap/v1/oauth2/token` (Phase 15.2a).

### Sample client: the browser flow

- **`OAuth2BrowserFlow`** (new, `samples/SampleBlazorClient/Explorer/`) — the client-side browser-flow
  helper: builds the authorize URL (`client_id` + `redirect_uri` + `state`), generates a CSRF
  `state` (`NewState`), builds the callback `redirect_uri`, parses the callback's `code` + `state`
  (`ParseCallback`), and exchanges the code for a Bearer token (`ExchangeCodeAsync`).
- **`SampleBlazorClient.CreateOAuth2ClientService`** — composes an OAuth2 (Bearer) client via
  `OAuth2ClientAuthenticator` (no Basic-auth proxy fallback, since the token authenticates directly).
- **`ExplorerSession.LogOnWithOAuth2Async`** — resolves the actor IRI (WebFinger → direct fallback),
  builds the OAuth2 client, and logs on (Bearer actor document + private key).
- **`Home.razor`** — a "Log on with OAuth2" button that navigates the browser to the authorize URL;
  on the `/callback` return it verifies `state`, exchanges the code for a token, and logs on. The
  dial base + handle are echoed on the callback URL so a hard reload still knows the target.
- **`IrisStaticHost`** — a `/callback` route that serves the WASM `index.html` so the SPA boots on
  the callback URL and reads `?code=…&state=…`.

## Key types

- **`OAuthAuthorizeHandler`** (`src/Iris.Server/ActivityPubServerExtensions.cs`) — the authorize
  endpoint handler (302 + one-time code).
- **`OAuth2BrowserFlow`** (`samples/SampleBlazorClient/Explorer/OAuth2BrowserFlow.cs`) — **new**
  static helper (authorize URL, state, callback parse, code exchange).
- **`SampleBlazorClient.CreateOAuth2ClientService`** (`samples/SampleBlazorClient/SampleBlazorClient.cs`)
  — the OAuth2 composition root (Bearer authenticator, no proxy fallback).
- **`ExplorerSession.LogOnWithOAuth2Async`** (`samples/SampleBlazorClient/Explorer/ExplorerSession.cs`)
  — the session's OAuth2 logon path.

## Tests

- 8 integration tests in `OAuthAuthorizeEndpointIntegrationTests` (TestServer-based): valid request
  (302 + code + state, code redeemable), redirect URI with existing query (ampersand), missing
  `client_id`/`redirect_uri`/`state` (400), unknown actor (400 `invalid_client`), code→token
  exchange (full flow), and one-time code (second exchange fails).
- 7 integration tests in `OAuth2BrowserFlowIntegrationTests` (TestServer-based, sample client):
  authorize URL shape, callback parsing, state uniqueness/URL-safety, the full
  authorize→token→`LogOnWithOAuth2Async`→signed `PostNoteAsync` flow (proof the private key loaded
  and the client signs as the actor), one-time code, bad code (null), and an invalid token (logon
  fails).

Test count: 1017 → 1032 total (8 server + 7 sample client). Full suite green.

## Decision: `client_id` is the actor handle

The v1 model has no separate OAuth2 client registration (no `client_id`/`client_secret` table). The
`client_id` query parameter is the **actor handle** the user wants to log on as (e.g. `alice`). The
server maps it to the actor IRI (`{base}/ap/v1/u/{handle}`) and checks existence. This keeps the
flow credential-less on the authorize hop (the browser is redirected with only the handle +
`redirect_uri` + `state`) and matches the Basic-auth model where the handle identifies the actor.
A future "client registration" (third-party app logon) would extend `client_id` to a registered
client that delegates to an actor; that is out of scope for v1.

## Decision: dial base + handle echoed on the callback URL

The WASM app is a static SPA — after the server's 302 to `/callback`, the browser does a **full page
load** (the SPA re-boots), so any in-memory state (the entered base URL, the handle) is lost. The app
therefore appends `?dial=…&handle=…` to the `redirect_uri` before the authorize hop; the server
appends `&code=…&state=…` on the return. `OnInitializedAsync` reads all four from the address bar, so
a hard reload on `/callback` still completes the logon. The `state` (generated per request, held in a
static) is still verified when present (same-tab flow) to guard against CSRF.

## Files changed

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `OAuthAuthorizeHandler` + route registration
- `tests/Iris.Server.Tests/OAuthAuthorizeEndpointIntegrationTests.cs` — **new** (8 tests)
- `samples/SampleBlazorClient/Explorer/OAuth2BrowserFlow.cs` — **new**
- `samples/SampleBlazorClient/SampleBlazorClient.cs` — `CreateOAuth2ClientService`
- `samples/SampleBlazorClient/Explorer/ExplorerSession.cs` — `LogOnWithOAuth2Async`
- `samples/SampleBlazorClient/Pages/Home.razor` — OAuth2 logon button + callback handling
- `samples/IrisStaticHost/Program.cs` — `/callback` route
- `tests/SampleBlazorClient.Tests/OAuth2BrowserFlowIntegrationTests.cs` — **new** (7 tests)
