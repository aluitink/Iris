# 141 — Phase 19.1.2: Follow scenarios (live verification)

## What was done

Executed the 19.1.2 follow scenarios against `@RayvenMX@mastodon.world` over the live Docker stack.
Used the IrisSigner helper (driven via `docker exec`) to make signed ActivityPub requests, and — after
the signature fix — re-verified end-to-end through the **Sample Explorer UI** (Playwright MCP), which
exercises the real client-library authentication (WebFinger → Basic auth → key load → signed client)
rather than a hand-rolled signer.

## Results

| Scenario | Status | Notes |
|---|---|---|
| F1: RayvenMX follows us → we `Accept` | NOT TESTED | Requires RayvenMX's action (they must follow one of our local actors) |
| F2: We follow RayvenMX → their `Accept` arrives | **PASS (signature)** | Follow accepted (202) on our outbox; edge recorded in `following`; **delivery signature now accepted by Mastodon (202)** — the 401 is gone (see F-1912-1 verification). RayvenMX's `Accept` still pending (requires their side to process). |
| F3: `Reject` behavior | NOT TESTED | Requires RayvenMX to follow us first |
| F4: Unfollow via `Undo` | NOT TESTED | Requires F1/F2 to complete first |

## Findings

### F-1912-1: Follow to Mastodon rejected with 401 (signature validation failure)

**Repro:** alice@iris-dev1 publishes a signed `Follow` to RayvenMX@ mastodon.world via her outbox.
The outbox handler records the follow edge and schedules delivery to RayvenMX's inbox. The delivery
worker POSTs the Follow to `https://mastodon.world/inbox` (RayvenMX's shared inbox, resolved from his
actor document) and receives **401 Unauthorized**.

**Wire evidence:**
- `GET iris-dev1:8081/ap/v1/u/alice/following` → includes `https://mastodon.world/users/RayvenMX`
  (the edge is recorded locally)
- iris-a delivery worker log: `Delivery of activity https://iris-dev1.luit.ink/ap/v1/u/alice/follows/1912-1788207165 to https://mastodon.world/inbox returned permanent status 401; dead-lettering immediately`

**Root cause (confirmed, two distinct bugs):** Mastodon's signature validation
(`app/lib/signed_request.rb` → `HttpSignature#build_signed_string`, verified against the live
Mastodon source) reconstructs the signature base as the `headers` lines joined with `"\n"` and **no
trailing newline**, and it only accepts a `sha-256` `Digest`. Iris had two mismatches:

1. **Wrong digest algorithm.** `Signatures.DigestAlgorithm` was `"SHA-512"` and `ComputeDigest`
   emitted `sha-512=…`. Mastodon's `verify_body_digest!` raises
   "Mastodon only supports SHA-256 in Digest header" for anything else, so the request 401s before
   the signature is even checked.
2. **Trailing newline in the signature base.** `Signatures.BuildSignatureBase` appended a final
   `'\n'`. Mastodon joins with `"\n"` (no trailing newline), so the signed byte string differed from
   the one Mastodon reconstructs → `verified?` returns false →
   "Verification failed for …".

**Why the JWK hypothesis (commit 82afc67) was a red herring:** the actor document's `publicKeyPem`
and the JWK (`kty`/`n`/`e`) encode the *same* RSA key (modulus matches byte-for-byte, exponent
65537). Mastodon's `keypair_from_key_id` resolves the key fine from PEM (real Mastodon actors like
RayvenMX serve PEM-only and are signed by other servers). Removing the JWK enrichment and re-testing
produced the identical 401, proving key resolution was never the blocker. The JWK enrichment is
harmless and non-standard, so it was kept.

**Fix:**
- `Signatures.DigestAlgorithm` → `"SHA-256"`; `ComputeDigest` now uses `SHA256.HashData` and emits a
  `sha-256=…` prefix (`Signatures.cs`).
- `Signatures.BuildSignatureBase` no longer appends the trailing `'\n'`; it joins the component lines
  with a newline separator only (`Signatures.cs`). This matches the de facto Fediverse convention
  (Mastodon, Pleroma, Misskey, GoToSocial all join without a trailing newline) even though
  draft-cavage-03's letter says each line is newline-terminated. Iris's signer and verifier share
  `BuildSignatureBase`, so outbound signing and inbound verification stay consistent.

