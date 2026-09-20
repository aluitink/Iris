# 14816 — Anonymous proxy seam signs the public GET as the local instance actor

**Status:** done
**Slice:** Dev Queue — S2 / S14: the signed-out proxy seam 401s on unsigned remote GETs
**Owner:** dev

## Problem

The signed-out (anonymous) proxy seam — a cookie-less `GET /ap/v1/proxy/{target}` that relays a
public ActivityPub read for a visitor who is not signed in — relayed the request **unsigned**. That
is correct for a lenient in-process peer, but a real remote instance (mastodon.social in
particular) requires a **valid HTTP signature even for a public read**: an unsigned `GET` of a
public actor document is rejected with `401 {"error":"Request not signed"}`.

Live symptom (S2 root page, S14 actor-detail): signed-out `/actor?iri=https://mastodon.social/users/gnomon`
routed through the same-origin proxy, but the proxy relayed an unsigned GET, so mastodon.social
returned 401 and the page showed **"Actor not found."** The anonymous seam was deployed but not
functional for any remote that enforces signatures on reads.

A direct, unsigned `curl` of `https://mastodon.social/users/gnomon` (with `Accept:
application/activity+json`) reproduces the same 401 — confirming the rejection is the remote's,
not Iris's, and that an anonymous (no-actor) request cannot satisfy it.

## Root cause

`ProxyHandler` (the shared `POST/GET /ap/v1/proxy/{**target}` endpoint) built the outbound client
with `ActorId = null` for the anonymous path, so the `SigningHandler` never signed the request. The
authenticated path signed as the requesting actor; the anonymous path had no actor and was left
unsigned — which a strict remote refuses.

## Fix

The anonymous seam now **signs the forwarded public GET as the local instance actor**
(`ActivityPubServerOptions.InstanceActorId` — the site actor, whose key is registered in the
`IKeyProvider` and served at the instance root). The remote verifies the signature against the
instance actor's own public document and serves the public object.

`src/Iris.Server/ActivityPubServerExtensions.cs` (`ProxyHandler`):

- A `signingActor` is chosen: the requesting actor when authenticated, otherwise the local
  **instance actor** (`options.InstanceActorId`).
- The `X-Iris-Actor` override carries `signingActor` (the established re-sign mechanism, Resolved
  Decision 037), and the outbound `IActivityPubClient` is built with `ActorId = signingActor` so the
  `SigningHandler` resolves the key. Previously the anonymous path set `ActorId = null` (unsigned).
- When `InstanceActorId` is unset (a host with no instance actor) the anonymous seam falls back to
  the prior unsigned relay — the same behavior as before for such hosts.

The authenticated path is unchanged (it still signs as the requesting actor; the
`Proxy_ResignsAsActingActor_NotInstanceActor` regression test still passes).

## Tests

- `tests/Iris.Server.Tests/ProxyFallbackIntegrationTests.cs`:
  - `Proxy_AnonymousGet_IsRelayedUnsigned_NotSigned` → renamed/repurposed to
    `Proxy_AnonymousGet_IsRelayedSigned_AsInstanceActor`: the anonymous relay is now **signed** as
    the local instance actor (alice in the test host), not left unsigned. The in-process target is
    lenient, so the assertion guards the behavior (the anonymous read must be a signed, resolvable
    read — the condition a strict remote like mastodon.social requires).
  - All other anonymous-seam tests (relay, archive, allowlist, rate limit, write-rejection) still pass.
- `tests/Iris.Server.Tests/OutboundSignatureIdentityIntegrationTests.cs`:
  `Proxy_ResignsAsActingActor_NotInstanceActor` still passes — the authenticated path still re-signs
  as the acting actor (carol), not the instance actor.

Full fast suite: **1402 pass, 0 fail** (`dotnet test --filter "Category!=Slow"`).

## Live verification (deployed)

- Signed-out `GET /ap/v1/proxy/https%3A%2F%2Fmastodon.social%2Fusers%2Fgnomon` → **200** with gnomon's
  full actor document (previously 401 `{"error":"Request not signed"}`).
- Signed-out `GET /ap/v1/proxy/https%3A%2F%2Fmastodon.social%2Fusers%2Fgnu` → **200** (different
  account, confirming it is not a single-account fluke).
- The S14 actor-detail facet (same anonymous seam) is therefore functional for signature-enforcing
  remotes.

## Files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `ProxyHandler` anonymous path signs as the
  instance actor.
- `tests/Iris.Server.Tests/ProxyFallbackIntegrationTests.cs` — anonymous-retry test repurposed to
  assert the signed-as-instance-actor behavior.
