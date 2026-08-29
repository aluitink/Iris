# 043 — Dedicated `Create` handler owns recipient semantics

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The server needs to handle inbound `Create` activities for two different recipient types:

- a person actor: the activity is a local post and should be recorded in the author's outbox
- a community: the activity should be recorded in the members' outboxes so it appears in the community feed

A generic `Activity` catch-all alone cannot cleanly represent both semantics without conflating them.

## Decision

A dedicated `CreateActivityHandler : ActivityHandlerBase<Create>` takes precedence over the generic activity handlers.

That handler owns the recipient decision:

- if the recipient is a local person: record the object in the person's outbox
- if the recipient is a local community: record the content in each member outbox using the shared community recorder
- otherwise: no-op

This is intentionally distinct from the generic community inbox behavior, which handles community content propagation for activities such as likes and announces.

## Alternatives considered

### 1. Expand the generic community handler to also write to a person outbox

This conflates person and community semantics in one path and makes the handler's purpose ambiguous when it is responsible for both content propagation and author-owned recording.

### 2. Ignore the specific `Create` type and let the generic handler decide

That would silently drop the author post case or force a catch-all implementation to know about person-vs-community branching.

### 3. Keep the event decision inside the inbox processor

This would make the routing logic more special-case-heavy and harder to test. The handler boundary is a cleaner ownership point.

## Consequences

- Recipient decisions are centralized in a specific, testable handler.
- The shared community content recorder is reused across person and community flows.
- Generic handlers remain general-purpose and do not absorb content-routing edge cases.
- The system can evolve the community post path without redefining the local author path.

## Code alignment

The implementation follows the decision structure:

- typed `Create` handler wins over generic `Activity` handlers
- person and community recipient paths are distinct and partitioned by local actor/community lookups
- the shared `CommunityContentRecorder` is reused for the community branch

This preserves the separation between author-owned content and community feed aggregation.
