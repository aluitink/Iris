# 103 — Phase 15.1: BearerTokenCredentialValidator (Bearer-token auth seam)

> 2026-08-30 · Phase 15.1 · `Iris.Server`

## What was built

`BearerTokenCredentialValidator` — a drop-in `IActorCredentialValidator` implementation that
validates `Authorization: Bearer <token>` headers. The token→handle resolution is delegated to a
host-provided delegate (`Func<Iri, string, ValueTask<string?>>`), so the token format and storage
are host-specific (an OAuth2 token table, a static API-key map, etc.).

This is the first step of Phase 15 (Auth Hardening): Bearer-token auth alongside the existing
`BasicAuthCredentialValidator`. The host app swaps the `IActorCredentialValidator` registration to
change the auth scheme — no other code changes needed (the seam was designed for this in Phase 3).

## Key types

- **`BearerTokenCredentialValidator`** (`src/Iris.Server/Security/BearerTokenCredentialValidator.cs`)
  — implements `IActorCredentialValidator`. Parses the `Authorization: Bearer` header, extracts the
  token, and delegates to the host's token-resolution delegate. Returns the authenticated handle on
  success, null on failure (missing header, wrong scheme, empty token, or the delegate returning null).

## Tests

7 integration tests in `BearerTokenAuthIntegrationTests` (TestServer-based):
- Public document excludes `privateKey` (no auth).
- Valid Bearer token authenticates and includes `privateKey` + `keyAlgorithm`.
- Missing token rejects (public document, no `privateKey`).
- Invalid token rejects.
- Basic-auth header is rejected by the Bearer validator (scheme mismatch).
- Empty Bearer token rejects.
- Request for a different (non-existent) actor returns 404.

Test count: 567→574 in `Iris.Server.Tests`; 992→999 total.

## Decision: delegate-based token resolution (not a token store)

The validator does not own the token store. It delegates to a host-provided delegate, mirroring the
`BasicAuthCredentialValidator` pattern (which delegates to a `Func<Iri, string, string,
ValueTask<bool>>` credential check). This keeps the validator framework-agnostic and lets the host
app wire the token store to whatever backing it uses (in-memory dict, database, Redis, OAuth2 token
table). The delegate signature `Func<Iri, string, ValueTask<string?>>` (actor IRI + token → handle
or null) is async-aware (the token store may be remote) and returns the handle directly (not a bool),
so the validator can distinguish "valid token for a different actor" (null) from "valid token for
this actor" (handle).

## Files changed

- `src/Iris.Server/Security/BearerTokenCredentialValidator.cs` — **new**
- `tests/Iris.Server.Tests/BearerTokenAuthIntegrationTests.cs` — **new** (7 tests)
