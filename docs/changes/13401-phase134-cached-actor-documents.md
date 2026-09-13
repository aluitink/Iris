# 134.1 — Serve cached remote actor documents for known-actor profiles

**Date:** 2026-09-13
**Slice:** 134.1 (PLAN "Up Next" — the directory only shows local actors; the backend should cache copies of the actor records it has interacted with and serve them as known content so they can be displayed in the directory of known actors)
**Commits:** `0d56105` — `Serve cached remote actor documents for known-actor profiles (134.1)`

## Problem

The directory's "All known" scope and the actor-detail/profile view render an actor's
**identity** (name, banner, bio, avatar) by fetching the actor's document over the network.
For a **remote** (cross-origin) actor, `UiContext.FetchActorAsync` routed the read through the
home instance's proxy (`POST /ap/v1/proxy/{iri}`), which performed a **live** fetch of the actor
from its home instance. Two consequences:

1. **Resilience:** when the actor's home instance was unreachable, or the account had been
   **deactivated** (Mastodon returns `410 Gone` for a deactivated account), the profile view
   showed "Actor not found." even though this instance had already cached the actor's document in
   its database during federation (Phase 117.3's `RemoteActorPersister` persists a remote actor's
   document on first encounter, triggered by every signed inbound activity).
2. **Efficiency:** a live actor's profile still triggered a live cross-instance fetch (and the
   Posts/Followers/Following tabs each trigger their own) on every view.

The backend caching **already worked** (confirmed live: `GET /ap/v1/search?q=&type=Actor&local=false`
returns ~24 actors, 14 of them remote: mastodon.social, lemmy.world, fairy.id, mstdn.social,
dresden.network, …). The gap was that the **profile view** did not use the cached copy — it
always did a live fetch.

## Change

### New server endpoint: `GET /ap/v1/actor?iri={absolute-actor-iri}`

`ActivityPubServerExtensions` registers a new `group.MapGet("/actor", ActorByIriHandler)` on the
`/ap/v1` group, **before** the `/{**path}` object catch-all so it wins by routing specificity.
`ActorByIriHandler(HttpContext, IPersistenceProvider, CancellationToken)`:

- Reads the `?iri` query value. On a missing/malformed value (fails `Iri.TryParse`) it returns
  **404**.
- Looks the actor up in `persistence.Actors.TryGetActorAsync`. On a miss (an actor this instance
  has never cached) it returns **404** — the client then falls back to a live fetch.
- On a hit it serves the **stored document as-is** (`ActivityJson.Deserialize<Actor>(ActivityJson.Serialize(actor))`,
  a deep copy so the stored actor is never mutated) with the `ActorCacheControl`
  (`max-age=60, stale-while-revalidate=300`) header. The document is served **unmodified** — no
  Iris-local collection extensions (`BuildActorDocument`'s capabilities/feed/etc. are only valid
  for local actors; a known actor's client reads its collections by the IRIs the document
  advertises). A remote actor's document keeps its original remote IRI, name, summary, icon,
  image, and publicKey.

The endpoint is registered as a plain `MapGet` handler (no authentication): actor documents are
public, so an anonymous read succeeds — the same policy as the existing actor-document and
search endpoints.

### Client: `UiContext.FetchActorAsync` consults the cache first for remote actors

`apps/Iris.Web.Client/Ui/UiContext.cs` — `FetchActorAsync(Iri)`:

- **`IsRemoteActorIri(Iri)`** — true when the actor IRI's host differs from the home instance's
  origin (the named `"iris"` `HttpClient`'s `BaseAddress`). A local actor (same origin) is **not**
  remote. When the home origin can't be determined the IRI is treated as remote (the cached
  lookup is a safe no-op that 404s and falls through to the live fetch).
- **`FetchCachedActorAsync(Iri)`** — a same-origin `GET /ap/v1/actor?iri={escaped}` via the `"iris"`
  client. Returns the deserialized actor document on 2xx, or `null` on 404/any failure.
- The flow: for a **remote** actor, try `FetchCachedActorAsync` first; if it yields a document,
  cache + return it (no live fetch). If it returns `null` (the instance has no cached copy), fall
  through to the existing live path (the session's signing client when signed in, a plain
  unsigned `HttpClient` when signed out). A **local** actor skips the cached path entirely and
  keeps the direct `/u/{handle}` fetch, preserving the Iris-local extensions.

This makes a known actor's profile **resilient**: it renders from the database even when the
actor's home instance is unreachable or the account has been deactivated. And it's **faster**: a
cached actor no longer triggers a live cross-instance fetch for its identity document.

## Verification

**Server integration tests** (`tests/Iris.Server.Tests/CachedActorEndpointTests.cs`, 5 tests, its
own xunit collection + shared `TestServer` fixture): a cached **local** actor is served (200, the
stored document); a cached **remote** actor (on a different host) is served as-is (200, the stored
document with its original remote IRI + name + summary); an actor the instance never cached 404s;
a missing `?iri` 404s; a malformed `?iri` 404s.

**Build + suite:** `dotnet build Iris.slnx` clean; `dotnet test Iris.slnx` green (the new 5 tests
pass; the full suite is green — one unrelated integration test is intermittently flaky across
runs but is never `CachedActorEndpointTests`).

**Live (Playwright, `--no-cache` Docker rebuild of `iris-web`):**
- A **cached, deactivated** remote actor (fairy.id "Mase", previously "Actor not found." via the
  live proxy) now renders its profile from the cached document: heading "Mase", name, and the
  Posts/Followers/Following tabs.
- A **live** remote actor (mastodon.social Gargron) still loads fully from the cached doc: banner
  image, name "Gargron", real name "Eugen Rochko", and the full bio.
- The directory **"All known"** scope renders all cached remote actors (mastodon.social,
  dresden.network, fairy.id, mstdn.social, mastodon.world, social.vivaldi.net) with banners/names/
  bios; clicking a card navigates to the profile, which renders.
- The only console errors are the **pre-existing** CSP `connect-src 'self'` violations from the
  Posts/Followers collection fetches (identical for live actors) — **not** introduced by this
  change (the cached-actor fetch is same-origin and clean).
