# 037 — Proxy route parameter and catch-all routing

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The Phase 6 proxy endpoint had to forward a target IRI that could contain path separators and still remain compatible with ASP.NET Core routing and the rest of the server's route group model.

The initial implementation was close to the correct shape, but the routing pattern and the lookup key were wrong in two separate ways:

- the catch-all parameter name must match the route template, not the path segment name
- the template must be written with the catch-all braces explicitly, not as a bare segment fragment

This surfaced while the proxy route was being wired under the `/ap/v1` group.

## Decision

Iris maps the proxy endpoint as a single catch-all route:

- route template: `/proxy/{**target}`
- route value key: `target`

The proxy handler reads `context.Request.RouteValues["target"]`, not `"proxy"`. The route is registered once on the `/ap/v1` group, avoiding the route ambiguity that would otherwise occur when the same template is mapped twice.

## Alternatives considered

### 1. Read `RouteValues["proxy"]`

This looks natural because the literal segment is `proxy`, but ASP.NET Core binds the catch-all value to the parameter name declared in the template (`target`). The result is `null` and a 404.

### 2. Use a non-catch-all template such as `/proxy/**target`

This is not valid for the .NET 10 route parser and produces a non-match; requests never hit the endpoint.

### 3. Register the same proxy template both on the group and directly on the endpoints builder

This creates an endpoint ambiguity and raises a routing error instead of a 404.

## Consequences

- The proxy endpoint accepts the entire target URI path without losing segments.
- The route remains stable and easy to test because the target value is explicitly named.
- Future proxies or relays can reuse the same pattern without hidden routing traps.
- The route group model stays consistent with the rest of the server's versioned mapping.

## Code alignment

The current implementation follows the decision:

- the proxy route is mapped once under the versioned route group
- the target is read from `RouteValues["target"]`
- the endpoint matches the full remaining target path, including slashes

This is the required ASP.NET Core behavior for a valid proxy catch-all route.