**Verification (live, confirmed):**
- **Unit:** All 195 `Iris.Core.Tests` and all 707 `Iris.Server.Tests` pass (including the outbound
  signature conformance tests and the sign→verify round-trips).
- **Mastodon (direct, via IrisSigner):** After rebuilding the `iris-a` image with the fix and
  re-publishing IrisSigner, a signed `Follow` POST to `https://mastodon.world/inbox` returns **202
  Accepted** (previously 401). Repeated to confirm it is not a fluke.
- **Cross-instance (via the Sample Explorer UI, the real client-library auth path):** Logged on to
  `alice@iris-dev1.luit.ink` (dialing `http://localhost:8081`) via WebFinger + Basic auth — the
  client library resolved the FQDN actor IRI and loaded the key. Composed a note addressed to
  `https://iris-dev2.luit.ink/ap/v1/u/alice`; the UI reported
  `DeliveryResult { StatusCode = 202, IsSuccess = True }`. The iris-a delivery worker then signed and
  POSTed the Create to iris-b's inbox; **iris-b (fixed verifier) accepted it (202)** and the activity
  landed in iris-b's alice outbox/followed-feed graph. The pre-fix iris-a→iris-b 401 storm is gone —
  both instances now agree on the signature base (no trailing newline) and digest (SHA-256).

### F-1911-3 (confirmed root cause): Community follow delivery fails — signing identity not registered

**Repro:** iris-dev1 community follows iris-dev2 community. The outbox handler records the local
follows edge and schedules delivery to the target's community inbox. The delivery worker attempts
to sign the delivery as the community actor but fails.

**Wire evidence:**
- iris-a delivery worker log: `No signing identity registered for actor 'https://iris-dev1.luit.ink/ap/v1/c/iris'`
  (repeated 5 times, then dead-lettered)

**Root cause:** The community's key (seeded in `SampleServer/Program.cs` line 387-392) is stored in
the community's `ExtensionData["publicKey"]` but is **not registered** in the `IKeyProvider` that the
`SigningHandler` uses to resolve signing identities. The `SigningHandler.ResolveIdentity` method looks
up the actor IRI in the key provider, but the community's key was never added there.

 **Fix (committed):** Two sides, both required.
 1. **Server** (`SampleServer/Program.cs`): the seeded community's key is now registered with the
    `IKeyProvider` so the server's outbound `DeliveryWorker` can sign community-sourced deliveries
    (the original dead-letter source). The seeded community reuses the primary actor's key (its
    `publicKey` extension points at it).
 2. **Client** (`SampleBlazorClient/Explorer/ExplorerSession.cs`): after a successful logon,
    `RegisterCommunityIdentity` registers the seeded community's signing identity in the *client's*
    `IKeyProvider` (derived from the resolved actor IRI's host, under the actor's own `#key-1` key).
    This is what lets the Raw delivery screen's "act as" override (and any community follow driven from
    the browser) sign as the community. **Root cause of the live failure:** the method originally read
    the session's `_service` field, but `LogOnAsync` called it *before* assigning `_service`, so it
    always bailed at the "no bundle" guard and never registered. It now takes the freshly-built
    `ClientService` directly.

 **Verification:**
 - **In-process (regression test, `S10RawDeliveryTests.Deliver_ActAsCommunity_SignsAndIsAccepted`):**
   log on as alice through `ExplorerSession`, then `DeliverAsync` a `Follow` to bob's inbox with the
   community IRI as the `X-Iris-Actor` override. Before the fix this threw `KeyNotFoundException`
   ("No signing identity registered for actor"); after the fix it is `202 Accepted` and the follow edge
   is recorded from the community.
 - **Live over genuine sockets (IrisSigner):** signed a `Follow` (actor =
   `https://iris-dev1.luit.ink/ap/v1/c/iris`, object =
   `https://mastodon.world/users/RayvenMX`) with alice's key and POSTed it to
   `https://mastodon.world/users/RayvenMX/inbox` → **202 Accepted**. The community's outbound follow to
   Mastodon is now accepted.
 - **Live UI (Playwright MCP):** on the Raw delivery screen, "act as" the community. A temporary
   on-page diagnostic confirmed the client session now resolves the community identity
   (`keyInStore=True; communityResolves=True`) — the F-1911-3 signing-identity fix is confirmed in the
   running WASM app. (A direct browser POST to a *remote* inbox such as `mastodon.world` still fails with
   `TypeError: Failed to fetch`: the browser cannot make a cross-origin write to a host that does not
   send CORS headers — an environmental limit of driving a remote inbox from the browser, not a signing
   bug. The IrisSigner over genuine sockets above is the authoritative live check.)

