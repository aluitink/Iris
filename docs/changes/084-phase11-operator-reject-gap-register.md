# 084 — Phase 11.11: operator `Reject` endpoint for `manuallyApprovesFollowers` actors + gap-register refresh

> 2026-08-30 · Phase 11 (Implementation Gaps & Usability Exploration) · Gap closure (G-2 Reject half / J-10) + gap-register finalization

## What was built

Two related things:

1. **The operator `Reject` path for `manuallyApprovesFollowers` actors** — the live outbound half of the
   manually-approves-followers gate (gap G-2's `Reject` half / user-journey J-10). Previously
   `FollowIris.BuildReject` existed but had **no callers**: a follow on a `manuallyApprovesFollowers`
   actor was recorded but never auto-accepted (Slice 11.10, Decision #46), and there was no way to
   *reject* it — only to auto-accept or leave it pending forever. This slice adds the reject surface.
2. **A refresh of the risk & gap register** (`docs/reference/RISK_GAP_REGISTER.md`) to its verified
   implementation status — the register had gone stale (G-1/G-2/G-4/G-5 were listed as "Predicted" while
   actually implemented; G-6 was already marked resolved).

## The operator reject endpoint

- **Route:** `POST /ap/v1/u/{handle}/follows/{**followId}` (`LocalFollowRejectHandler`, name
  `local-follow-reject-endpoint`), in `src/Iris.Server/ActivityPubServerExtensions.cs`.
- **Auth:** Basic auth via `IActorCredentialValidator` — the **same credential seam** as the mute
  (`LocalMuteHandler`) and relay (`LocalRelayHandler`) endpoints. A reject is a local actor's
  moderation decision, so it is owner-only (the acting actor's credentials), not a signed federation
  delivery.
- **Body:** the original `Follow` activity (its `id` is the follow being rejected). The handler
  reconstructs the deterministic `Reject` from it.
- **Behavior:**
  1. Authenticate the acting actor (else **401**); the actor must exist (else **404**).
  2. Read the body; it must deserialize to a `Follow` with an `id` (else **400**).
  3. The `Follow`'s target must be this local actor (else **409** Conflict — a reject is always the
     followed side's decision about a follow made *of* that actor).
  4. The follower must be a **remote** actor (else **403** Forbidden — a local follow is an `Undo`, not a
     `Reject`). The follow must be known to this instance — the provisional edge is recorded **or** the
     `Reject` is already recorded for this exact follow — else **410 Gone** (a follow that was never
     recorded is unknown to the instance).
  5. Build + record the deterministic `Reject` (`FollowIris.BuildReject`, actor = this local actor,
     object = the original follow by IRI, IRI = `{localActor}/rejects/{followId}`) in the activity store
     + the local actor's outbox (so it is inspectable and a re-reject is a no-op).
  6. Remove the provisional local follow edge (follower → local actor; a no-op if already removed) and
     **server-deliver the `Reject` to the follower's inbox** (signed as the local actor) so the remote
     follower's `RejectActivityHandler` removes its own edge.
  7. **202 Accepted** (rejected + delivery scheduled).
- **Idempotency:** a re-reject of an already-rejected follow is **202** (the `Reject` is stored under the
  same deterministic IRI — a no-op — and the delivery is re-scheduled; the remote dedupes on the IRI). A
  follow that was *never* recorded is **410 Gone**.

## Request-body read (a small robustness note)

The handler reads the request body via a **buffered copy** (`ReadAsBufferedStringAsync` — `CopyToAsync`
into a `MemoryStream`), not by seeking the request stream. The request stream is not guaranteed seekable
(in TestHost it throws `NotSupportedException` on `Position = 0`), so the buffered copy is the portable
approach.

## Test harness additions

- `TestSeeder.SeedManuallyApprovingPersonWithKey(persistence, host, handle)` — seeds a
  `manuallyApprovesFollowers` actor **with an RSA-2048 key** (the pre-existing
  `SeedManuallyApprovingPerson` has no key, so a Reject signed by that actor could not be delivered +
  verified over the wire). Returns `(Key, ActorIri, KeyId)`.

## Tests

- **`OperatorRejectEndpointIntegrationTests`** (new, single instance, b.domain.local hosts a
  manually-approving `bob` + a second local actor `carol`; a remote `alice` follows bob):
  - `Reject_Authenticated_RemoteFollow_IsAcceptedAndRemovesEdge` — 202; the `Reject` is recorded in the
    activity store + bob's outbox under its deterministic IRI; the provisional edge is removed.
  - `Reject_Unauthenticated_Is401AndRecordsNothing` — 401; nothing recorded; the edge survives.
  - `Reject_FollowTargetIsNotThisActor_Is409` — rejecting alice's follow of *carol* on bob's endpoint → 409.
  - `Reject_LocalFollower_Is403` — rejecting a *local* (carol) follow of bob → 403 (a local un-follow is
    an `Undo`, not a `Reject`); the edge is left untouched.
  - `Reject_NotRecordedLocally_Is410` — a follow with no provisional edge → 410; nothing recorded.
  - `Reject_AlreadyRejected_IsIdempotent` — a re-reject of an already-rejected follow → 202 (no new
    activity, edge stays removed).
  - `Reject_NotAFollow_Is400` — a body that is not a `Follow` (a `Note`) → 400.
- **`FederationSignatureIntegrationTests.ManuallyApprovingActor_OperatorRejectsFollow_...`** (new, two
  instances): alice follows bob (bob is manually-approving, so **no auto-Accept** is scheduled); the
  operator rejects the follow via the Basic-auth endpoint; the `Reject` is recorded in B's store + bob's
  outbox, the provisional edge is removed on B immediately, and B's `DeliveryWorker` delivers the
  `Reject` back to alice's inbox over the wire **signed as bob** — A validates bob's signature (fetching
  B's actor doc) and A's `RejectActivityHandler` removes alice → bob from A's follow store. This is the
  end-to-end proof the operator reject federates back to the follower.

## The gap-register refresh

`docs/reference/RISK_GAP_REGISTER.md` was updated to the verified implementation status (confirmed by
reading the code, not the docs):

- **G-1 (outbound `Create`) — MITIGATED.** The inbound `Create` → `CreateActivityHandler` server-delivers
  a signed `Create` to every remote, non-blocked follower's inbox (+ relay fan-out). *Residual:* the
  **outbox-publish** path (`POST /u/{handle}/outbox`) fans out to only the *first* remote follower
  (`RecordCreateLocalAsync` returns one recipient). The headline ("Iris is receiving-only") no longer
  holds.
- **G-2 (outbound `Reject`/`Undo`) — MITIGATED.** `Undo` (un-follow) is server-delivered via the
  outbox-publish pipeline; `Reject` is now live via this slice's endpoint (the gate's operator path).
  *Residual:* `FollowIris.BuildUndo` itself has no production caller (the Undo is client-constructed and
  re-delivered — functionally equivalent); a local community cannot un-follow (see G-3).
- **G-3 (outbound community-follow) — still OPEN (the largest remaining capability gap).** A local
  community can *receive* follows (inbound records the community's follow sets + auto-`Accept`), but a
  local community cannot *initiate* a `Follow` of a remote `Group` — the only outbound `Follow`
  construction is the client's `FollowAsync` (a local person), and there is no `POST /c/{name}/outbox`
  write surface. → Phase 12.
- **G-4 (global search/directory) — MITIGATED.** `GET /ap/v1/search` (`GlobalSearchHandler` →
  `GlobalSearchService`) searches local actors (a directory) + stored content objects, paged.
  Instance-local scope.
- **G-5 (Ed25519/EdDSA) — MITIGATED.** `RemoteInboundKeyResolver` classifies OKP/Ed25519 keys;
  `HttpSignatureVerifier.VerifyWithKey` → `Ed25519Key.Verify` (BouncyCastle) verifies them.
- **G-6 (person-inbox `Create`) — already RESOLVED** (Slice 11.6, J-8).
- **H-4 (live-test residue) — note updated.** The `Undo` path is now live (a live follow can be undone by
  the local instance); the remaining residue is the outbound `Create` (a post is not withdrawn — no
  outbound `Delete` of a federated post yet).
- The summary + the "G-1 is the headline" note were updated to reflect the above (G-3 is now the largest
  remaining gap).

## Why this is the right slice

The manually-approves-followers gate (Slice 11.10) made auto-accept suppressible, but left the operator
with **no way to say no** — only to do nothing (the follow stays pending forever). A private/manual
account that wants to *curate* its followers cannot reject a request without this path. It is the direct,
minimal close of J-10/G-2's Reject half, and it reuses existing seams (the Basic-auth credential validator,
`FollowIris.BuildReject`, the delivery pipeline, the inbound `RejectActivityHandler`) rather than adding
new infrastructure. Refreshing the gap register at the same time keeps the "what is actually implemented"
source of truth honest — it had drifted behind the code.

## Verification

- `dotnet build Iris.slnx -c Release` → **0 warnings, 0 errors**.
- `dotnet test Iris.slnx -c Release` → **995 passed, 0 failed** (was 887 at the last recorded checkpoint,
  change 083; this slice adds **8** tests — seven single-instance endpoint tests + one two-instance
  federation-loop test. The 887→995 span also includes Phase 12's 12.7–12.22 conformance work, which
  landed without an interim checkpoint commit).
- The pre-existing `Follow_ThenReject_FullFederationLoop_...` (the inbound `RejectActivityHandler`
  proof) and all Slice 11.10 `manuallyApprovesFollowers` tests remain green.

## Decisions

- **Local endpoint, not a signed inbox delivery.** A reject is the *followed* side's local moderation
  decision (like a mute/block), so it is a Basic-authenticated request to the actor's own instance — not
  a signed activity posted to an inbox. This matches the mute/relay pattern and keeps the acting actor
  identity first-class (the owner-only seam).
- **The body is the original `Follow`.** Posting the follow (rather than just its IRI) lets the handler
  validate the follow's target + follower before building the `Reject`, and keeps the endpoint
  self-contained (no store lookup of the follow is required to build the `Reject`).
- **410 distinguishes "unknown" from "already rejected."** A re-reject of an already-rejected follow is
  202 (idempotent); a follow that was never recorded is 410 Gone. This makes the endpoint safe to retry
  while still signaling a genuinely unknown follow.
- **The `Reject` is recorded before delivery.** Recording it in the activity store + outbox first makes a
  re-reject detectable (idempotency) and inspectable, and matches how the server records its own
  outbound activities (outbox is the write surface).
