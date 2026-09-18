# 139.3-s6 Finding 1 (follow-up) — Bound the outbound connection phase

**Phase:** 139.3 — Data lifecycle & persistence review
**Item:** Up Next #1 (follow-up to Scenario 6, Finding 1)
**Date:** 2026-09-18
**Result:** **FIXED.** The server-side outbound transport (the innermost `HttpMessageHandler` of every
server→server federation dial) is now a `SocketsHttpHandler` with an explicit, bounded
`ConnectTimeout` (5 s), so a single unreachable peer (TCP accepted, TLS stalls) can no longer hang the
TCP/TLS dial for ~60–70 s. The community feed, followed feed, remote-collection fetcher, actor-document
fetcher, and outbound delivery worker all share this bounded transport.

## Root cause

Scenario 6 (offline rebuild) found that a community feed following an *unreachable* peer hung ~60–70 s.
The diagnosis: `HttpClient.Timeout` (5 s, already set on the feed's outbound client) bounds the
**send/read** phase of a request, but **not the TCP/TLS connection (dial) phase**. A plain
`HttpClientHandler` (which wraps `SocketsHttpHandler` internally) leaves `ConnectTimeout` at the
platform default — **35 s** in .NET 8+ — so a peer that accepts TCP but stalls TLS holds the dial for
~35 s before the transport gives up; with the retry/timeout interplay the observed wall time reached
~60–70 s, during which the feed could not render.

## What was built

A small `internal static class` — `src/Iris.Server/ServerOutboundTransport.cs` — that builds the
server-side outbound transport:

- `ServerOutboundTransport.Create(TimeSpan? connectTimeout = null)` returns a `SocketsHttpHandler`
  with:
  - `ConnectTimeout = connectTimeout ?? 5 s` (the default matches the `HttpClientTimeout` already
    applied to the feed's outbound client); a value ≤ `TimeSpan.Zero` is rejected (`ArgumentOutOfRangeException`)
    because the `SocketsHttpHandler.ConnectTimeout` setter does not accept zero.
  - `PooledConnectionLifetime = 24 h`, `PooledConnectionIdleTimeout = 2 min` (the `SocketsHttpHandler`
    defaults — do not pin stale connections).
  - `AllowAutoRedirect = true`, `AutomaticDecompression = DecompressionMethods.All` (outbound
    federation GETs — actor docs, collections, media — follow redirects and decompress).

The six server-side outbound `new HttpClientHandler()` registrations in
`ActivityPubServerExtensions.cs` were swapped to `ServerOutboundTransport.Create()`:

| Registration | Service | Line (approx.) |
|---|---|---|
| `IActorDocumentFetcher` innermost (TEMP `ResponseLogHandler` wrap) | actor-doc fetch | ~277 |
| `IActivityPubClient` singleton | outbound object fetch (24.1) | ~309 |
| `IRemoteCollectionFetcher` | remote-collection fetch (Phase 4) | ~339 |
| `ICommunityFeedService` client | community feed (the Finding 1 site) | ~493 |
| `IFollowFeedService` client | followed feed (F-14) | ~553 |
| `Func<HttpMessageHandler>` seam | outbound delivery worker (F-22) | ~599 |

No new NuGet packages, no dependency-direction changes, no API-surface changes (the helper is
`internal`; the public DI registrations are unchanged in type).

## Tests

`tests/Iris.Server.Tests/ServerOutboundTransportTests.cs` (7 tests, all passing):

- `Create_Default_ReturnsSocketsHttpHandler_WithFiveSecondConnectTimeout` — default is a
  `SocketsHttpHandler` with a 5 s `ConnectTimeout`.
- `Create_CustomTimeout_ReturnsSocketsHttpHandler_WithThatConnectTimeout` (×3: 1 s, 3 s, 10 s) — a
  custom timeout is honored.
- `Create_ZeroTimeout_ThrowsArgumentOutOfRange` — zero is rejected (the setter does not accept it).
- `Create_NegativeTimeout_ThrowsArgumentOutOfRange` — negative is rejected.
- `Create_ConfiguresPoolingAndRedirectDefaults` — pooling lifetimes (24 h / 2 min),
  `AllowAutoRedirect`, and `AutomaticDecompression = All` are set.

Full suite green: `dotnet test --filter "Category!=Slow"` — 0 failures across all projects
(`Iris.Server.Tests` 1313 passed, +7 new).

## Live verification

Rebuilt the Docker app (`docker compose -f apps/Iris.Web/docker-compose.yml up --build -d iris-web`),
confirmed healthy (port 8088). Navigated (as a logged-in user) to the `technology` community — the
community that follows `lemmy.world` (a remote peer, the same shape as the unreachable-peer case in
Scenario 6). The community feed loaded **~3 s** with 20 items + a "Load more" button; **0 console
errors**. The transport swap is a clean regression pass: outbound fetches (including remote-member
outbox fetches) still work, now with a bounded connection phase.

## Decision recorded

**Scope — all six server-side outbound registrations, not just the feed.** The Finding 1 wording named
the community feed, but the same unbounded-dial defect applied to every server-side outbound dial
(delivery, collection fetch, actor-doc fetch, followed feed). Bounding them all is the same one-line
swap per registration and prevents the identical stall in delivery (a dead recipient would otherwise
hold the delivery worker's connection for 35 s). The 5 s default matches the `HttpClientTimeout`
already applied to the feed client, keeping the connection-phase bound and the request-phase bound
symmetric.

**Why not parallelize the feed's per-contributor fetches?** The Finding offered "and/or parallelize
the feed's per-contributor remote fetches with a per-fetch `CancellationTokenSource` timeout." Bounding
the connection phase alone removes the ~60–70 s hang (the dial now fails at ~5 s instead of ~35–70 s),
which is the user-visible defect. Parallelization would reduce the *aggregate* feed latency when many
peers are slow (a 5 s dial per contributor still serializes), but it is a larger change (refactoring
`CommunityFeedService`'s sequential per-contributor loop into a bounded parallel `Task.WhenAll` with
cancellation) and is deferred as a separate optimization. The connection-phase bound is the minimal
fix that resolves the reported hang.

## Files

- `src/Iris.Server/ServerOutboundTransport.cs` — **new** (the bounded transport helper).
- `src/Iris.Server/ActivityPubServerExtensions.cs` — six `new HttpClientHandler()` →
  `ServerOutboundTransport.Create()`.
- `tests/Iris.Server.Tests/ServerOutboundTransportTests.cs` — **new** (7 tests).
