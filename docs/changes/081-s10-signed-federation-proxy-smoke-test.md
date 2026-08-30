# 081 — Phase 8 S10: signed cross-container federation + proxy over Docker (smoke test)

> 2026-08-30 · Phase 8 (Sample) · Slice S10 (smoke test: signed federation + proxy over genuine sockets)

## What was built

The Phase 8 smoke test (`scripts/docker-smoke-test.sh`) no longer stops at "the containers are up and can
WebFinger each other". It now asserts that the three-service stack **interoperates as a real system over
genuine network I/O**:

1. **Health** — each instance (`iris-a`, `iris-b`) serves the public WebFinger endpoint for its own seeded
   actor.
2. **Cross-container reachability** — `iris-a` resolves the remote actor `alice@iris-b` by WebFinger over the
   internal Docker network (Docker's built-in DNS resolves the service name).
3. **UI** — `iris-ui` serves the Blazor WebAssembly app's index page (HTTP 200, the app root) over the
   network.
4. **Signed cross-container federation (the S10 upgrade)** — a genuine ActivityPub HTTP-**signed** Follow from
   `iris-a`'s alice to `iris-b`'s alice, published to alice's *own* outbox on `iris-a`, is **server-delivered**
   (signed, over the network) to `iris-b`'s inbox, which **validates** it (resolving alice's key from `iris-a`'s
   actor document) and **records the follow edge** — asserted on `iris-b`'s public followers collection (a read
   that proves the write landed on the remote instance).
5. **Proxy fallback** — `iris-a`'s proxy endpoint relays a request to `iris-b` (HTTP 200), the browser's way of
   reaching a remote instance when it cannot (CORS + it cannot sign).

This is the slice the SAMPLE_PLAN §6.2 names as the smoke test's purpose: proving "signed federation + proxy
fallback over Docker" rather than merely "the containers deploy".

## Key types & files

- **`scripts/docker-smoke-test.sh`** — rewritten checks 4–5 to drive a *signed* request and assert the
  federated edge; checks 1–3 retained. The script now:
  - builds the `IrisSigner` helper (`dotnet publish … -r linux-x64 --self-contained`),
  - copies it + the acting actor's private-key PEM into `iris-a` (via the host — `docker cp` does not support
    container→container),
  - runs the signer inside `iris-a` so it signs the Follow with alice's key and POSTs it to alice's outbox,
  - polls `iris-b`'s public followers collection until the remote follow edge appears (the server→server
    delivery is asynchronous),
  - asserts `iris-a`'s proxy returns `iris-b`'s actor document (200).
- **`tools/IrisSigner`** (new project) — a **minimal self-contained console tool** (references only
  `Iris.Core` + `Iris.Client`) that signs an ActivityPub HTTP request the *exact* way the Iris client's
  `SigningHandler` does — the same signature base (the `(request-target)`, `host`, `date`, + `digest`/
  `content-type` for a body-bearing request), the same `HttpRequestMetadata`, the same `SigningProfile`
  (`ServerToServer` for a body, `ClientToServer` otherwise), the same `HttpSignatureSigner` — then sends it
  with the produced `Signature` + `Date` (+ `digest`/`content-type`) headers and prints the response body +
  status code. It exists because **curl cannot produce an ActivityPub HTTP signature**, and the smoke test must
  drive a *real* signed write over sockets to prove the federation path (not an in-process test double).
