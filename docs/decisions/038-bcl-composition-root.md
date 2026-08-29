# 038 — BCL-only client composition root

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The client extension package needs to supply a ready-made ActivityPub client pipeline for a Blazor-hosted app without introducing a direct dependency on `Microsoft.Extensions.DependencyInjection`.

That matters for two reasons:

1. The package is intended to stay lightweight and WASM-safe.
2. The composition graph is small and fixed, so a DI container is not required to make the design usable.

The package therefore needed a plain composition model that still provides the same session/key/client behavior as a DI-driven host would.

## Decision

Iris exposes a BCL-only composition root rather than a `IServiceCollection` extension package.

The design is:

- `IrisClientBuilder.Create(options)` builds the graph
- `.WithAuthenticator(...)` and `.WithKeyStore(...)` customize the session and key storage
- `.Build()` returns an `IrisClientBundle`
- the bundle exposes `Session`, `ClientFactory`, `KeyStore`, `KeyProvider`, and a convenience `CreateClient(...)`

This keeps the graph testable without a container and also keeps the package dependency surface minimal.

## Alternatives considered

### 1. Put `AddIrisClient(...)` directly in the package

This would require a DI dependency and would broaden the package's intended scope. It is not needed for the core client-composition scenario and weakens the WASM-safe story.

### 2. Require callers to hand-wire `IKeyStore`, `IKeyProvider`, and `IActivityPubClientFactory` themselves

This pushes too much composition detail into callers and makes the session lifecycle harder to reason about.

### 3. Keep the composition in the server package

This would mix the client-hosting layer with server concerns and is the wrong separation of responsibilities.

## Consequences

- The client composition is simple enough to use from a Blazor or plain console app.
- The package stays dependency-light and portable.
- A host that wants a DI wrapper can build one around the bundle without the package itself depending on DI.
- Session/key lifetime remains explicit and easy to reason about for login, switch-account, and logout flows.

## Code alignment

The implementation reflects the decision:

- `IrisClientBuilder` and `IrisClientBundle` provide the fixed composition shape
- `IKeyStoreProvider`/`SessionKeyStoreProvider` share a single in-memory key store
- the pipeline config remains in the client options, not in a server-side extension package

This is the correct composition boundary for a lightweight client package.
