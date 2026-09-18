# 139.3-s6 Finding 2 (follow-up) — foreign-IRI object pages via the object-document catch-all

**Phase:** 139.3 — Data lifecycle & persistence review
**Item:** Up Next #1 (follow-up to Scenario 6, Finding 2)
**Date:** 2026-09-18
**Result:** **FIXED.** The object-document catch-all now accepts an explicit `?iri=` query parameter
that overrides the path-based IRI reconstruction. A stored **foreign** object (e.g.
`https://neuromatch.social/users/jonny/statuses/…`) — unreachable by path because the catch-all
reconstructs a *local* IRI — is now served by its exact absolute IRI via
`GET /ap/v1/object?iri={full-iri}`. Path-based lookup for **local** objects is unchanged.

## Root cause

Scenario 6 (offline rebuild) found that a **foreign-IRI** remote object page **404s** via the
object-document catch-all. The diagnosis: `ObjectDocumentHandler` reconstructs the lookup IRI as
`baseUrl + RoutePrefix + path` (a *local* IRI). A stored foreign object's IRI has a *different* host
(e.g. `https://lemmy.luit.ink/post/1`), so the local-IRI lookup misses and the endpoint returns 404.
The UI's `/object?iri=` Blazor page already covered the user-facing case (it fetches via the AP
client, which reaches foreign IRIs over the wire), so the gap was an **API-level** / **peer-level**
ability to fetch a stored foreign object by its exact IRI.

## What was built

A one-guard change in `ObjectDocumentHandler`
(`src/Iris.Server/ActivityPubServerExtensions.cs`, the object-document catch-all at
`MapGet("/{**path}", …)`): before the existing path-based reconstruction, the handler checks for an
absolute `?iri=` query parameter.

- **Present + absolute** → use it directly as the lookup IRI (the foreign-object case).
- **Absent, blank, or relative** → fall back to the existing `base + RoutePrefix + path` reconstruction
  (backward-compatible; a relative `?iri=` is ignored rather than trusted, since a stored foreign
  object always has an absolute IRI).

No route change, no API-surface change beyond the new (optional) query parameter, no new NuGet
packages, no dependency-direction changes.

## Tests

`tests/Iris.Server.Tests/ForeignObjectDocumentEndpointTests.cs` (6 tests, all passing) — a single
`TestServer` seeded with one stored **foreign** object and one stored **local** object:

- `ForeignObject_ByPath_Returns404` — the s6 F2 gap: a foreign object fetched by path 404s.
- `ForeignObject_ByExplicitIri_Returns200WithStoredObject` — the fix: the same foreign object fetched
  via `?iri=` returns 200 with its stored content and its foreign IRI as the document `id`.
- `LocalObject_ByPath_StillReturns200` — backward compat: a local object fetched by path still works.
- `LocalObject_ByExplicitIri_Returns200` — `?iri=` works for a local object too.
- `UnknownIri_ByExplicitIri_Returns404` — an `?iri=` naming an unknown object 404s cleanly.
- `RelativeIri_QueryParam_Ignored_FallsBackToPath` — a relative `?iri=` is ignored (falls back to the
  path-based reconstruction), not trusted.

Full suite green: `dotnet test --filter "Category!=Slow"` — 0 failures across all projects
(`Iris.Server.Tests` 1319 passed, +6 new).

## Live verification

Rebuilt the Docker app (`docker compose -f apps/Iris.Web/docker-compose.yml up --build -d iris-web`),
confirmed healthy (port 8088). Picked a stored foreign object from the live DB
(`https://neuromatch.social/users/jonny/statuses/117237800074111038`, a Mastodon `Note`):

| Request | Result |
|---|---|
| `GET /ap/v1/neuromatch.social/users/jonny/statuses/117237800074111038` (by path) | **404** (the s6 F2 gap — local-IRI reconstruction misses the foreign host) |
| `GET /ap/v1/object?iri=https%3A%2F%2Fneuromatch.social%2F…` (by `?iri=`) | **200**, `id` = the foreign IRI, `type` = `Note`, `attributedTo` = the foreign author |

## Decision recorded

**Why `?iri=` rather than a route change or documenting full-IRI addressing.** The finding offered
"serve stored foreign objects by an explicit `?iri=` lookup on the object endpoint, **or** document
full-IRI addressing." A route change (e.g. a new `MapGet("/foreign/{**path}")`) would be a larger,
less discoverable surface and would not match how the UI already addresses objects (the Blazor
`/object?iri=` page uses the `?iri=` convention). Documenting full-IRI addressing alone leaves the
API-level gap (a peer or raw inspector still cannot fetch a stored foreign object). The `?iri=`
parameter is the minimal, additive, backward-compatible fix that closes the API-level gap and is
consistent with the existing UI convention. It is also the natural fit for the raw inspector: a user
pasting a foreign IRI into the inspector can now resolve it against this instance's store.

## Files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — the `?iri=` guard in `ObjectDocumentHandler`.
- `tests/Iris.Server.Tests/ForeignObjectDocumentEndpointTests.cs` — **new** (6 tests).
