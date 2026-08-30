# 083 — Phase 11.5: client failure-mode end-to-end tests (404 + proxy fallback)

> 2026-08-30 · Phase 11 (Implementation Gaps & Usability Exploration) · Failure-mode coverage

## What was built

Phase 11's open item — *"Extend end-to-end tests for realistic failure cases … Ensure failure modes such
as bad signatures, unknown actors, 404s, rate limits, and proxy fallback are exercised in realistic
paths"* — was partially covered: the **server** side already pins bad-signature → 401, unknown/unresolvable
actor → 401/404, missing-object/actor → 404, proxy allowlist → 403, proxy rate-limit → 429, and the full
two-instance signed proxy path (`ProxyFallbackIntegrationTests`). What was **not** exercised *end-to-end
over the real signed client* (`IActivityPubClient`, built by the real `ActivityPubClientFactory`, over a
live in-process `TestServer`) were the **client's** 404 surface and the client's **proxy-fallback** path.

This slice adds those two to `ClientServerIntegrationTests` (the client ↔ live-`TestServer` harness):

- **404 is a final answer (no retry, `null` result).**
  - `GetObjectAsync` on an unknown object IRI → `null` (a not-found is an expected condition, not an error).
  - `GetActorAsync` on an unhosted actor → `null`.
  - `GetObjectAsync` on a 404 makes **exactly one** attempt — the `RetryHandler` does not retry a not-found
    (no retry storm on 404).
- **Proxy fallback: a direct 401 is rerouted through the home instance's proxy.** A direct signed GET to a
  remote that rejects the client's cross-origin signature (401) is retried through the home instance's
  `POST /ap/v1/proxy/{target}` endpoint. The test uses the **real factory path** (`ProxyBaseUrl` +
  `ProxyCredentials` wire the outermost `ProxyFallbackHandler`) with a `ProxyRoutingTransport` that 401s the
  direct leg and relays the remote actor doc from the proxy leg — proving the caller gets the proxied doc,
  the direct leg was a single GET, and the fallback was a Basic-auth POST to `/ap/v1/proxy/{target}`.

## Test harness additions

- `FakeActivityPubServer` gains an internal `ActorDocJson` (the actor document's JSON, so a test can relay
  the remote doc verbatim from the proxy leg — exactly what the real `ProxyHandler` does).
- `ProxyRoutingTransport` (new, in the client test project) is an `HttpMessageHandler` that routes a
  client's two proxy-fallback legs by host: a `GET` to the remote host → 401 (direct attempt rejected); a
  `POST` to the home proxy endpoint → 200 relaying the proxied body. It records both legs (direct-GET count
  + proxy POSTs with their auth scheme/path) so the test can assert the fallback happened.

## Why this is the right slice

The client's **happy** path (signing, content negotiation, WebFinger, paging, cache, retry on transient 503)
was already pinned. The realistic **failure** path — the browser hitting a remote it cannot authenticate to
(401) and being transparently rerouted through its own instance's proxy — is the one that makes the sample's
external-instance read + follow story actually work, and it was previously only pinned at the unit level
(`ProxyFallbackHandlerTests`, a scripted `RecordingHandler`) and at the two-instance server level
(`ProxyFallbackIntegrationTests`), never through the **real signed client pipeline** over a live stack. This
closes that gap.

## Verification

- `dotnet build Iris.slnx -c Release` → **0 warnings, 0 errors**.
- `dotnet test Iris.slnx -c Release` → **887 passed, 0 failed** (was 883; +4: three 404 tests + the
  proxy-fallback e2e test).
- `ClientServerIntegrationTests` alone: 11 passed (8 prior + 4 new).

## Decisions

- **Exercise the real pipeline, not a mock.** The proxy-fallback test builds the client through the real
  `ActivityPubClientFactory` (so the `ProxyFallbackHandler` is the genuine outermost stage) and injects a
  routing transport rather than a fake server for the proxy leg — this proves the *client's* behavior (strip
  signature, forward Basic-auth POST, relay the response) without needing a second full `WebApplication`.
- **404 assertions are about the contract, not the wire.** `GetObjectAsync`/`GetActorAsync` returning `null`
  on a 404 is the documented contract (a not-found is an expected condition); the single-attempt assertion is
  the important one (no retry storm), matching `RetryHandler`'s "not transient" rule.
- **Server-side failure modes are out of scope here.** Bad signature (401), unknown actor (401/404), proxy
  allowlist (403), and proxy rate-limit (429) are already covered by `SignatureValidationMiddlewareTests` and
  `ProxyFallbackIntegrationTests`; this slice deliberately does not duplicate them.
