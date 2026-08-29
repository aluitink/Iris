# 040 — Public FQDN and TLS provisioning

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

A public ActivityPub deployment cannot rely on an internal bind address. Other instances will fetch the actor IRI, WebFinger subject, NodeInfo URI, and inbox endpoints over the public network. If those values point at a non-routable address, federation fails even when the local server itself is healthy.

The implementation therefore had to separate the address the server binds to from the address it advertises to the rest of the federation.

## Decision

Iris keeps two addresses distinct:

- bind address: the internal HTTP listener (for example `http://127.0.0.1:8080`)
- advertised base URI: the public FQDN and scheme, configured via `ActivityPubServerOptions.BaseUri`

The operator must provide:

- a public FQDN and DNS records
- a TLS certificate managed by the reverse proxy
- a reverse proxy that terminates TLS and forwards to the server's internal listener

The public base URI must match the operator's externally reachable host or remote federation writes will fail.

## Alternatives considered

### 1. Advertise the internal bind address directly

This would work only for local testing and would break every remote fetch, because the remote instance would hit a private address that cannot route to the correct server.

### 2. Put TLS termination inside Iris itself

This is outside the server's scope. Iris is a service, not a public reverse-proxy or certificate manager.

### 3. Treat the public FQDN as optional metadata only

This is a functional bug, not a cosmetic issue. The advertised base URI is part of the actual federation surface.

## Consequences

- A real public instance can be deployed behind a reverse proxy without exposing internal bind details.
- Federation resolves to the correct host because the advertised IRIs round-trip to the correct server.
- The deployment runbook and operator responsibilities are clear and reproducible.
- The bind-vs-advertise split is an explicit architectural requirement for later live federation work.

## Code alignment

The current implementation and deployment docs reflect this split:

- `BaseUri` is treated as the externally advertised URI
- the bind address is internal-only
- the server advertises the public host in actor docs, WebFinger results, and NodeInfo references

This is the correct design for a public, federated instance and is reflected in the deployment-prep material.
