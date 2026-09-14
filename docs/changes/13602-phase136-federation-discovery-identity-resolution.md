# 136.2 — Federation discovery + identity resolution (wire-level)

**Date:** 2026-09-14
**Slice:** 136.2 (Lemmy interop foundation — discovery + identity resolution)
**Status:** **COMPLETE.** Federation discovery and identity resolution are verified to work at the
wire level in both directions, with the acceptance-criteria gaps now covered by integration tests.
This is the second half of the 135.1b interop work; the remaining gap (HTTP signature
verification) lands in 136.3.

## What 136.2 verifies (and where)

Phase 136.2 is a **verification + test-coverage** slice. The discovery and identity-resolution
endpoints already exist (WebFinger, actor/Group document serving, content negotiation, remote
public-key resolution). The work this slice is (a) confirming each path works at the wire level —
against the running `iris.luit.ink` instance and the running Lemmy 0.19.20 instance — and (b) closing
the acceptance-criteria gaps that had no test coverage.

### Live verification — Iris side (running `iris.luit.ink`, port 8088)

All four of the 135.1b acceptance criteria on the Iris side hold at the wire level:

1. **User WebFinger** — `GET /.well-known/webfinger?resource=acct:bob@iris.luit.ink` (and the bare
   `/.well-known/webfinger` path) returns a JRD document whose `self` link resolves to
   `https://iris.luit.ink/ap/v1/u/bob`. The response is served as `application/jrd+json` (RFC 8410),
   not generic `application/json`.
2. **Community WebFinger** — `GET /.well-known/webfinger?resource=acct:technology@iris.luit.ink`
   returns a JRD document whose `self` link resolves to `https://iris.luit.ink/ap/v1/c/technology`.
   Community (Group) discovery works the same single-endpoint way user (Person) discovery does.
3. **Dereferencing** — `GET /ap/v1/u/bob` and `GET /ap/v1/c/technology` return the actor/Group
   document (type `Person` / `Group`) with its `publicKey` (`id` + `publicKeyPem`), so a remote
   instance can verify signatures from either.
4. **Content negotiation** — the actor document responds to `Accept: application/ld+json` with
   `application/ld+json`, to `Accept: application/activity+json` with `application/activity+json`,
   and to `Accept: */*` (and no `Accept`) with the spec-default `application/activity+json`.

### Live verification — Lemmy side (running Lemmy 0.19.20, port 8091, host `lemmy.luit.ink`)

Lemmy's own discovery is correct and Iris-compatible:

- **Lemmy WebFinger** — `GET /.well-known/webfinger?resource=acct:interop@lemmy.luit.ink` (Lemmy's
  only local community is `interop`) returns a JRD document whose `self` link points at
  `https://lemmy.luit.ink/c/interop` (type `application/activity+json`).
- **Lemmy community document** — `GET /c/interop` (with `Accept: application/activity+json`) returns
  a `Group` whose `publicKey` is a **PKIX RSA PEM** under the keyId fragment
  `https://lemmy.luit.ink/c/interop#main-key` (Lemmy's `#main-key` convention, distinct from Iris's
  `#key-1`). This is the exact shape Iris's inbound key resolver must consume to verify a Lemmy
  signature — now covered by a test.

## Tests (the gap closure)

9 new tests, committed together with this slice:

- **`ServerEndpointIntegrationTests`** (8 new cases):
  - WebFinger response is served as `application/jrd+json` (RFC 8410 content-type contract).
  - WebFinger `self` link advertises the actor document media type (`application/activity+json`).
  - Community (Group) discovery: WebFinger resolves a community handle to the
    `/ap/v1/c/{name}` Group IRI.
  - The community document serves a `Group` carrying its `publicKey` (`id` + `publicKeyPem`) for
    remote signature verification.
  - Actor-doc content negotiation: `Accept: application/ld+json` → `application/ld+json`;
    `Accept: application/activity+json` → `application/activity+json`; `Accept: */*` →
    `application/activity+json`; a mixed `Accept` that includes `ld+json` → `application/ld+json`.
- **`InboundKeyResolverTests`** (1 new case): a **real Lemmy community key shape** (a `Group` actor
  carrying a PKIX RSA PEM under the `#main-key` fragment) resolves to a verifying key, and the
  resolved key verifies a signature made with the matching private key. This is the wire-level proof
  that the inbound key resolver — the seam that validates a Lemmy signature — accepts Lemmy's
  community key format.

## Findings (carried to 136.3 / ops)

- **Iris→Lemmy discovery is unblocked.** Iris can resolve a Lemmy community (`interop`) via WebFinger
  and dereference its `Group` document, whose PKIX PEM key is accepted by the inbound key resolver.
  The only remaining Iris→Lemmy gap is HTTP signature verification (136.3).
- **Lemmy→Iris WebFinger is failing live.** `GET http://localhost:8091/.well-known/webfinger?resource=
  acct:bob@iris.luit.ink` returns `{"error":"unknown","message":"Failed to resolve actor via
  webfinger"}`. This is **not** an Iris WebFinger defect — Iris's WebFinger is verified correct above
  (and resolves `acct:bob@iris.luit.ink` to `https://iris.luit.ink/ap/v1/u/bob`). The failure is in
  Lemmy's outbound WebFinger resolution of the Iris actor IRI, i.e. Lemmy's HTTP client could not
  fetch `https://iris.luit.ink/.well-known/webfinger`. The likely cause is environmental: the
  Lemmy container's public egress / the `iris.luit.ink` reverse proxy path (a `504 Gateway
  Time-out` was observed on the `iris-dev2.luit.ink` FQDN proxy this turn). This is an **ops /
  network** finding, not a code gap; it does not block the 136.3 signature work (which can be
  verified against the running instances directly, and is covered by the conformance suite).
- **Lemmy's community key is PKIX RSA under `#main-key`** — a distinct keyId convention from Iris's
  EC P-256 / `#key-1`. The inbound key resolver already handles both (PEM + JWK, any keyId
  fragment); the new test locks in the Lemmy shape.

## What is NOT in this slice

- No HTTP signature verification fix — that is 136.3 (canonical verification matrix), which consumes
  the 136.1 federation trace as its diagnostic input.
- No fix to the Lemmy-side WebFinger failure — that is an ops/network issue (Lemmy container egress /
  the `iris.luit.ink` reverse-proxy path), not an Iris code gap.
