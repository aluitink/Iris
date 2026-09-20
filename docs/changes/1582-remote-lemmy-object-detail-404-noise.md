# 1582 — S13: Remote Lemmy object-detail logs expected proxy 404s

**Severity:** S3
**Status:** Fixed

## Problem

Opening a remote Lemmy post (`/object?iri=https://lemmy.luit.ink/post/1`) logged 3 console
404 errors: `POST /ap/v1/proxy/https://lemmy.luit.ink/post/1/{replies|likes|shares}` all
404. Lemmy does not expose ActivityStreams collection endpoints — it serves post data via
`/api/v3/post?id=1`. The proxy faithfully relays the 404s, and the browser logs them as
console errors. The UI degraded gracefully but the 3 errors were noise.

## Fix

`ObjectDetail.razor`: added `IsLocalIri(Iri)` helper that checks whether an IRI is on the
local instance (by comparing against the session actor's `/ap/v1` prefix). In
`OnParametersSetAsync`, the collection walks (`LoadRepliesAsync`, `LoadEngagementAsync`) are
now skipped when the content object's IRI is not local — remote platforms that don't expose
AS collections no longer generate 404 console noise.

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass. `Iris.Client.Tests` 190/190 pass.
- Live-verified (fresh browser context):
  - `/object?iri=https://lemmy.luit.ink/post/1` → post renders ("Hello from Lemmy interop"),
    "0 comments", Replies/Likes/Shares tabs present. **0 console errors** (previously 3 ×
    404 on the proxy's `/replies|likes|shares` paths).
  - Local posts still load their collections correctly (S3 fix unaffected).
