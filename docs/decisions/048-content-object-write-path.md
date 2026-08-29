# 048 — Content-object write path and tombstone semantics

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The server needed to support the ActivityPub object lifecycle for local content: creating an object, editing it, deleting it, and later serving the object by absolute IRI. The project initially stored inbound activities but did not persist the embedded object state in a way that allowed later retrieval or mutation.

This created a real gap: a local `Create` could be accepted but not re-read, and a later `Update` or `Delete` would have no durable object to rewrite or tombstone.

## Decision

Iris stores embedded object content under the object's own IRI when the recipient is a local actor and the object is a true embedded object. `Update` operations refresh the stored object in place; `Delete` operations replace the stored object with an AS2.0 `Tombstone` rather than removing it outright.

The object-document endpoint then serves:

- the stored object for a live IRI
- the `Tombstone` for a deleted IRI
- 404 for an unknown IRI

This preserves object identity across updates and deletions while remaining compatible with the ActivityPub expectation that a deleted object can still be resolved as a tombstone rather than disappearing.

## Alternatives considered

### 1. Hard-delete the object on `Delete`

This loses object identity and breaks the requirement to serve a tombstone for the old IRI. It also prevents clients from observing that the resource existed and was removed.

### 2. Ignore `Update`/`Delete` entirely for local content

This preserves the original bug and would make the object model effectively write-only.

### 3. Store only activity metadata and not the embedded object

This makes later object retrieval impossible and prevents object rehydration by IRI.

## Consequences

- Local content can be fetched by IRI via the object endpoint.
- Updates rewrite the same object identity instead of creating a stale duplicate.
- Deletes remain observable and resolvable as tombstones.
- The server enforces ownership: local actors may update or delete only objects they own.
- This is the minimum durable object model needed for a real ActivityPub object lifecycle.

## Code alignment

The implementation follows the decision:

- `CreateActivityHandler` persists embedded objects in `IObjectStore`
- `UpdateActivityHandler` rewrites the stored object in place
- `DeleteActivityHandler` produces a `Tombstone` instead of a hard remove
- `GET /ap/v1/o/{**path}` serves the object or tombstone by reconstructed IRI

This is the core of the object-identity and tombstone behavior required for spec-conformant content mutation.
