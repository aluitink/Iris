# 051 — Spec-conformance surface is protected by regression tests

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The implementation had already proven functional behavior across follow, delivery, posting, and federation. What it lacked was a guardrail around the exact wire format that remote peers and spec checks rely on: content types, JRD structure, NodeInfo fields, actor-document shape, and the outbound HTTP signature base.

The risk was that a change could keep the behavior working while quietly violating the required ActivityPub / RFC 8615 / NodeInfo surface for real-world peers.

## Decision

Iris treats the spec surface as a first-class contract and protects it with explicit conformance tests, not just by behavior tests.

This includes:

- WebFinger responses served as `application/jrd+json`
- JRD `subject` and `self` link structure
- NodeInfo 2.0 fields and status codes
- actor-document JSON-LD and endpoint metadata
- `sharedInbox` exposure when configured
- the outbound `ServerToServer` signature base for a body-carrying request, which must include `digest` and `content-type`

The project keeps these assertions as build-time regression tests so a wire-format drift fails immediately.

## Alternatives considered

### 1. Rely only on functional federation tests

This would miss the exact response media type and header shape that real peers inspect before they will accept a server.

### 2. Make the server permissive and accept anything inbound

This would hide invalid wire formatting and weaken interoperability with strict peers.

### 3. Skip the conformance suite until later phases

This would leave a hidden specification gap and would make the project harder to validate against real-world implementations.

## Consequences

- The server is guarded against silent protocol regressions.
- WebFinger and NodeInfo remain compliant with the broadest peer expectations.
- Outbound signatures continue to satisfy the stricter ActivityPub signing profile.
- The implementation remains easy to validate against future interoperability targets.

## Code alignment

The project reflects the decision in its server-level conformance checks:

- `ConformanceSuiteTests` assert JRD and NodeInfo output shape
- `OutboundSignatureConformanceTests` verify the actual on-the-wire `Signature` header fields
- the WebFinger content-type fix is captured as part of the same regression net

This keeps the project aligned with the protocol contract rather than only the happy-path federation flow.
