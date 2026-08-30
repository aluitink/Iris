# 105 — Phase 15.2b: OAuth2ClientAuthenticator (Bearer-token auth flow)

> 2026-08-30 · Phase 15.2b · `Iris.Client`

## What was built

`OAuth2ClientAuthenticator` — an `IClientAuthenticator` implementation that authenticates with a
Bearer token (the client-side half of the OAuth2 authorization code + PKCE flow). Given a Bearer
token (obtained via the code→token exchange against `/ap/v1/oauth2/token`), it fetches the actor
document with an `Authorization: Bearer` header, reads the owner-only `privateKey` extension, and
loads it into an `ISigningKey`.

The token is obtained via a host-provided async delegate
(`Func<CancellationToken, ValueTask<string?>>`), so the token store is host-specific (e.g. the token
obtained via the browser redirect → callback → code exchange). This is the drop-in replacement for
`BasicAuthClientAuthenticator`: the host app swaps the `IClientAuthenticator` registration to change
the auth scheme.

## Key types

- **`OAuth2ClientAuthenticator`** (`src/Iris.Client/Auth/OAuth2ClientAuthenticator.cs`) —
  implements `IClientAuthenticator`. Takes an `HttpClient` + a token-provider delegate. On
  `AuthenticateAsync`, it resolves the token, fetches the actor document with a Bearer header,
  extracts the `privateKey` + `publicKey` + `keyAlgorithm` extensions, and loads the key via
  `KeyPem.Load`. Returns `AuthenticatedActor?` (null on any failure).

## Tests

5 integration tests in `OAuth2ClientAuthenticatorIntegrationTests` (TestServer-based, using
`BearerTokenCredentialValidator` + `InMemoryOAuthTokenStore` on the server side):
- A valid token (from the code→token exchange) authenticates and loads the key.
- A missing token returns null.
- An invalid token returns null.
- A revoked token returns null.
- A token for the wrong actor returns null (404).

Test count: 582→587 in `Iris.Server.Tests`; 1007→1012 total.

## Decision: token-provider delegate (not a token store)

The authenticator does not own the token store. It takes a `Func<CancellationToken,
ValueTask<string?>>` delegate that returns the current Bearer token (or null). The delegate is
async-aware because the token may need to be refreshed (Phase 15.3). This mirrors the
`BasicAuthClientAuthenticator` pattern (which takes the credentials in the constructor) but is more
flexible: the host app can wire the delegate to any token source (a static token, an OAuth2 token
table, a cookie, a keychain). The delegate returns `null` when no token is available (the
authenticator returns null, signaling an unauthenticated session).

## Scope: 15.2b (client-side authenticator only)

This slice covers the client-side `OAuth2ClientAuthenticator` — the CI-testable half of the OAuth2
flow (token → actor document → private key). The browser-based authorization code + PKCE flow
(redirect to `/oauth2/authorize`, handle the callback, exchange the code) is not CI-testable (it
requires a real browser + a real redirect). The `/oauth2/authorize` endpoint will be implemented
alongside the Blazor WASM client integration (the sample app drives the browser flow). The
`OAuth2ClientAuthenticator` is the drop-in point: the host app obtains the token (via the browser
flow) and passes it to the authenticator.

## Files changed

- `src/Iris.Client/Auth/OAuth2ClientAuthenticator.cs` — **new**
- `tests/Iris.Server.Tests/OAuth2ClientAuthenticatorIntegrationTests.cs` — **new** (5 tests)
