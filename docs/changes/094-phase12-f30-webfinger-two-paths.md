# 094 — Phase 12: F-30 — WebFinger two paths (regression tests for the bare RFC path)

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Gap closure (F-30, doc/test)

## What was built

Regression tests proving the **bare** WebFinger path (`/.well-known/webfinger`) — the RFC 8615-required
one — serves correctly. WebFinger is served at **both** the route-prefixed path
(`/ap/v1/.well-known/webfinger`, already tested) and the bare path. The bare path is the one a remote
WebFinger client queries per RFC 8615; it previously had no dedicated regression test. This closes the
discoverability nit in F-30.

## The fix

**No code change.** The two-path situation is **deliberate** (Decision #10 / C-01): the bare
`/.well-known/webfinger` path satisfies RFC 8615, and the route-prefixed copy is an additive Iris
route-prefix convenience. Both paths are registered in `ActivityPubServerExtensions.cs` (the route
comment documents the RFC requirement). The "gap" was a missing regression test for the bare path, not
a missing feature.

## Tests

**`ServerEndpointIntegrationTests`** (2 new integration tests on the bare `/.well-known/webfinger`
path):

- `WebFinger_BarePath_ResolvesHandleToActorIri` — a WebFinger query for `acct:{handle}@{host}` at the
  bare path returns 200 with the same `subject` + `self` link (typed `application/activity+json`,
  pointing at the actor IRI) as the prefixed path.
- `WebFinger_BarePath_UnknownHandle_Returns404` — a WebFinger query for an unknown handle at the bare
  path returns 404 (mirroring the prefixed path's behavior).

## Files changed

- `tests/Iris.Server.Tests/ServerEndpointIntegrationTests.cs` — 2 new integration tests (the bare
  WebFinger path).

## Decisions

- **Close with tests, not code.** F-30 was marked "S (doc)" — the two-path situation is deliberate
  (Decision #10 / C-01). The bare path already satisfies RFC 8615; the prefixed copy is additive. The
  only real gap was the missing regression test for the bare path (the one a remote WebFinger client
  actually queries). Adding the test closes the discoverability nit without changing behavior.
- **Both paths stay.** Removing the prefixed path would break Iris-route-prefix symmetry (the NodeInfo
  and other well-known routes are also served at the prefixed path). Keeping both is the right call:
  the bare path satisfies the RFC, the prefixed copy is a convenience, and now both are regression-
  tested.

## Test count

959 → 961 (+2), 0 failures.
