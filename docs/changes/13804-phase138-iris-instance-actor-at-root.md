# 138.4 (unblock) — Serve the Iris instance-actor document at the instance root

Phase 138 slice 138.4 ([plan](../plans/phase-138-lemmy-community-integration.md)): Lemmy → Iris
community discovery. Before any remote→Iris resolve can be attempted, a concrete interop defect had
to be fixed: **Lemmy dereferences the Iris site actor from the instance root and was failing.**

## The defect

When Lemmy (0.19.20) tries to resolve *any* object on the Iris instance — a community by URL, a
post, a person — its `resolve_object` path first dereferences the **site actor** from the instance's
root URL. The Lemmy log (captured 2026-09-15) shows exactly where it stopped:

```text
lemmy_apub::objects::instance: Failed to dereference site for https://iris.luit.ink/:
Unknown: Failed to parse object https://iris.luit.ink/ with content <!DOCTYPE html>
<meta name="description" content="Iris — a decentralized social platform ..." />
<title>Iris</title> ...
```

Iris served its **HTML SPA shell** at `GET /` (even with `Accept: application/activity+json`), so
Lemmy could not parse a site actor and every remote→local resolve was blocked. This is not Iris
being "wrong" by the letter of any single spec — the instance-actor IRI is the configured
`InstanceActorId` (`/ap/v1/u/{handle}`) — but it breaks a real, common remote-platform assumption:
*the instance root is where the site actor lives.* (Mastodon serves its `@user`/instance actor at the
root; Friendica and Pleroma do too. Lemmy's `objects::instance` module expects an actor document at
the instance origin.)

## The fix

Content-negotiate the instance root. A new endpoint `GET /` (mapped on the root endpoint, **not** the
`/ap/v1` group, because the instance root is the host root):

- **If the request is an ActivityPub client** — its `Accept` header names `application/activity+json`,
  `application/ld+json`, or `application/*` — serve the configured **instance actor's public
  document** (the same document `ActorDocumentHandler` serves at `/ap/v1/u/{handle}`), rendered
  through the shared `LocalActorDocumentCache`. The response carries the usual
  `Cache-Control: max-age=60, stale-while-revalidate=300` actor caching.
- **Otherwise** (a browser with `text/html`, a bare `curl` with no `Accept`) return **404**, so the
  host app's SPA fallback (mapped after the ActivityPub endpoints) serves the shell — the home page
  still works for humans.

Two deliberate constraints:

- **Public form only.** The root never serves the owner-only `privateKey` extension. The instance
  root is a public discovery URL; the owner-only form requires the request to be authenticated *for
  the instance actor specifically*, which a bare root GET is not. A host wanting the authenticated
  form still uses `/ap/v1/u/{handle}`.
- **Exact root route only** (`GET "/"`), so it never shadows the SPA's non-root client routes
  (`/home`, `/compose`, …).

The `WantsActivityStreams(HttpContext)` helper centralizes the Accept check; the handler
`InstanceActorDocumentHandler` reuses `BuildActorDocument` + `LocalActorDocumentCache` so the root
and the versioned route share one cached rendering.

Files:
- `src/Iris.Server/ActivityPubServerExtensions.cs` — route + `InstanceActorDocumentHandler` +
  `WantsActivityStreams`.
- `tests/Iris.Server.Tests/InstanceActorDocumentAtRootIntegrationTests.cs` — 5 integration tests.

## Tests (5, all pass)

- `Root_WithActivityPlusJsonAccept_ReturnsInstanceActorDocument` — the AP client gets the instance
  actor's document (id = the `InstanceActorId`, type `Person`, inbox/outbox present).
- `Root_WithLdJsonAccept_ReturnsInstanceActorDocument` — `application/ld+json` also negotiates.
- `Root_WithHtmlAccept_ReturnsNotFound_ForSpaFallback` — `text/html` → 404 (SPA shell path).
- `Root_WithoutAcceptHeader_ReturnsNotFound` — no `Accept` → 404.
- `Root_DoesNotLeakPrivateKey` — the public form never includes `privateKey`.

## Live verification (2026-09-15)

Rebuilt the Iris Docker image, restarted `irisweb-iris-web-1`, then:

```text
# AP client (what Lemmy sends) now gets the instance actor at the root:
$ curl -s -H 'Accept: application/activity+json' http://127.0.0.1:8088/
{"outbox":"https://iris.luit.ink/ap/v1/u/alice/outbox","inbox":"https://iris.luit.ink/ap/v1/u/alice/inbox",
 "preferredUsername":"alice", ...}
$ curl -s -o /dev/null -w "%{http_code}" -H 'Accept: text/html' http://127.0.0.1:8088/   # browser
404  (→ SPA fallback serves the shell)

# From inside the Lemmy container (the actual federation path):
$ docker exec lemmy-1 curl -s -H 'Accept: application/activity+json' http://irisweb-iris-web-1:8080/
{"outbox":"https://iris.luit.ink/ap/v1/u/alice/outbox", ...}   # 200, parses as an actor
```

After the fix, the previous `Failed to parse object https://iris.luit.ink/ with content <!DOCTYPE
html>` error in the Lemmy log no longer occurs — the site-actor dereference succeeds.

## Remaining (separate) gap — not fixed here

Lemmy 0.19's **search / resolve-by-URL UI path** still does not surface the Iris `interop` community
even though the site-actor dereference now succeeds. That is a distinct Lemmy-side limitation (its
community URL-search does not index a freshly-resolved remote community), not the Iris site-actor
defect this change fixes. 138.4's full "follow the Iris community from Lemmy" acceptance still needs
a follow mechanism (e.g. the Iris community following Lemmy first — slice 138.5 — or a Lemmy-side
follow-by-actor-id) to be demonstrated end-to-end; the Iris-side blocker is resolved.

A secondary edge case noted in the plan: for the `interop` handle, Iris webfinger resolves to the
**user** (`/u/interop`) rather than the Group because a same-named local user exists (the community
webfinger fallback only fires when no user matches the handle). That does not affect this root-actor
fix but should be resolved before relying on webfinger to resolve a community that collides with a
local user handle.
