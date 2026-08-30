# 093 — Phase 12: F-29 — canonical `url` on served content objects

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Gap closure (F-29)

## What was built

The object-document endpoint (`GET /ap/v1/{**path}`) now ensures every served content object carries a
canonical `url` — the "view in browser" link. When the stored object has no `url`, it is set to the
object's own IRI (the canonical addressable form, since Iris serves the object at its IRI). An object
that already carries an author-provided `url` (e.g. a separate HTML page) keeps it. A `Tombstone`
(deleted object) is served as-is — it has no `url` to surface.

## The fix

Two additions in `src/Iris.Server/ActivityPubServerExtensions.cs`:

1. **`ServeObjectDocument(IObject, Iri)`**: serializes a stored object for the object-document endpoint,
   ensuring it carries a canonical `url`. A `Tombstone` is serialized directly (no `url` to add).
   Otherwise the object is deep-copied (via `ActivityJson.Deserialize<IObject>(ActivityJson.Serialize(obj))`)
   and, when it has no canonical `url` (`HasCanonicalUrl`), its `url` is set to a single `Link` whose
   `href` is the object's own IRI. The deep-copy avoids mutating the stored object — the `url` is a
   serving-time convenience, not stored state (the same technique the actor/community document handlers
   use).

2. **`HasCanonicalUrl(IObject)`**: returns true when the object already carries a non-empty `url` (an
   `ILink` with an absolute `href`) — an author-provided value that must not be overwritten.

The `ObjectDocumentHandler` now calls `ServeObjectDocument(obj, objectIri)` instead of serializing `obj`
directly.

## Tests

**`ObjectEndpointIntegrationTests`** (3 new integration tests):

- `StoredObject_ServedByIri_CarriesCanonicalUrl` — a stored `Note` with no `url` is served with a
  `url` equal to its own IRI (the library's link converter renders a one-element `Link` collection as a
  plain string, so the `url` is a string, not a `Link` object).
- `StoredObject_WithAuthorUrl_KeepsAuthorUrl` — an object stored with an author-provided `url`
  (e.g. `https://blog.example.com/posts/42`) is served with that `url` preserved (not overwritten).
- `ServedObject_DoesNotMutateStoredObject` — after serving an object, re-fetching it from the store
  shows its `url` is still `null` (the deep-copy prevented mutation of the stored object).

## Files changed

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `ServeObjectDocument` + `HasCanonicalUrl`;
  `ObjectDocumentHandler` now calls `ServeObjectDocument`.
- `tests/Iris.Server.Tests/ObjectEndpointIntegrationTests.cs` — 3 new integration tests.

## Decisions

- **Set the `url` at serve time, not store time.** The canonical `url` is a presentation convenience
  (a "view in browser" link). Setting it at serve time (on a deep-copy) means the stored object is
  never mutated, the IRI is always known at serve time (no need to recompute on store), and an
  author-provided `url` (a separate HTML page) is respected. This matches the actor/community document
  handlers' deep-copy pattern.
- **The object's own IRI is the canonical URL.** Iris serves a content object at its IRI (the object
  IRI IS the endpoint IRI — no separate HTML page). So when an object has no author-provided `url`,
  the canonical addressable form is its own IRI. A client offering "view in browser" can link to the
  object's IRI (which Iris serves as `application/activity+json`).
- **`url` serializes as a string.** The library's `ILink` converter renders a one-element collection of
  a single `Link` as a plain string (its `href` value), so the served `url` is a string, not a `Link`
  object. The tests assert accordingly.

## Test count

956 → 959 (+3), 0 failures.
