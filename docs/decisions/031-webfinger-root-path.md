# 031 — WebFinger root-path compliance and discovery correctness

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

A remote instance resolves a handle such as `bob@host` through WebFinger. The standard path is the well-known URL at the host root, not a versioned application path. Before this decision, Iris served the WebFinger endpoint only under the versioned prefix, which meant standard clients and the built-in client resolver could not discover accounts properly.

The relevant code lives in [src/Iris.Server/ActivityPubServerExtensions.cs](../../src/Iris.Server/ActivityPubServerExtensions.cs) and [src/Iris.Client/WebFingerClient.cs](../../src/Iris.Client/WebFingerClient.cs).

## Decision

Iris serves WebFinger at both of these paths:

- `/ap/v1/.well-known/webfinger` for in-app compatibility and the internal route-group structure.
- `/.well-known/webfinger` for RFC 8410 compliance and standard host-level discovery.

The client-side resolver is also aligned to the root-path pattern: it builds the query from the account's own host and resolves `acct:` resources through the standard well-known URL.

## Alternatives considered

### 1. Only serve the versioned route

This seemed internally consistent but failed against standard ActivityPub clients and any remote instance that queries WebFinger the way the spec defines. It effectively made account discovery non-standard and prevented valid cross-instance lookup.

### 2. Serve only the root path and drop the versioned route

This is the correct spec-facing behavior, but the project intentionally keeps the versioned route for a cleaner internal route layout and easier local testing. The root path is the requirement; the prefixed route is a compatibility convenience.

### 3. Keep the client resolving against the calling server's base address instead of the account's own host

This is incorrect for multi-host federation and would make the client dependent on the current request environment. The decision preserves the RFC rule that resolution happens on the account's own host.

## Consequences

- Account discovery works with standard ActivityPub clients.
- Cross-instance federation can resolve `acct:` handles without special-casing Iris.
- The server exposes both a spec-compliant well-known endpoint and the project’s conventional versioned route.
- WebFinger caches remain consistent with the account's host-derived query logic.

## Code alignment

The current implementation reflects the decision:

- `MapActivityPubEndpoints()` maps the versioned WebFinger route under `/ap/v1`.
- It also maps `/.well-known/webfinger` directly at the root for standard discovery.
- `WebFingerClient` resolves using the account host and a `/.well-known/webfinger?resource=acct:...` query.

This is the compatibility bridge between the project’s internal route model and the ActivityPub ecosystem’s expected discovery contract.
