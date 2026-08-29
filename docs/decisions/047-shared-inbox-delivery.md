# 047 — Shared inbox delivery semantics

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The project needed a correct ActivityPub delivery model for federated actors that advertise a shared inbox. The earlier implementation assumed that every outbound delivery target could be resolved simply as `actorId.InboxOf()`. That breaks for real-world peers that publish `endpoints.sharedInbox` and expect senders to deliver there instead.

At the same time, the implementation needed to remain backward-compatible with actors that do not advertise shared inboxes and with test environments where the recipient document is not yet cached.

## Decision

Iris honors `sharedInbox` in both directions.

For served documents, a configured server-level `SharedInboxIri` is included under the public actor/community `endpoints` block when present. The normal per-actor inbox remains advertised as well, so a sender that ignores the shared inbox still lands in the correct place.

For outbound delivery, `DeliveryService.DeliverToActorAsync(...)` resolves the recipient's document and prefers `endpoints.sharedInbox` if it is present. If the document fetch fails, is absent, or does not advertise a shared inbox, it falls back to the conventional `recipientIri.InboxOf()` path.

The fetch is done at delivery time, not in the caller, so the inbox resolution is derived from the recipient's live document and can share the same cache path as the inbound key-resolution flow.

## Alternatives considered

### 1. Always send to `InboxOf()`

This is the conventional fallback, but it ignores a real peer behavior that is common in federated deployments and is part of the ActivityPub interoperability surface.

### 2. Require a custom API for every caller

This would spread delivery-policy logic across the codebase and would bypass the single decision point the server should own.

### 3. Cache shared-inbox state outside the document-fetch flow

This would increase inconsistency risk and may allow stale routing decisions to persist when the remote actor document changes.

## Consequences

- Outbound delivery becomes compatible with both direct inbox and shared-inbox actors.
- The delivery layer owns the routing decision and preserves a consistent fallback path.
- Remote document fetches and key fetches reuse the same cached federation context without requiring separate routing code.
- The implementation remains compatible with actors that do not advertise a shared inbox.

## Code alignment

The implementation reflects the decision:

- `ActivityPubServerOptions.SharedInboxIri` is the configuration surface
- public actor and community documents expose `endpoints.sharedInbox`
- `DeliveryService` resolves a recipient's document and prefers `sharedInbox` before falling back to the actor inbox
- a null document-fetcher or fetch failure keeps the old direct-inbox path working

This was a necessary interoperability fix for real federated peers and a blocker-level gap in the spec conformance audit.
