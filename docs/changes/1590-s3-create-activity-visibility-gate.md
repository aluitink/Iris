# S3 — Create activity 404 on object-document endpoint (visibility gate)

- **Date:** 2026-09-20
- **Status:** done
- **QA finding:** `docs/qa/s03-object-detail-create-iri-404.md`

## Problem

Opening a local post's object-detail page via its Create-activity deep-link IRI
(`/object?iri=…/creates/{id}`) returned 404 "Object not found" + 1 console error, even though
the Create activity was stored in the `Activities` table.

## Root cause

The `ObjectDocumentHandler` visibility gate (line ~7199) applied
`VisibilityFilter.IsVisibleTo()` to the stored object/activity. For a Create activity, the
`to`/`cc` audience is the author's follower list (a named audience with no public sentinel),
and the Create has no `attributedTo`. An anonymous request therefore failed the visibility
check and got a 404.

The visibility gate is designed for **content objects** (Notes, Articles) whose `to`/`cc`
encodes privacy. Activities (Create, Announce, Like, …) are metadata wrappers — their
`to`/`cc` is a distribution list, not a privacy gate.

## Fix

Added `&& visObj is not Activity` to the visibility check in `ObjectDocumentHandler`
(`ActivityPubServerExtensions.cs:7199`). Activities are now served without the visibility
gate; the gate still applies to content objects (Notes, Articles, etc.).

## Files changed

- `src/Iris.Server/ActivityPubServerExtensions.cs` — added `is not Activity` to the visibility
  gate in `ObjectDocumentHandler`
- `tests/Iris.Server.Tests/ObjectEndpointIntegrationTests.cs` — new test
  `StoredCreateActivity_ServedByObjectDocumentEndpoint`

## Verification

- 1392 server tests passed (0 failed), +1 new test
- Live-verified: `GET /ap/v1/u/s7test/creates/06GBX3GP…` → **200** (was 404)
- Live-verified: `/object?iri=…/creates/…` in browser → renders the post, **0 console errors**
- Client's `ObjectDetail.razor` already derives `/replies`+`/likes`+`/shares` from the resolved
  Note IRI (`ContentIri`), not the activity IRI — so the collections load correctly