- **`samples/SampleServer/Program.cs`** — adds a `FederatedActorDocumentFetcher`: when resolving an actor for
  **signature validation**, it first checks the local key store, then (if the actor is not local and the
  `Iris__PeerBase` env var is set) fetches the peer's actor document over the network (`IActivityPubClient`
  `GetActorAsync`) and reads the public key from it. This is how a real instance validates a delivery from a
  peer it has not seen before: by looking up the peer's advertised key. The DI registration uses a factory that
  reads `Iris__PeerBase` (the peer's in-network base URL) and picks the federated fetcher when it is set, or the
  local-only fetcher otherwise.
- **`docker-compose.yml`** — `iris-a` now sets `Iris__PeerBase: http://iris-b:8080` and `iris-b` sets
  `Iris__PeerBase: http://iris-a:8080` (so each can resolve the other's key over the network); `iris-a` also sets
  the opt-in `Iris__DumpKeyTo: /tmp/iris-alice-key.pem` (see below).
- **`Iris.slnx`** — adds the `IrisSigner` project to a new `/tools/` folder.

## Why a dedicated signer (and why the key is dumped to the container)

The smoke test's signed Follow must be a *genuine* ActivityPub HTTP signature: `iris-a`'s outbox write
**rejects** an unsigned or Basic-auth-only request with **401** (the outbox is a signed write surface — the
client signs the activity, the server records it, and the *server* is the one that signs and delivers it to the
recipient). curl (the smoke test's HTTP client) cannot build the signature base, compute the digest, and produce
the `Signature` header, so the smoke test runs the `IrisSigner` helper *inside* `iris-a` to sign the request with
alice's key.

`IrisSigner` needs alice's **private** key. The key is generated per-boot in memory (it is never committed and
never leaves the process by default), so the sample can, **opt-in and locally only**, dump the acting actor's
private-key PEM to a local path via the `Iris__DumpKeyTo` env var (`iris-a` sets it to
`/tmp/iris-alice-key.pem`, world-readable *inside the container*). The smoke test copies that PEM back into the
same container for the signer to read. This is a sample-only, opt-in, local mechanism: a production instance
never sets `Iris__DumpKeyTo`, and the key never leaves the container (it is only written to, and read back from,
the same container's local filesystem).

## The federated key-resolution path (why the Follow is accepted)

When `iris-b` receives the server-delivered Follow (signed by `iris-a`'s alice, delivered by `iris-a`'s
`DeliveryWorker` as its `InstanceActorId`), it must validate the signature. `iris-b` does not know alice's key
locally (alice is `iris-a`'s actor), so the `FederatedActorDocumentFetcher` fetches `iris-a`'s actor document
over the network (via `Iris__PeerBase`) and reads the public key from it — exactly how a real instance validates
a delivery from a peer. Only then can `iris-b` validate the signature and record the follow edge, which the smoke
test asserts on `iris-b`'s public followers collection.

## Verification

- The full three-service stack boots healthy (`iris-a`, `iris-b`, `iris-ui`).
- All **five** smoke checks pass over genuine sockets:
  - `iris-a`/`iris-b` WebFinger (200, actor IRI present),
  - `iris-a` → `iris-b` WebFinger (200, cross-container reachability),
  - `iris-ui` index page (200, the Blazor WASM app root),
  - **the signed Follow `iris-a` → `iris-b` (HTTP 202) and the federated edge recorded on `iris-b`** (alice@iris-a
    appears in `iris-b`'s public followers — the write landed on the remote instance),
  - `iris-a`'s proxy relaying a GET to `iris-b` (200, the actor document returned).
- `IrisSigner` publishes self-contained (linux-x64) and, run inside `iris-a`, signs the Follow and receives **202**
  from `iris-a`'s outbox (the signature is accepted).
- Full solution builds with **0 warnings**; **883 tests green**.

## Decisions

- **A dedicated signer, not a test double.** The S10 point is to prove *signed federation over genuine sockets* —
  the real `SigningHandler` crypto, the real `DeliveryWorker` delivery, the real `SignatureValidationMiddleware`
  validation, the real network. An in-process test (which the S3–S8 suites already provide) cannot prove the
  *network* path. `IrisSigner` is the minimal bridge that lets the smoke test drive a real signed request; it
  reuses the production crypto (`HttpSignatureSigner`, `Signatures`, `KeyPair`) rather than re-implementing it.
- **The key dump is opt-in, local, and sample-only.** A real instance never sets `Iris__DumpKeyTo`. The smoke test
  needs the key *in the container that owns it* (iris-a), and the only way to get it there is to read it from that
  container's local filesystem — so the sample dumps it there on request. No secret is committed, and the key never
  crosses a network boundary.
- **`FederatedActorDocumentFetcher` is the honest key-resolution model.** A real instance resolves a peer's signing
  key by fetching the peer's actor document; the sample now does exactly that (gated by `Iris__PeerBase`) rather
  than assuming every actor is local. The local-only fetcher is retained for instances with no peer configured.
