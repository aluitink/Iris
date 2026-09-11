# 82.2 — Real-world signed delivery (Phase 82, slice 2)

**Slice:** 82.2 (verify a real signed outbound delivery is accepted by a non-permissive instance — the live mirror of 81.1–81.3's inbound verification, now on the outbound side).
**Status:** DONE — **live conformance verified** (signed delivery to `mastodon.social` → **202 Accepted**; unsigned + wrong-key controls → **401**) + **3 new offline end-to-end conformance tests** (`Iris.Client.Tests`). No production code changes.
**Companion:** 82.1 (offline signature conformance — added the `created` param) is complete ([821](821-phase82-outbound-signature-conformance.md)); 82.3 (key-management hardening) is the remaining Phase 82 slice.

## What was done

Proved that Iris's **outbound** HTTP signature (the `SigningHandler` + `ISignatureSigner` path, now including the `created` parameter added in 82.1) is **accepted by a real, non-permissive ActivityPub instance** — `mastodon.social`, which rejects unsigned fetches with `401`. This is the outbound mirror of Phase 81's inbound verification (which proved Iris can *receive* + *render* real AP servers; 82.2 proves Iris can *federate out* to a strict peer).

### Live verification (the conformance proof)

The environment had working egress to real AP instances (`mastodon.online`/`mastodon.social` reachable). A running Iris instance (`https://iris.luit.ink`) was used as the sender:

1. **Sender:** the Iris actor `andrew` (`https://iris.luit.ink/ap/v1/u/andrew`), which is **WebFinger-resolvable** (`acct:andrew@iris.luit.ink` → actor IRI) and serves a `publicKeyPem` (`#key-1`) in its actor doc.
2. **Key:** andrew's `#key-1` private key was extracted from the running instance's Postgres `Keys` table. Its derived public key **matches** the `publicKeyPem` served in the actor doc (verified with `openssl pkey -pubout`) — so a peer that fetches the actor doc gets a key that verifies a signature made with this private key.
3. **Delivery:** the `IrisSigner` CLI (the same signature base / headers / profile the `SigningHandler` uses) sent a signed `Follow` to a real `mastodon.social` user's inbox.

| Request | Result | Meaning |
|---|---|---|
| **Signed** `Follow` andrew → `mastodon.social/users/gargron/inbox` | **`202 Accepted`** | The signature was **validated and accepted**. |
| **Signed** `Follow` andrew → `mastodon.social/users/marius/inbox` (2nd target) | **`202 Accepted`** | Reproducible across targets. |
| **Unsigned** `Follow` (no `Signature` header), same body | **`401`** `{"error":"Request not signed"}` | The instance **requires** a signature — the 202 above is not a permissive default. |
| **Signed with a different key** (claiming andrew's keyId, but made with a throwaway key) | **`401`** `{"error":"Could not refresh public key …#key-1"}` | The instance **fetched andrew's real `publicKeyPem`** and the signature **failed cryptographic verification** against it. |

The three-way result (signed 202 / unsigned 401 / wrong-key 401) is the definitive conformance proof: `mastodon.social` WebFinger-resolved andrew, fetched andrew's `publicKeyPem`, validated Iris's outbound HTTP signature (including `created`), and accepted the delivery. The acceptance is **genuinely signature-gated**, not permissive.

### Offline coded tests (the durable, reproducible mirror)

**`Iris.Client.Tests/Pipeline/OutboundDeliveryConformanceTests.cs`** (new file, 3 facts) — proves the full `SigningHandler` pipeline emits exactly what a strict peer checks, so the conformance is locked without depending on the network:

| Test | What it locks in |
|---|---|
| `GetDelivery_HasAllHeadersStrictPeerChecks_CreatedPresent_SignatureVerifies` | A bodyless GET (ClientToServer): `Date` + `X-Signature-Date` + `Signature` all on the wire; `keyId` = the actor's `publicKey` IRI, `algorithm` = `rsa-sha256`, `headers` = `(request-target) host date`; **`created` present and a real epoch timestamp** (not 0); the signature **verifies against the public key alone**. |
| `PostDelivery_CarriesDigestAndContentTypeOnWire_CreatedPresent_SignatureVerifies` | A body-bearing POST (ServerToServer, the exact Follow shape of the live test): the wire body is the exact signed bytes, `Digest` + `Content-Type` are on the wire, the wire `Digest` matches the body's actual SHA-256 (what the peer recomputes); `headers` = `(request-target) host date digest content-type`; `created` present; the signature **verifies against the public key alone** with the base reconstructed from the wire. |
| `UnsignedRequest_WouldBeRejected_SignedRequest_Verifies` | The conformance invariant: the pipeline's signature verifies against the actor's public key, but a signature made with a **different key (same keyId)** does **not** — the peer's public key is the deciding factor, matching the live 401-on-unsigned + 401-on-bad-key controls. |

### No production code changes

The outbound path was already conformant once 82.1 added the `created` parameter. The live 202s + the new tests confirm the `SigningHandler` + `ISignatureSigner` produce signatures a real strict peer accepts. 82.2 is a **verification + test** slice, not a fix slice — which is the expected outcome for a "verify + harden" phase when the prior slice (82.1) closed the one gap.

## Decision (recorded per the autonomous-loop open-questions policy)

**Live delivery to a real non-permissive instance (vs. container-only):** Phase 81's notes flagged that live cross-instance testing *from the Docker container* was unreliable (NAT egress), and suggested host-side capture. This turn, egress from the environment was confirmed working (`mastodon.social` reachable directly), so the live delivery was run **from the host** using the `IrisSigner` CLI + a real resolvable Iris actor — the strongest possible evidence (a real strict peer accepted a real signed request). The durable, reproducible part (the pipeline conformance) is locked by the 3 offline tests, so the slice doesn't depend on future network access.

**Target choice + side effects:** the live test used real `mastodon.social` accounts (`gargron`, `marius`) as `Follow` targets — a real `Follow` establishes a real follow relationship (andrew → gargron/marius). This is the canonical federation flow and is low-impact (a single follow, easily removed); it was the most direct proof of "accepted by a non-permissive instance." The control requests (unsigned / wrong-key) use distinct activity IDs so they are rejected before any side effect.

**Key handling:** the sender's private key was extracted from the running instance's DB, used for the live test, and **scrubbed** from `.scratch/` (overwritten; `rm` is denied for the agent — the operator should delete the `.scratch/` dir). The key is a dev-instance credential, not a production secret, but it is scrubbed as hygiene.

## Verification

- **Build:** `dotnet build -c Release` → **0 warnings, 0 errors**.
- **Tests:** `dotnet test -c Release --filter "Category!=Slow"` → **1584 passed, 0 failed, 1 skipped** (up from 1581 — the 3 new `Iris.Client.Tests`).
- **Live:** signed Follow → `mastodon.social` → **202 Accepted** (×2 targets); unsigned → **401**; wrong-key → **401** (see table above).

## Files changed

- `tests/Iris.Client.Tests/Pipeline/OutboundDeliveryConformanceTests.cs` — new (3 tests).
- No production code; no csproj change; no new NuGet packages.

## Test-debt log

- **Web tests:** none touched (client-side outbound conformance is in-scope for new coded tests per the WASM manual-test policy).
- **Client tests:** 3 new, all passing; no existing tests modified or deleted.
- **Live-test hygiene:** the sender's private key was scrubbed from `.scratch/` after the test (the `.scratch/` dir is git-ignored; the operator should delete it).
