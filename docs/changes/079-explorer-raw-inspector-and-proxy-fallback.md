# 079 — Phase 8 S8: Raw JSON inspector + proxy-fallback screen

> 2026-08-30 · Phase 8 (Sample) · Slice S8 (raw-JSON inspector + proxy-fallback write surface)

## What was built

The Blazor WASM explorer's **raw JSON inspector** and **proxy-fallback** paths — the two remaining S7
follow-up write-surface screens — are now pinned by in-process tests against a live `Iris.Server`
ActivityPub pipeline. These are the last of the S8 explorer slices: the raw-JSON inspector (the primary
tool for finding interop bugs — it shows the exact signed request and the raw response) and the
proxy-fallback path (the browser's way of reaching a remote instance when it cannot reach it directly).

Both mechanisms were already implemented as library code in earlier phases; this slice pins them
end-to-end in-process, the way the S3–S7 screens are pinned, so the thin Blazor screens that wrap them
rest on a verified foundation.

- **Raw JSON inspector.** The inspector's library surface is
  [`IActivityPubClient.SendAsync`](../../src/Iris.Client/IActivityPubClient.cs) — a raw request sent
  through the client's full signed pipeline (retry → JSON-LD → signing), returning the (unconsumed)
  `HttpResponseMessage` the caller inspects. The new test proves `SendAsync` signs (the server sees a
  `Signature` header) and returns the raw response body, which is the actor document.
- **Proxy fallback.** When a browser cannot reach a remote instance directly (CORS, and the browser
  cannot produce an ActivityPub HTTP signature), the client's
  [`ProxyFallbackHandler`](../../src/Iris.Client/Pipeline/ProxyFallbackHandler.cs) (outermost pipeline
  stage) retries the request through the home instance's proxy endpoint
  (`POST {proxyBase}/ap/v1/proxy/{target}`), which the home server signs with the acting actor's key.
  The new test drives the **full client pipeline** against a real home server (A) whose proxy relays to a
  real remote server (B): a direct GET to B's actor document (rejected 401 — A's signature is not
  resolvable cross-origin) falls back through A's proxy, which re-signs with alice's key, so B validates
  and the client gets the document.

## Key types & files

- **`tests/SampleBlazorClient.Tests/S8InspectorAndProxyTests.cs`** (new, 2 facts) — the S8 in-process
  tests, hosted against a live `Iris.Server` pipeline (the test project already references `Iris.Server`
  + `Iris.Testing`, used by the S3–S7 screen tests):
  - `RawInspector_SendAsync_SignsAndReturnsRawResponse` — builds a client (alice's key, no proxy),
    `SendAsync` a GET of the actor's own document; asserts the request reached the server carrying a real
    `Signature` header (the inspector's "exact signed request" half) and the response is returned
    unconsumed with the actor document as its body (the "raw response" half).
  - `ProxyFallback_Direct401_RetriesThroughHomeProxyAndSucceeds` — two in-process instances (A, the
    home/proxy origin, hosting alice; B, the remote target, hosting bob). A's proxy outbound transport is
    routed to B (`LazyHandler`), and B's signature-validation fetcher resolves a signing key by fetching
    the actor's document from A. The browser's client (signed as alice, proxy fallback to
    `https://a.example` with alice's Basic auth, transport dials B) GETs bob's actor document; the direct
    attempt is 401 by B, the `ProxyFallbackHandler` retries through A's proxy, A re-signs with alice's
    key, B validates and returns bob's document.

## Tests

`SampleBlazorClient.Tests` 45 → 47 (2 new S8 facts). Full solution green — **883 tests, 0 failures** —
build clean (0 warnings).

## Decisions

- **Pinned in-process, not as a separate Blazor screen test.** The raw-JSON inspector and the
  proxy-fallback path are thin wrappers over two already-implemented library mechanisms
  (`SendAsync` and `ProxyFallbackHandler` + the server `ProxyHandler`). The S3–S7 screen tests pin each
  screen's in-process behavior the same way (a Blazor `BlazorServerApp`/`TestServer` host + the
  `IrisClientFactory`), and the proxy-fallback mechanism already has dedicated unit + server-side
  integration coverage (`ProxyFallbackHandlerTests`, `ProxyFallbackIntegrationTests`). Adding a
  full-pipeline client test (direct 401 → proxy → remote 200) closes the remaining gap: it proves the
  `ProxyFallbackHandler` is correctly wired into the real client pipeline (not just unit-tested in
  isolation) and that the home server's `ProxyHandler` re-signs with the acting actor's key so the remote
  validates.
- **The direct attempt is genuinely rejected 401.** B's `RemoteInboundKeyResolver` (wired to A via a
  `LazyHandler`) can only resolve a signing actor's key by fetching the actor's document from A. A
  direct A-signed GET to B is therefore not resolvable cross-origin and 401s — forcing the
  `ProxyFallbackHandler` to route through A's proxy. The test's passing (and the `GetObjectAsync`
  assertion) confirms the fallback path is taken, not a direct 200.
