# 141 — Phase 19.1.2: Follow scenarios (live verification)

## What was done

Executed the 19.1.2 follow scenarios against `@RayvenMX@mastodon.world` over the live Docker stack.
Used the IrisSigner helper (driven via `docker exec`) to make signed ActivityPub requests.

## Results

| Scenario | Status | Notes |
|---|---|---|
| F1: RayvenMX follows us → we `Accept` | NOT TESTED | Requires RayvenMX's action (they must follow one of our local actors) |
| F2: We follow RayvenMX → their `Accept` arrives | **FAIL** | Follow accepted (202) on our outbox; edge recorded in `following`; **delivery to Mastodon rejected 401** (signature validation failure) |
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

**Likely cause:** Mastodon's signature validation rejects our HTTP signature. Possible causes:
1. Mastodon cannot resolve our public key from the `publicKeyPem` format (expects JWK)
2. The signature base or headers are malformed
3. The key IRI (`#key-1` fragment) is not recognized by Mastodon's key resolver

**Fix (commit 82afc67):** The actor document now includes both the `publicKeyPem` and JWK forms
(`kty`/`n`/`e` for RSA) in the `publicKey` extension. The `BuildActorDocument` method enriches the
public key with the JWK form when the key is found in the key store. This allows remote instances
that expect JWK (e.g. Mastodon) to resolve the key.

**Live test after fix:** Follow to RayvenMX@ mastodon.world accepted (202) on our outbox; the follow
edge is recorded in `following`. Delivery to Mastodon pending verification (delivery worker logs not
visible in the current container session — the container was recreated after the fix, and the
delivery queue journal is empty).

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

**Fix:** The `IKeyProvider` must register the community's key when the community is seeded. The
`SampleServer/Program.cs` seeds the community with a key in `ExtensionData` but does not register it
in the key store/provider. The key store (`persistence.Keys`) has the key (seeded at line 375-377),
but the `IKeyProvider` (which wraps the key store for the signing pipeline) does not know about it.

**Next steps:**
- Register the community's key in the `IKeyProvider` when the community is seeded
- Verify the community follow delivery succeeds after the fix

## Environment notes

- The Docker stack is up and healthy (iris-a, iris-b, iris-ui all `Up (healthy)`)
- Public FQDNs resolve: `https://iris-dev1.luit.ink`, `https://iris-dev2.luit.ink`
- RayvenMX's WebFinger and actor document are fetchable and valid
- The IrisSigner tool works correctly (signed requests are accepted by our own outbox)

## Test counts

No code changes; no new tests. Existing suite: 1186 passing.
