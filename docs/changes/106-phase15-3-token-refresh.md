# 106 — Phase 15.3: Token refresh support (grant_type=refresh_token)

> 2026-08-30 · Phase 15.3 · `Iris.Server`

## What was built

Token refresh support for the OAuth2 token endpoint (RFC 6749 §6). The token endpoint
(`POST /ap/v1/oauth2/token`) now handles two grant types:

- **`grant_type=authorization_code` + `code`** — redeems the code (one-time), issues a random
  Bearer token + a random refresh token, stores both, and returns
  `{ access_token, token_type: "bearer", refresh_token }`.
- **`grant_type=refresh_token` + `refresh_token`** — redeems the refresh token (one-time,
  rotation), issues a new Bearer token + a new refresh token, stores both, and returns the same
  shape.

An unrecognized grant type returns `400 unsupported_grant_type` (RFC 6749 §5.2).

## Key types

- **`IOAuthTokenStore`** — extended with `StoreRefreshTokenAsync` + `RedeemRefreshTokenAsync`
  (one-time, rotation). The refresh token is a random opaque string, keyed by the actor IRI.
- **`InMemoryOAuthTokenStore`** — implements the new methods with a third
  `ConcurrentDictionary<string, Iri>`.
- **`OAuthTokenHandler`** — updated to handle both grant types. The response now always includes
  `refresh_token` (in addition to `access_token` + `token_type`).

## Tests

5 new integration tests in `OAuthTokenEndpointIntegrationTests`:
- `authorization_code` returns a `refresh_token` in the response.
- `refresh_token` exchanges for a new token pair (different from the original; the old refresh
  token is rotated/removed).
- `refresh_token` is one-time (second use with the same rotated token → 400 `invalid_grant`).
- Unknown `refresh_token` → 400 `invalid_grant`.
- Missing `refresh_token` → 400.

The existing `WrongGrantType` test was updated: `invalid_request` → `unsupported_grant_type`
(the correct RFC 6749 §5.2 error for an unrecognized grant type).

Test count: 587→592 in `Iris.Server.Tests`; 1012→1017 total.

## Decision: refresh token rotation

The refresh token is **rotated** on each use (RFC 6749 §6 recommends rotation; OAuth 2.1 makes it
mandatory). `RedeemRefreshTokenAsync` removes the old refresh token from the store after a
successful refresh, so a stolen refresh token can only be used once. The client must use the new
refresh token returned by the token endpoint for subsequent refreshes. This is stricter than
RFC 6749 (which allows non-rotation) but aligns with OAuth 2.1 and the industry best practice
(Microsoft, Google, and other major IdPs all rotate refresh tokens).

## Decision: always return refresh_token

The token endpoint always returns a `refresh_token` in the response (both for
`authorization_code` and `refresh_token` grants). This simplifies the client: the client always
stores the latest `access_token` + `refresh_token` pair, regardless of which grant type was used.
The alternative (only return `refresh_token` on the initial grant, not on refresh) is more
complex for the client and provides no security benefit (the refresh token is already rotated).

## Files changed

- `src/Iris.Server/Security/IOAuthTokenStore.cs` — extended with refresh-token methods
- `src/Iris.Server/Security/InMemoryOAuthTokenStore.cs` — implemented refresh-token methods
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `OAuthTokenHandler` handles both grant types
- `tests/Iris.Server.Tests/OAuthTokenEndpointIntegrationTests.cs` — 5 new tests + 1 updated
