# 1581 — S3: Object-detail 404s a local post's collections (Create-activity IRI)

**Severity:** S3
**Status:** Fixed

## Problem

Opening a local post's object-detail page via its deep-link IRI (`/object?iri=…/creates/{id}`)
404'd the post's `/replies`, `/likes`, and `/shares` collections (3 console errors) because
the page derived those collection IRIs from the raw `?iri=` activity IRI, but the server serves
those collections only for the **stored Note object** IRI (`…/notes/{id}`), not the Create
activity IRI.

## Fix

`ObjectDetail.razor`: added a `ContentIri` property that resolves to the subject object's IRI
(the Note/Article) when the doc is a Create/Update activity, falling back to the raw `ObjectIri`.
All collection walks (`LoadRepliesAsync`, `LoadEngagementAsync`, `LikesCollectionIri`,
`SharesCollectionIri`, `ReplyHref`) now use `ContentIri` instead of `ObjectIri`.

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- Live-verified (fresh browser context):
  - Navigated to `/object?iri=…/creates/06GBVE2MK1DR0R71BSDQ6P61HM` (the S12a test note's
    Create-activity IRI).
  - Page renders the post content, "No replies yet." in the Replies tab.
  - **0 console errors** (previously 3: 404s on the activity IRI's collections).
  - The Note's collections (`…/notes/…/replies|likes|shares`) return 200; the client now
    walks those instead of the activity's (which still 404 server-side).
