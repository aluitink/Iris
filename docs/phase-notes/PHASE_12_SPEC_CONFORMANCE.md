# Phase 12 — Spec conformance and missing-feature closure

> Extracted from the historical changelog. The changelog now keeps a short pointer to this phase note.

## Overview

Phase 12 shifted the project from feature completion to correctness and compatibility. It included:

- a missing-feature audit against the ActivityPub / ActivityStreams / RFC 8615 / NodeInfo surface
- a severity-ranked fix plan
- a conformance suite to keep the server honest
- the last major feature gaps for Wave 1 and first Wave 2 items

## Slice 12.1 — Missing-feature inventory + spec audit

- Recorded the severity-ranked gap list in `docs/reference/MISSING_FEATURES.md`.
- This was a research-only pass; it did not change production code.
- It covered spec expectations for identity, routing, object updates/deletes, shared inbox, Like collection, and follow-feed surfaces.

## Slice 12.2 — Shared inbox

- Added instance-level `SharedInboxIri` support.
- The server now advertises `endpoints.sharedInbox` when configured.
- Delivery resolves an advertised shared inbox before falling back to `recipientIri.InboxOf()`.
- This closes the Wave 1 blocker item for shared-delivery semantics.

## Slice 12.3 — Update/Delete and object endpoint

- Added `UpdateActivityHandler` to refresh stored object content in place.
- Added `DeleteActivityHandler` to tombstone deleted objects instead of returning a `404`.
- Added `GET /ap/v1/o/{**path}` to serve stored objects and tombstones.
- This resolves the object-write path and object endpoint semantics.

## Slice 12.4 — Ed25519 support

- Unified signing around `ISigningKey`.
- Added a BouncyCastle-backed `Ed25519Key` implementation.
- Accepted Ed25519 keys inbound and signed outbound with them.
- This closes the major interop gap for servers like Pleroma that use Ed25519.

## Slice 12.5 — Move handler

- Added `MoveActivityHandler`.
- When an actor moves to a new IRI, local follow edges are repointed to the new IRI.
- The handler also invalidates stale key and actor-document cache entries.
- This closes the final Wave 1 `Move` gap.

## Slice 12.6 — Conformance tests + RFC 8615 fix

- Added regression tests for:
  - WebFinger at the RFC root path and the correct media type (`application/jrd+json`)
  - NodeInfo 2.0 structure
  - actor document content type and fields
  - shared inbox advertisement
  - outbound `ServerToServer` signature shape (`digest` + `content-type`)
- Fixed WebFinger to serve `application/jrd+json` instead of `application/json`.

## Slice 12.7 — Like + liked collection

- Added `ILikeStore` and `InMemoryLikeStore`.
- Added `LikeActivityHandler` for both local liker recording and community feed propagation.
- Added the `liked` collection endpoint and public docs advertisement.
- This closes the first Wave 2 feature gap.

## Slice 12.8 — Followed feed / home timeline

- Added the home timeline service that merges followed actors’ outboxes.
- It computes a deterministic, de-duped, capped feed from local + remote follows.
- This closes the most important user-facing feed feature after the basic read path.

## Result

Wave 1 is effectively closed; the project now has explicit regression coverage for conformance-sensitive semantics, and the major remaining work is in feature completeness and real-world interop testing rather than basic correctness.