## UI verification (Sample Explorer, Playwright MCP)

After the two-bug signature fix, the stack was rebuilt with `docker compose up -d --build` (all three
images: iris-a, iris-b, iris-ui) and re-verified through the Blazor Sample Explorer UI at
`http://localhost:8090`, which uses the client library for authentication and signing.

1. **Log on to iris-a via FQDN.** WebFinger address `alice@iris-dev1.luit.ink`, password
   `iris-sample`, Base URL `http://localhost:8081`. WebFinger resolved the authoritative actor IRI
   `https://iris-dev1.luit.ink/ap/v1/u/alice`; the header shows the logged-on actor. (The WebFinger
   dial uses the explicit Base URL, not the address's FQDN host, so the local host-published port is
   reachable while the IRI keeps its advertised FQDN.)
2. **Cross-instance note (the signature path).** On the Compose screen (posting as
   `https://iris-dev1.luit.ink/ap/v1/u/alice`), posted a note with audience
   `https://iris-dev2.luit.ink/ap/v1/u/alice` (iris-b's alice). The UI reported
   `DeliveryResult { StatusCode = 202, IsSuccess = True }`.
3. **Peer accepted the signature.** iris-b's logs show the inbound
   `POST http://iris-dev2.luit.ink/ap/v1/u/alice/inbox - 202` (no 401). The Create
   (`…/creates/b2c2862fbf46be6c`, content "UI cross-instance signature test note (iris-a -> iris-b)")
   is present in both iris-a's alice outbox (origin) and iris-b's alice outbox/followed-feed graph
   (peer) — proof the delivery worker's signature was verified and the activity persisted.
4. **Followed feed on iris-b** (logged on to `alice@iris-dev2.luit.ink`, dialing
   `http://localhost:8082`) displays the cross-instance items delivered from iris-a
   (`iris-dev1.luit.ink` Follow + Create IRIs).

**Regression guard:** Before the fix was deployed to *both* instances, iris-a (new signer) → iris-b
(old verifier, trailing-newline base) produced a 401 storm. Rebuilding iris-b with the same fixed
image eliminated it — confirming the signer and verifier must move together (they share
`BuildSignatureBase`).

## Environment notes

- The Docker stack is up and healthy (iris-a, iris-b, iris-ui all `Up (healthy)`)
- Public FQDNs resolve: `https://iris-dev1.luit.ink`, `https://iris-dev2.luit.ink`
- RayvenMX's WebFinger and actor document are fetchable and valid
- The IrisSigner tool works correctly (signed requests are accepted by our own outbox)
- The Sample Explorer UI (Blazor WASM) session is in-memory: a hard page reload logs out; in-app nav
  links (client-side routing) preserve the session. The UI's Object viewer cannot fetch a cross-
  instance IRI's object doc (it dials the IRI's own FQDN host, not browser-reachable locally); the
  activity envelope is what is delivered and shown in the feed.

## Test counts

Existing suite: 1191 passing (195 `Iris.Core.Tests` + 707 `Iris.Server.Tests` + 121 `Iris.Client.Tests`
+ 84 `SampleBlazorClient.Tests` + the rest; the +5 over the previous count includes the new
`Deliver_ActAsCommunity_SignsAndIsAccepted` regression test). Signature fix and the F-1911-3
community-signing fix verified live (Mastodon 202 via IrisSigner + cross-instance 202 via the UI).
