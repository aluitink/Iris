# 010 — Versioned ActivityPub API and route-prefix stability

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

Iris needs a stable, interoperable API surface for local actors, federation, and well-known discovery. The server cannot safely keep an unversioned route set if later phases add or revise serialization semantics, collection shapes, or protocol fields. At the same time, the code must remain easy to operate in local tests and in real hosts with a public base URI.

The implementation now groups the ActivityPub endpoints under a route prefix and emits a meta header alongside the route itself. The code path is in `AddActivityPubServer()` and `MapActivityPubEndpoints()` in [src/Iris.Server/ActivityPubServerExtensions.cs](../../src/Iris.Server/ActivityPubServerExtensions.cs), and the version header constant is defined in [src/Iris.Server/ActivityPubServerConstants.cs](../../src/Iris.Server/ActivityPubServerConstants.cs).

## Decision

Iris versions the public API via a route prefix, not by a hidden convention or a post-hoc response header alone.

The canonical shape is:

- `/ap/v1/...` for ActivityPub endpoints, including actors, collections, and inbox routes.
- `Iris-Version` as a response meta header for observability and debugging.
- The route prefix is the authoritative versioning mechanism; the header is additive metadata.

This preserves compatibility for older integrations while allowing a new major protocol revision to be introduced as a new route prefix (for example, `/ap/v2/...`) without breaking existing clients.

## Alternatives considered

### 1. Version only in a header

This is simpler operationally, but it leaves the routing surface ambiguous. A client and a proxy both have to guess which handler is being asked for, and the same path shape cannot distinguish an older and newer API contract.

### 2. Version only in the path, with no header

This is viable for routing, but it makes debugging and deployment diagnostics less clear. We lose a quick signal for which protocol version a request hit, especially when multiple Iris services sit behind a shared ingress.

### 3. Version only in a query string or suffix

This creates poor ergonomics and does not match the way ActivityPub discovery is usually modeled. It also makes well-known routes and canonical actor IRIs less clean.

## Consequences

- Stable route families are easier to document and test.
- Endpoint evolution stays explicit and incremental.
- The public actor IRIs and the route prefix stay aligned with the advertised protocol version.
- Hosts can add a new major prefix later without disturbing existing clients or server logic.
- The version header remains useful for observability but is not the contract boundary.

## Code alignment

The current implementation matches the decision:

- `MapActivityPubEndpoints()` creates the `/ap/v1` group and maps the protocol endpoints beneath it.
- `ActivityPubServerConstants.VersionHeaderName` is set to `Iris-Version`.
- The same server code also maps the well-known WebFinger route at the RFC root, which is a compatibility add-on rather than a replacement for the versioned path.

The result is a protocol surface that is both explicit and easy to evolve.
