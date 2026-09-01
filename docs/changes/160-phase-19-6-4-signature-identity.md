# 160 — Signature identity: the outbound `Signature` `keyid` is the acting actor's key, not the instance actor's

> 2026-09-01 · Slice 19.6.4 (Phase 19.6 — Architectural expectations: client↔server interaction) · the
> key IRI in the `Signature` header matches the acting actor's `publicKey` id — resolvable from the actor
> document — for both outbound paths Iris signs with a per-actor key

## What was built

**19.6.4** asks: "Deliveries are signed as the *acting* actor (decision 029), resolvable by the receiver
from the actor document (not the instance actor); the proxy path re-signs as the acting actor (decision
037). Verify with the raw inspector (key IRI in the `Signature` header matches the acting actor's
`publicKey` id)."

Reading the path confirmed the production behavior is **already implemented** for both outbound paths; the
missing piece was a pin that captures the real signed outbound `Signature` header and asserts its `keyid`
is the **acting** actor's key in a setup where the acting actor is **distinct from** the instance actor.
That distinction is the crux of the invariant — the existing signature tests
(`OutboundSignatureConformanceTests`) register a *single* actor (the instance actor **is** the acting
actor), so they cannot tell "signs as the acting actor" from "signs as the instance actor."

Both paths Iris signs with a per-actor key carry the acting actor via the Iris-internal `X-Iris-Actor`
header, which the `SigningHandler` resolves in preference to the shared client's `ActorId`:

- **Outbound delivery** (decision 029): the `DeliveryWorker` creates **one** long-lived signed client as
  the *instance actor*. Each `DeliveryJob` may carry a *distinct acting actor* (a local actor whose note /
  announce is being relayed, a community, …); when `job.ActorIri` is set, the worker adds
  `X-Iris-Actor: {actorIri}`, and the `SigningHandler` signs with **that** actor's key — the `keyid` in
  the `Signature` header becomes the acting actor's key IRI (`{actingActor}#key-1`), the key served in the
  acting actor's document `publicKey` extension.
- **Proxy re-sign** (decision 037): the gated proxy endpoint identifies the authenticated actor from Basic
  auth and sets `X-Iris-Actor: {actorIri}`, so the browser's proxied request is re-signed with **that**
  actor's key (the `keyid` is the acting actor's `#key-1`), not the instance actor's.

What was **not** pinned was the 19.6.4 identity as one scenario per path: a distinct acting actor, a real
signed outbound request, and the assertion that the `keyid` equals the acting actor's `publicKey` id (and
not the instance actor's). This slice adds that pin for both paths.

## Key types & files

- `src/Iris.Server/Delivery/DeliveryWorker.cs` — **unchanged** (`DeliverAsAsync` sets
  `X-Iris-Actor: {job.ActorIri}` when the job carries a distinct acting actor; the shared client is
  created as the instance actor).
- `src/Iris.Server/ActivityPubServerExtensions.cs` — **unchanged** (the proxy endpoint sets
  `X-Iris-Actor: {actorIri}` for the authenticated actor and creates the client with `ActorId = actorIri`).
- `src/Iris.Client/Pipeline/SigningHandler.cs` — **unchanged** (`ResolveIdentity` prefers the
  `X-Iris-Actor` override over the handler's `ActorId`; the `keyid` is the resolved identity's key IRI).
- `tests/Iris.Server.Tests/OutboundSignatureIdentityIntegrationTests.cs` — **new** (two tests; see below).

## Tests

1258 → **1260** passing (+2: the two signature-identity tests).
Full `dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`); `dotnet format` clean on the
changed file.

- `OutboundDelivery_SignsAsActingActor_NotInstanceActor` (decision 029) — a `DeliveryWorker` over a
  capturing transport with **two distinct** local actors: alice (the instance actor — the host's
  `InstanceActorId`, the actor the shared delivery client is created as) and bob (a second local actor). A
  `DeliveryJob` carrying bob as the acting actor is enqueued; the captured signed request asserts the
  `X-Iris-Actor` override is bob and the `Signature` `keyid` is `bob#key-1` (bob's `publicKey` id) — **not**
  `alice#key-1` (the instance actor's key). This proves the acting actor's own key signs the delivery.
- `Proxy_ResignsAsActingActor_NotInstanceActor` (decision 037) — two `TestServer`s: A (alice = the
  instance actor) hosts a second local actor (carol, registered via `ExtraLocalActors` at `#key-1`). A
  browser authenticated as **carol** posts a proxied GET; A's outbound transport (a capturing handler)
  records the re-signed request. The captured request asserts the `X-Iris-Actor` override is carol and the
  `Signature` `keyid` is `carol#key-1` (carol's `publicKey` id) — **not** `alice#key-1` (the instance
  actor's key). This proves the proxy re-signs as the acting (authenticated) actor, distinct from the
  instance actor.

## Live verification (deferred — a live item)

The server-side signature identity is pinned by the new tests (the real signed outbound `Signature`
header's `keyid` asserted against the acting actor's `publicKey` id, for both the delivery and the proxy
path). The **raw-inspector (UI) half** — reading the rendered `keyid` from the inspector UI while driving a
write through the two-instance Docker environment and confirming it matches the acting actor's `publicKey`
id — is the remaining live-verification item for 19.6.4. It requires the two-instance Docker environment
(dev1-public host unreachable from CI), so it is deferred as a live item; the server-side invariant it
exercises is already covered in CI by the new tests.

## Decisions

- **The pin is a verification, not a production change.** Both the delivery's per-actor signing (decision
  029) and the proxy's per-actor re-sign (decision 037) were already implemented end-to-end. The slice is
  a verification pin, consistent with how 19.6.2 (change 156), 19.6.3 (change 157), 19.6.5 (change 158),
  and 19.6.6 (change 159) were closed.
- **The acting actor must be distinct from the instance actor.** The invariant "the `keyid` is the acting
  actor's key, *not* the instance actor's" is only falsifiable when the two differ. Each test therefore
  constructs a setup where the instance actor (alice) and the acting actor (bob / carol) are different
  local actors, and asserts the `keyid` equals the acting actor's `#key-1` and **not** alice's `#key-1`.
  The existing `OutboundSignatureConformanceTests` cannot make this assertion (its single actor is both
  the instance and the acting actor).
- **The `#key-1` key-IRI convention is the `publicKey.id`.** Both the test-seeded keys
  (`TestSeeder.SeedPersonWithKey`) and the direct `KeyPairGenerator` keys use `{actorIri}#key-1`, and the
  host factory registers local (and `ExtraLocalActors`) keys at that convention — so the asserted `keyid`
  is exactly the key IRI a peer would read from the acting actor's document `publicKey` extension.
- **The proxy test captures rather than validates end-to-end.** `ProxyFallbackIntegrationTests` already
  proves the proxied request is accepted by the peer (which resolves the acting actor's key); this test
  instead captures the re-signed request A emits and asserts the explicit `keyid`, with the acting actor
  distinct from the instance actor — the precise 19.6.4 / decision-037 identity, without depending on the
  peer's validation as the sole signal.
