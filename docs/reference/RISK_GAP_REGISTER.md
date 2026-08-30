# Risk & Gap Register (Phase 9)

> Phase 9 is **ideation + preparation only** — no live interop is run here. This document is the
> **risk & gap register** (ROADMAP bullet 6, the final Phase 9 bullet): the known unknowns that live
> testing (Phase 13) must resolve, and the predicted capability gaps that the live suite will confirm
> and the follow-up phases will fix. It **synthesizes** the gaps and unknowns established by the other
> Phase 9 artifacts into one tracked register, so Phase 13 has a single source of truth for "what we
> expect to break, and why."
>
> Companion docs (the register's sources of truth): [COMPATIBILITY_MATRIX.md](COMPATIBILITY_MATRIX.md)
> (the 6 predicted gaps + the per-scenario expectations), [ENUMERATION_DESIGN.md](ENUMERATION_DESIGN.md)
> (the Threads/Lemmy platform unknowns + reconnaissance guardrails), [INTEROP_TEST_HARNESS.md](INTEROP_TEST_HARNESS.md)
> (the `Gap`-scenario mechanism that confirms each gap live), [DEPLOYMENT_PREP.md](DEPLOYMENT_PREP.md)
> (the FQDN/TLS + operational risks).

## 1. How to read this register

Each entry has:

- **ID** — a stable identifier (G-# for predicted capability gaps, U-# for platform unknowns, O-# for
  operational risks, H-# for harness/test risks).
- **Severity** — **Blocker** (blocks a core use case), **High** (breaks a common interop path),
  **Medium** (degrades but workable), **Low** (edge case / cosmetic).
- **Status** — **Predicted** (expected from the source, not yet confirmed live) / **Confirmed**
  (Phase 13 live test observed it) / **Mitigated** (a fix or workaround landed) / **Accepted**
  (known, deliberately not fixed in v1).
- **Source** — the Phase 9 artifact + section that established it.
- **Phase 13 confirmation** — the specific matrix scenario (or reconnaissance step) that confirms it.
- **Disposition** — what happens next (which phase fixes it, or the accepted limitation).

The register is a **living document**: Phase 13 updates the Status column as live tests run (Predicted →
Confirmed/Mitigated/Accepted), and the fix phases update it as gaps close. Phase 9's job is to make every
known unknown *tracked* so nothing is silently dropped.

## 2. Predicted capability gaps (from COMPATIBILITY_MATRIX.md §5)

These are the six gaps the **current** implementation will not satisfy, in priority order. Phase 13's
`Gap`-scenario assertions (INTEROP_TEST_HARNESS.md §3.4) confirm each surfaces as predicted. They are
the concrete, code-level unknowns — not platform quirks.

| ID | Gap | Severity | Status | Phase 13 confirmation | Disposition |
|---|---|---|---|---|---|
| **G-1** | **No outbound `Create`.** The server writes posts to local outboxes but never sends a signed `Create` activity to followers' inboxes. Remote followers never receive our posts. | **Blocker** | **MITIGATED (Phase 11, Slice 11.7).** Inbound `Create` → `CreateActivityHandler` records it in the author's outbox and **server-delivers a signed `Create` to every remote, non-blocked follower's inbox** (plus relay fan-out, F-06) — a local post now federates. *Residual:* the **outbox-publish** path (`POST /u/{handle}/outbox`, the client's `PostNoteAsync` write surface) fans out to **only the first** remote follower (`RecordCreateLocalAsync` returns one recipient) — a single-follower limitation, not a missing capability. | Matrix **C1** (out): "remote follower's inbox did NOT receive a signed `Create`." | Closed for the core "post and have it federate" use case (the inbox path). Follow-up: make the outbox-publish path fan out to the full remote-follower set (mirror `CreateActivityHandler`'s loop). |
| **G-2** | **No outbound `Reject` / `Undo`.** `FollowIris.BuildReject` exists but has no callers (follows are always accepted); no `Undo` is constructed (un-follow is not delivered). | High | **MITIGATED (Phase 11, Slice 11.10 + this slice, change 084).** Both halves now have live paths: **(a) `Undo` (un-follow)** — the outbox-publish pipeline server-delivers a client-constructed `Undo` (un-follow / unblock / unflag) to the remote target's inbox and removes the local edge; the inbound `UndoActivityHandler` removes edges for a remote's Undo. **(b) `Reject`** — `manuallyApprovesFollowers` gates auto-accept (a pending follow is recorded but not auto-accepted), and the **operator reject endpoint** (`POST /u/{handle}/follows/{followId}`, Basic-auth) builds `FollowIris.BuildReject`, records it in the activity store + outbox, removes the provisional edge, and **server-delivers the `Reject` back to the follower's inbox** (signed as the local actor) so the remote removes its edge. | Matrix **F3** (out, reject never sent) + **F4** (out, `Undo` never sent). | Closed for the core paths. *Residual:* `FollowIris.BuildUndo` itself has no production caller (the Undo is client-constructed; the server re-delivers it) — functionally equivalent; and a local **community** cannot un-follow (no community outbox write surface — see G-3). |
| **G-3** | **No outbound group-follow.** The server never follows a remote `Group` (no outbound group-follow path); Iris communities can't initiate following a remote community. | High | **MITIGATED (Phase 12, change 085).** A community outbox write surface now exists: `POST /ap/v1/c/{name}/outbox` (`CommunityOutboxPublishHandler`) accepts a signed `Follow` or `Undo` from the community actor, records the follow in the community's follows set + activity store + outbox, and server-delivers a signed `Follow`/`Undo` to a remote target's inbox (signed as the community). The inbound `UndoActivityHandler` removes the correct `(community → otherParty)` edges when a remote community's Undo arrives. 8 integration tests cover 401/404/400/403, signed follow, federation + Accept finalization, local undo, and cross-instance undo delivery. | Matrix **G2** (out): "we did not initiate a follow of the remote community." | Closed — a local community can now initiate + undo a follow of a remote actor/community. |
| **G-4** | **No global search / directory.** Only per-community search (`/ap/v1/c/{name}/search`) exists; no global search or directory endpoint for a platform to index Iris communities. | Medium | **MITIGATED (Phase 11, Slice 11.8).** A global instance-local search/directory endpoint now exists: `GET /ap/v1/search?q=&limit=&offset=` (`GlobalSearchHandler` → `GlobalSearchService`) searches **local actors** (name / preferredUsername / IRI — a directory) and **stored content objects** (content / name), paged. Per-community search remains at `/c/{name}/search`. *Scope:* instance-local surface only (no cross-instance/relay search — consistent with F-13). | Matrix **S1** (out): "a platform's global search/directory does not surface our community." | Closed for the instance-local directory/search use case (a platform can index this instance's communities + actors + content). |
| **G-5** | **No EdDSA validation.** The server supports only RSA `rsa-sha256` + EC P-256 `ecdsa-p256-sha256`; no Ed25519. EdDSA-signed inbound posts are rejected (401). | High | **MITIGATED (Phase 11, Slice 11.9).** Ed25519 (EdDSA) is now a first-class inbound signature algorithm: `RemoteInboundKeyResolver` classifies `kty:"OKP"` JWKs + Ed25519 PKIX PEM as `KeyAlgorithm.Ed25519`, and `HttpSignatureVerifier.VerifyWithKey` → `Ed25519Key.Verify` (BouncyCastle) verifies them alongside `rsa-sha256` / `ecdsa-p256-sha256`. `Ed25519Key.Generate` produces them; the `ed25519` algorithm label is wired into the `Signature` header. | Matrix **SIG2** (in): "an Ed25519-signed POST is rejected." | Closed — an Ed25519-signed inbound request resolves + validates. *Note:* `KeyPairGenerator` deliberately excludes Ed25519 (use `Ed25519Key.Generate` directly). |
| **G-6** | **Person-inbox `Create`/`Like` uninterpreted.** Posts to a local *person* inbox are stored but not surfaced in a personal feed (only community inboxes interpret them via the catch-all). | Medium | **RESOLVED (Slice 11.6, J-8).** A dedicated `CreateActivityHandler` records an inbound `Create` in the recipient's outbox: for a local **person** it records in the person's outbox (the author's own post is now surfaced in their feed); for a local **community** it records in the members' outboxes (delegating to the shared `CommunityContentRecorder`). `dotnet test` 491→500. | Predicted | Matrix **C2** (in): "a `Create` to a person inbox is stored but not in a personal feed." | Closed — a person-inbox `Create` handler records into the person's outbox (the community member-recording loop is shared via `CommunityContentRecorder`). Resolved Decision #43. |

 > **G-1 was the headline — now mitigated.** Iris is no longer a *receiving-only* instance: a local post
  > is now federated — `CreateActivityHandler` server-delivers a signed `Create` to every remote, non-blocked
  > follower's inbox (the core "post and have it federate" use case works). The only residual is a
  > **single-remote-follower limitation on the outbox-publish path** (`POST /u/{handle}/outbox` fans out to
  > the first remote follower, not the full set). G-3 (outbound community-follow) is now the largest
  > *remaining* capability gap.

> **Cross-reference — user-journey walkthrough (Phase 11, Slice 11.2).** [PHASE_11_USER_JOURNEYS.md](PHASE_11_USER_JOURNEYS.md) re-derives these capability gaps **from the user's point of view** (each capability walked end-to-end as an app would drive it) and confirms G-1…G-6 (as J-18/J-10/J-11/J-15/J-4/J-8 respectively). It also surfaces a **new usability-friction register (J-1…J-22)** the capability register does not cover — most notably **J-6 (client "post" API)**, and (now resolved) **J-9 (client "follow" API — Slice 11.4)**, **J-6's client half (client "post" API — Slice 11.5)**, **J-8 (record a local post in the author's outbox — Slice 11.6)**, and **J-21 (client discovery service not exposed in the bundle — Slice 11.3)**: the *client* write side is now mostly reachable and a local post is now *surfaced*, even though the *read* side was already solid. (J-21 is closed — `IrisClientBundle` exposes `Discovery`/`ResolveActorAsync`; J-9 is closed — `IActivityPubClient.FollowAsync(actorId, targetId)` builds + delivers a signed `Follow` to `targetId.InboxOf()`; J-6's client half is closed — `IActivityPubClient.PostNoteAsync(actorId, content, to)` builds a `Create` (embedded `Note`, deterministic id) + delivers it to the author's own inbox; J-8 is closed — `CreateActivityHandler` records an inbound `Create` in the author's outbox, so a local post is surfaced; J-18 is closed — `CreateActivityHandler` federates a local post to the author's remote followers, so a local post federates; **J-10 is closed (Slice 11.10)** — `manuallyApprovesFollowers` gates auto-accept and `BuildReject` has a live path, so an operator can reject a follow.) The remaining follow-moderation work is a dedicated UI/API (list pending follows, one-click approve/reject) — Phase 12.

## 3. Platform unknowns (from ENUMERATION_DESIGN.md §3.5 + COMPATIBILITY_MATRIX.md §3)

These are **not** Iris bugs — they are platform behaviors Iris has not yet observed live, and which
Phase 13 must resolve by running the reconnaissance + matrix against each platform. They determine
*which* platforms are viable targets and *what* their interop looks like.

| ID | Unknown | Severity | Status | Phase 13 resolution | Disposition |
|---|---|---|---|---|---|
| **U-1** | **Threads' non-standard AP surface.** Meta's Threads implementation has a non-standard ActivityPub surface and **no public directory/search**. Enumeration is limited to WebFinger on known handles + graph traversal from seeds; its AP endpoints may not conform to the scenarios in the matrix. | **Blocker** (for Threads as a target) | Predicted | Reconnaissance (ENUMERATION_DESIGN §3.1–3.4) + matrix scenarios against a Threads instance; document every non-standard behavior encountered. | **Likely deferred or partial in Phase 13** (COMPATIBILITY_MATRIX.md §3). Threads is the hardest target; if its AP surface is too non-standard, it is recorded as a limitation and the matrix focuses on Mastodon/Pleroma/Lemmy first. |
| **U-2** | **Lemmy's group semantics.** A Lemmy community is **not** a pure AS 2.0 `Group` — it has its own `t:`/`c:` IRIs and a different follow flow. The group-interop scenarios (G1–G4) may not map 1:1. | High | Predicted | Matrix **G1–G4** against a Lemmy instance; document how Lemmy's community follow differs from the AS 2.0 `Group` flow. | If the mismatch is small, the matrix notes it; if large, Lemmy community interop is a separate follow-up. User (non-community) interop is the safer Lemmy path. |
| **U-3** | **Cursor-based pagination mismatch.** Iris follows `next` links (forward-only, no cursor). A platform that uses **cursor-based** pagination will cause Iris to stop at page 1. | Medium | Predicted | Matrix **P2** (out): "we page through a platform collection" — observe whether the platform uses `next` vs. cursor. | If a target uses cursors, the client needs a cursor-following path (a `CollectionQuery` extension). Not required for `next`-based platforms (Mastodon/Pleroma are `next`-based). |
| **U-4** | **Mastodon extended-AS round-trip.** Mastodon-specific properties (`sensitive`, `spoilerText`, `inReplyTo` semantics, `toot`/`Video`/`Article` types) pass through Iris's unknown-property path. Unknown whether they round-trip intact or are dropped. | Medium | Predicted | Matrix **C4** + **T3** (in): receive a Mastodon extended-AS object; assert it is stored (not rejected) and check whether the extension survives a read-back. | If extensions are dropped on read-back, it's a fidelity limitation (the object federates, but platform-specific fields are lost). Accepted for v1 if the core content round-trips. |
| **U-5** | **HTTP Signature draft versioning.** Iris implements `draft-cavage-http-signatures-03`; Mastodon uses `draft-10`. The base-string format is the same for the headers Iris checks, so they are expected compatible — but live test must confirm a strict draft-10 sender is accepted. | Medium | Predicted | Matrix **SIG1** (in): a Mastodon-signed POST validates and is accepted. | If a strict draft-10 sender is rejected, the verifier needs a draft-10 path. Low likelihood (the base format matches), but the confirmation is cheap. |
| **U-6** | **NodeInfo `software`/`version` parsing for target selection.** The reconnaissance keys the matrix on the platform (from NodeInfo `software.name`/`version`). Unknown whether every target exposes a parseable NodeInfo (some do, some don't). | Low | Predicted | Reconnaissance §3.1 (NodeInfo fetch); record the platform for each target. | If a target has no NodeInfo, the platform is identified out-of-band (the operator knows which instance it is). Non-blocking. |

## 4. Operational risks (from DEPLOYMENT_PREP.md + ENUMERATION_DESIGN.md §5)

These are the risks of *running* a public Iris instance against the live federation — not code gaps,
but operational behaviors that live testing + deployment must handle. They are the "known unknowns"
the ROADMAP bullet calls out (rate limits, moderation, TLS, key rotation).

| ID | Risk | Severity | Status | Mitigation | Disposition |
|---|---|---|---|---|---|
| **O-1** | **Rate limits / 429 from third-party instances.** Outbound deliveries (follows, `Create`s) and inbound recon may hit a platform's rate limit (429). | High | Open | The client `RetryHandler` honors `Retry-After` (delta-seconds) + exponential backoff (Resolved #23); the proxy has a per-actor rate limit (Phase 6); the reconnaissance + live suite enforce a request budget + per-host rate limit (INTEROP_TEST_HARNESS.md §3.3, ENUMERATION_DESIGN.md §5). | Phase 13 tunes the budget per platform. A platform that hard-rejects on 429 (no `Retry-After`) is a live-test finding. |
| **O-2** | **TLS certificate provisioning + bind-vs-advertise.** The operator must provision a TLS cert + reverse proxy; `BaseUri` must be the public FQDN (not the internal bind address) or federation breaks (a remote instance fetches the advertised IRI, which must round-trip to the instance holding the actor). | **Blocker** (for any live test) | Open (operator action) | DEPLOYMENT_PREP.md §1 (FQDN & TLS plan) + §2 (bootstrap runbook, Step 4 "verify discovery" — if the actor `id` shows the internal bind address, `BaseUri` is wrong). | Operator-provided (Decision #40). Phase 13 is **blocked** on this — no live test runs until the FQDN is live and discovery is verified. |
| **O-3** | **Key rotation / revocation.** If an actor's key is rotated, the remote `RemoteKeyCache` (1h TTL) holds the stale key until it expires; an inbound POST signed with the new key fails validation until the cache is refreshed. | Medium | Open | The `RemoteKeyCache` TTL (1h) bounds the staleness; `?refresh=true` forces a bypass. A key-rotation runbook (re-fetch the actor doc, invalidate the key cache) is a follow-up. | Phase 13 documents the rotation procedure. A live key-rotation test (rotate a key, confirm the new key validates after a cache refresh) is a candidate Phase 13 scenario. |
| **O-4** | **Moderation / content policy.** Iris has no moderation (hide/remove posts, block, filter) in v1. A federated post from a platform that later deletes/moderates it is not withdrawn from Iris. | Medium | Open | Out of scope for v1 (ROADMAP Phase 12+ — "moderation (hide/remove posts), community-level blocking"). | Accepted limitation for v1. Documented as a known gap; a future phase adds moderation. |
| **O-5** | **PII / data exposure in enumeration.** The reconnaissance resolves real actor IRIs + types; it must not collect or store private data. | Low | Open | ENUMERATION_DESIGN.md §5 guardrails: read-only GETs only, bounded, gated/opt-in, rate-limited, **no PII beyond what the platform publishes publicly**. | Enforced by the `LiveGuard` + budget in the live suite. A live-test finding if a target's public data includes unexpected PII. |
| **O-6** | **In-process persistence is not durable.** `InMemoryPersistenceProvider` (used by the samples + the live suite's in-process self-tests) loses state on restart. A *production* Iris instance needs a durable persistence provider. | High (for production) | Open | The persistence seam (`IPersistenceProvider`) is the extension point; a durable provider (e.g. SQLite/Postgres) is a follow-up phase. The live suite's in-process self-tests use in-memory (sufficient for proving the harness); the live *scenarios* run against a real deployed instance. | Phase 13 runs against a deployed instance (the FQDN host), so in-memory persistence is not the live path. A durable provider is a separate phase (post-Phase 9). |

## 5. Harness / test risks (from INTEROP_TEST_HARNESS.md)

These are the risks in the *test harness itself* — the thing that runs Phase 13. They are tracked so
Phase 13 doesn't discover them the hard way.

| ID | Risk | Severity | Status | Mitigation | Disposition |
|---|---|---|---|---|---|
| **H-1** | **The hoisted harness is a design, not yet code.** The `ActivityPubHost`/real `FederationTopology`/`ScenarioRunner` types are designed (INTEROP_TEST_HARNESS.md §3.2) but not yet implemented; the ~9 copy-pasted per-test helpers in `Iris.Server.Tests` are the current reality. | High | Open (Phase 10) | Phase 9 establishes the *shape*; Phase 10 (code consolidation) moves the hoisted types into `Iris.Testing` and rewires the existing tests. Phase 13 consumes the consolidated harness. | Phase 10 is the implementation slice; Phase 13 depends on it. The Phase 9 in-process skeleton (§3.6) proves the shape compiles + the in-process path runs, de-risking the Phase 10 move. |
| **H-2** | **`Iris.Testing`'s `TestServerInstance.ActorIri` uses `/u/{handle}`, but the real server serves `/ap/v1/u/{handle}`** — the harness hasn't caught up to the Phase 3+ route shape. | Medium | Open (Phase 10) | The hoisted `ActivityPubHost` (Phase 10) uses the real route shape (`/ap/v1/u/{handle}`); the scaffold's `/u/{handle}` is replaced. | Fixed as part of the Phase 10 consolidation (the `ActivityPubHost` is the real-pipeline replacement). |
| **H-3** | **The live suite could hammer a third-party instance** if a scenario's budget is misconfigured. | Medium | Open (Phase 13) | The `ScenarioRunner` enforces a request budget + per-host rate limit (INTEROP_TEST_HARNESS.md §3.3); the `LiveGuard` skips when the FQDN/target config is absent. | Phase 13 sets the budget per platform; a runaway scenario is a live-test finding (the budget guard makes it *impossible* to exceed the configured cap). |
| **H-4** | **A live test run is non-idempotent** — it creates real follows/posts on third-party instances. Re-running leaves residue (the follow isn't undone, the post isn't deleted) because G-2 (no `Undo`) + G-1 (no outbound `Create` withdrawal) are open. | Medium | Open (Phase 13) | The live suite is **opt-in + gated** (Decision #41); runs are deliberate, not automated in the default CI. A manual cleanup step (un-follow via the platform's UI, delete the post) is documented. *Residue is now partly mitigated:* G-2's `Undo` path is live (a local un-follow is server-delivered), so a live follow can be undone by the local instance — the remaining residue is the **outbound `Create`** (a post is not withdrawn; there is no outbound `Delete` of a federated post yet). | Accepted for Phase 13 (the post-residue is the cost of live testing against real instances). A future phase adding post-withdrawal makes re-runs fully clean. |

## 6. Register summary + Phase 13 entry criteria

**Predicted gaps to confirm live (Phase 13):** G-3 (outbound community-follow — **the largest remaining**
capability gap; High). **Mitigated (live-confirmation pending):** G-1 (outbound `Create` — core path;
residual single-remote-follower limit on the outbox-publish path), G-2 (outbound `Reject` + `Undo`), G-4
(global search/directory), G-5 (Ed25519), G-6 (person-inbox `Create`). **Platform unknowns to resolve
(Phase 13):** U-1 (Threads, Blocker-for-target), U-2 (Lemmy, High), U-3,
U-4, U-5 (Medium), U-6 (Low). **Operational risks to manage (deployment + Phase 13):** O-2 (Blocker,
operator action), O-1 (High), O-3, O-4, O-6 (Medium/High), O-5 (Low). **Harness risks to close
(Phase 10 → Phase 13):** H-1 (High), H-2, H-3, H-4 (Medium).

**Phase 13 entry criteria** (all must hold before live interop runs):

1. **O-2 resolved** — the operator has provisioned the FQDN + TLS + reverse proxy, and the bootstrap
   runbook's "verify discovery" step passes (the actor `id` shows the public FQDN, not the internal bind
   address). *This is the hard blocker.*
2. **H-1 + H-2 resolved** — Phase 10 has consolidated the harness (the `ActivityPubHost`/real
   `FederationTopology` are in `Iris.Testing`, the existing tests run on them, the route shape is
   correct).
3. **The live suite compiles + the in-process self-tests pass** (the Phase 9 skeleton / Phase 10
   consolidation proves the harness works in-process before it's pointed at a FQDN).
4. **The reconnaissance (ENUMERATION_DESIGN.md) has run** against the targets, producing the target
   inventory (resolved user/community IRIs + platform/version from NodeInfo) that the matrix consumes.

When those hold, Phase 13 is "fill in targets + run the matrix" — the harness, the scenarios, and the
expected outcomes are all defined. The register's job then is to be the scoreboard: each entry's
Status flips from **Predicted** to **Confirmed** / **Mitigated** / **Accepted** as the live tests run,
and the fix phases (post-Phase 9) close the **Blocker**/**High** gaps (G-1 first).

## 7. What this phase does NOT do

- **No live interop.** No third-party instance is contacted; no scenario is run; the register's Status
  column stays **Predicted**/**Open** throughout Phase 9. Phase 13 flips the statuses as it runs.
- **No code changes.** The gaps (G-#) and harness work (H-#) are *tracked*, not fixed here. `dotnet
  build` / `dotnet test` are unchanged (444/444).
- **This is the final Phase 9 bullet.** With this slice, Phase 9 is **complete**: all five deliverables
  (FQDN/TLS plan + bootstrap runbook, real-user enumeration design, compatibility matrix, test-harness
  extension, risk & gap register) are done, all grounded in the real config/client/server/test surface.
  Phase 10 (Project & Test Review) is next.
