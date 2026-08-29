# 039 — Sample Docker topology

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The project needed a minimal, real deployment topology to prove that two instances could advertise routable base URIs, discover each other by hostname, and exchange signed federation traffic over a real network.

The solution had to stay simple enough to run locally and in CI while still proving the critical part of the deployment story. That ruled out a full relay topology and avoided unnecessary client containerization.

## Decision

Iris uses a two-instance sample topology:

- two `SampleServer` containers share the same image but differ by environment values
- each container advertises its own routable base URI through `Iris__HostName`, `Iris__Port`, and `Iris__Actor`
- the containers join a shared Docker bridge network (`iris-net`)
- they reach each other using Docker DNS rather than a relay
- the Blazor client stays a console composition root rather than a WASM container in this phase

## Alternatives considered

### 1. Add a relay node

This would add more moving parts than the system actually needs for the deployment check. Two instances are sufficient to prove real cross-instance federation.

### 2. Containerize the Blazor app as a WASM host

This creates a heavy, flaky runtime and does not add new federation coverage for the in-loop work. The client path is already exercised through the in-process multi-instance tests.

### 3. Use a single instance with port mapping only

This would not prove cross-instance federation, which is the key requirement for the sample Docker topology.

## Consequences

- The deployment boundary is validated in a real network topology.
- Cross-instance discovery and message delivery are tested without requiring a relay.
- The sample remains easy to debug and inexpensive to run.
- The client runtime remains a composition root, which keeps the sample focused on federation rather than browser hosting complexity.

## Code alignment

The implementation reflects the decision:

- `docker-compose.yml` defines the two same-image server instances on a bridge network
- each instance advertises its own public base URI via environment values
- the sample smoke test checks cross-container reachability and WebFinger discovery

This is the minimal topology needed to verify the real deployment model without unnecessary infrastructure.
