# 14818 — Remote-actor direct GET serves the stored document (S24 Defect 3)

**Status:** done
**Slice:** Dev Queue — S24: cross-instance follow, Defect 3 (a followed REMOTE actor's direct
`GET /ap/v1/u/<handle>` 404s on the follower's instance)
**Owner:** dev

## Problem

After a cross-instance follow (actor on instance B follows actor on instance A), a direct
`GET B /ap/v1/u/<remote-handle>` returned **404 (empty body)** even though B had just fetched
that exact actor's document during the follow flow (directory lookup → actor page → follow).
The follow edge and delivery were correct both directions, and the actor-page follow button was
correct — but the remote actor's document was not retrievable by direct IRI on the follower's
instance. Local control `GET B /ap/v1/u/<local-handle>` returned 200.

This is S24's Defect 3 (of three defects; the other two are tracked separately — see below).

## Root cause

`ActorDocumentHandler` (`GET /ap/v1/u/{handle}`, `src/Iris.Server/ActivityPubServerExtensions.cs`)
reconstructs the requested IRI as this instance's **local** base + handle:
`{base}/ap/v1/u/{handle}`. A cross-instance followed remote actor is stored in the durable actor
store under its **remote** IRI (the `RemoteActorPersister` persists it on first encounter during
the follow flow — `PUT` into `IActorStore` keyed by the remote IRI). The route's local IRI never
matches that stored remote IRI, so `persistence.Actors.TryGetActorAsync(localIri)` misses and the
handler returns 404 — even though the instance holds the exact document.

The handler already had a case-insensitive **local** fallback (S12a) for same-instance mixed-case
handles, but no **remote** fallback. Note the instance already served a stored remote actor's
document as-is through a *different* endpoint (`GET /ap/v1/actor?iri={iri}`, `ActorByIriHandler`) —
but that endpoint is deliberately local-only (Phase 138) and is keyed by full IRI, not by handle,
so it did not cover the handle-based direct-GET path.

## Fix

`src/Iris.Server/ActivityPubServerExtensions.cs` → `ActorDocumentHandler`: inside the existing
`!TryGetActorAsync(localIri)` miss block (after the S12a local case-insensitive fallback), add a
**remote** fallback:

- Search the actor store for a stored actor whose `preferredUsername` matches the requested handle
  case-insensitively **and** whose IRI is **not** on this instance's origin
  (`!rid.StartsWith(baseOrigin)`) — i.e. a genuine cross-instance actor, not a local one.
- When found, serve that stored document **as-is** (deep-copied so the store is never mutated):
  the remote actor's document already carries the remote instance's own
  inbox/outbox/followers/following IRIs, so no Iris-local collection extensions are added (those
  are only valid for local actors, whose documents `BuildActorDocumentAsync` rebuilds).
- The response uses the standard actor cache-control header (`ActorCacheControl`).

The fallback only fires when the local IRI missed **and** no local same-origin match resolved, so
it cannot shadow local actor resolution. Only the first (deterministic, store-ordered-by-Id) match
is served, so a handle colliding with several cached remote actors resolves deterministically.
Mirrors `ActorByIriHandler`'s "serve known content" half and the S4 remote-community Following-tab
precedent (adding a remote fallback to a local-only read path).

## Tests

`tests/Iris.Server.Tests/CrossInstanceSearchDiscoverabilityIntegrationTests.cs` (two-host in-memory
federation fixture):

- `RemoteActorDirectGet_ServesStoredRemoteDocument` — seeds a remote actor (on A) into B's actor
  store under its remote IRI (the `RemoteActorPersister` path); asserts `GET B /ap/v1/u/charlie`
  returns **200** with the stored document's original remote `id` and `type` (was 404).
- `UnknownHandleDirectGet_Still404s` — a handle no local or stored remote actor carries still
  **404s** (the remote fallback only serves a genuine match, no false 200).

`dotnet test tests/Iris.Server.Tests` (fast) → **1404 pass, 0 fail** (includes both new tests).
`dotnet test tests/Iris.Web.Tests` → **108 pass, 0 fail** (no regression). Full solution
`dotnet build` → **0 warning, 0 error**.

## Live verification (deployed, dev instance `iris.luit.ink`)

- `GET /ap/v1/u/skinnylatte` (a followed remote actor, `hachyderm.io/users/skinnylatte`, stored in
  the durable actor store) → **200**, serving the stored remote document **as-is** (original
  `hachyderm.io` inbox/outbox/followers/following IRIs — not rebuilt with local ones). **Was 404.**
- `GET /ap/v1/u/andrew` (local control) → **200** (local actor resolution unaffected).
- `GET /ap/v1/u/no-such-actor-xyz` (unknown handle) → **404** (no false positive).

## Out of scope (S24's other defects)

- **Defect 1** (Profile "Following" tab omits remote actors) — **not reproducible on the dev
  instance**: the Following tab renders remote actors correctly (server `following` collection is
  wire-correct and the client hydration via `Ui.GetActorAsync` resolves them through the proxy).
  Needs a clean two-Iris-instance entry (QA federation stack) to re-confirm; the S24 re-verify
  checklist covers it.
- **Defect 2** (spurious self-follow activity in the outbox) — **not reproducible on the dev
  instance** (outbox was empty after the QA stack's follows were undone); needs a fresh
  cross-instance follow on the two-instance stack to trigger and trace the follow handler's
  secondary write path.

## Files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `ActorDocumentHandler` remote-actor fallback
  (serve the stored remote document as-is on a local-IRI miss).
- `tests/Iris.Server.Tests/CrossInstanceSearchDiscoverabilityIntegrationTests.cs` — two regression
  tests for the remote-actor direct GET.
