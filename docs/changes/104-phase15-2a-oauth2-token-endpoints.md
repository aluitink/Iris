# 104 — Phase 15.2a: OAuth2 token + revoke endpoints (code→token exchange)

> 2026-08-30 · Phase 15.2a · `Iris.Server`

## What was built

The server-side OAuth2 token endpoints — the CI-testable core of the OAuth2 authorization code +
PKCE flow (Phase 15.2). Two new endpoints + a pluggable token store:

- **`POST /ap/v1/oauth2/token`** — exchanges an authorization code for a Bearer token. The client
  sends form-encoded `grant_type=authorization_code` + `code`; the server redeems the code (one-time,
  removed after exchange), issues a random 256-bit Bearer token, stores it in the `IOAuthTokenStore`,
  and returns `{ access_token, token_type: "bearer" }`.
- **`POST /ap/v1/oauth2/revoke`** — revokes a Bearer token. The client sends form-encoded `token`;
  the server removes it from the store and returns 200 (RFC 7009: always 200, even for unknown
  tokens, to avoid leaking token validity).

## Key types

- **`IOAuthTokenStore`** (`src/Iris.Server/Security/IOAuthTokenStore.cs`) — interface for storing
  authorization codes + Bearer tokens: `StoreAuthorizationCodeAsync`, `RedeemAuthorizationCodeAsync`
  (one-time), `StoreTokenAsync`, `ResolveTokenAsync`, `RevokeTokenAsync`. The host app provides the
  backing store (in-memory, database, Redis).
- **`InMemoryOAuthTokenStore`** (`src/Iris.Server/Security/InMemoryOAuthTokenStore.cs`) — in-memory
  implementation using `ConcurrentDictionary<string, Iri>` for codes and tokens.
- **`OAuthTokenHandler`** + **`OAuthRevokeHandler`** (private static methods in
  `ActivityPubServerExtensions.cs`) — the endpoint handlers.
- **DI registration**: `TryAddSingleton<IOAuthTokenStore, InMemoryOAuthTokenStore>` in
  `AddActivityPubServer`.

## Tests

8 integration tests in `OAuthTokenEndpointIntegrationTests` (TestServer-based):
- Valid code exchanges for a Bearer token (`access_token` + `token_type: "bearer"`).
- A redeemed code is one-time (second exchange → 400).
- Unknown code → 400 `invalid_grant`.
- Wrong grant type → 400 `invalid_request`.
- Missing code → 400.
- A valid token can be revoked (resolvable before, not after).
- Revoking an unknown token → 200 (RFC 7009).
- A revoked token is no longer resolvable via the store.

Test count: 574→582 in `Iris.Server.Tests`; 999→1007 total.

## Decision: code is an opaque string (not an IRI)

The authorization code is a random opaque string (not an IRI), matching the OAuth2 spec (RFC 6749
§4.1.2: "The authorization server issues a temporary authorization code… delivered in the query
component of the redirect URI"). The client generates the code (or the server does at the
authorization endpoint) and passes it to the token endpoint. The `IOAuthTokenStore` keys by the code
string. The code is one-time: `RedeemAuthorizationCodeAsync` removes it after a successful exchange.

## Decision: always 200 on revoke (RFC 7009)

RFC 7009 §2.2: "If the client sends a request without proper authentication… the authorization
server responds according to Section 5.2 of RFC 6749… If the token is a valid… the authorization
server responds with HTTP status code 200." The key point: revoking an unknown or already-revoked
token still returns 200, so a caller cannot probe token validity.

## Scope: 15.2a (server-side token endpoints only)

This slice covers the server-side token exchange + revocation — the CI-testable core. The remaining
Phase 15.2 work (15.2b) is the **client-side** OAuth2 authenticator (`OAuth2ClientAuthenticator`
implementing `IClientAuthenticator`) that drives the browser-based authorization code + PKCE flow
(redirect to `/oauth2/authorize`, handle the callback, exchange the code, use the Bearer token to
fetch the actor document). The authorization endpoint (`/oauth2/authorize`) is a browser redirect
and is not CI-testable (it requires a real browser); it will be implemented in 15.2b alongside the
client-side authenticator.

## Files changed

- `src/Iris.Server/Security/IOAuthTokenStore.cs` — **new**
- `src/Iris.Server/Security/InMemoryOAuthTokenStore.cs` — **new**
- `src/Iris.Server/ActivityPubServerExtensions.cs` — endpoint mappings + handlers + DI registration
- `tests/Iris.Server.Tests/OAuthTokenEndpointIntegrationTests.cs` — **new** (8 tests)
