# 151 — Operator follow-accept/reject: the live outbound half of `manuallyApprovesFollowers`

> 2026-09-01 · Slice 19.1.2 (follow scenarios, J-10) · Resolved Decision #46 (the Accept/Reject half)

## What was built

`manuallyApprovesFollowers` (Resolved Decision #46) already suppressed the auto-`Accept` when an
inbound follow arrives at a manually-approving local person: `FollowActivityHandler` records the
provisional follow edge and returns *without* scheduling an `Accept`. **But there was no way to
complete the lifecycle** — the operator could not Accept (finalize the edge) or Reject (remove it), and
the remote follower had no inbound `Accept`/`Reject` to react to. The reject-only endpoint that
existed (`LocalFollowRejectHandler`) read the original `Follow` from the request *body* and had no
accept half.

This slice closes the gap and proves the end-to-end "the operator decides, the remote side reacts"
behavior:

- **A single operator follow-decision endpoint (the 19.1.2 / J-10 live half).** One catch-all route —
  `POST /ap/v1/u/{handle}/follows/{**followId}` — replaces the old reject-only handler. The follow
  being decided on is the catch-all route value (the absolute IRI of the original `Follow`, fetched
  from the local activity store — an inbound follow is always stored there), and an optional trailing
  `/accept` selects acceptance (otherwise it is a rejection). Both are **body-less**. The endpoint is
  Basic-authenticated (the acting actor's credentials, the same owner-only seam as the mute/relay
  endpoints). For an **accept** it builds the deterministic `Accept` (`FollowIris.BuildAccept`), ensures
  the follow edge, and server-delivers it to the follower's inbox (so the remote finalizes its edge).
  For a **reject** it builds the deterministic `Reject` (`FollowIris.BuildReject`), removes the
  provisional edge, and server-delivers it (so the remote removes its edge). Both record the activity
  in the local activity store + the local actor's outbox (inspectable + idempotent).
- **The client surface.** `IActivityPubClient`/`ActivityPubClient` gain `AcceptFollowAsync` +
  `RejectFollowAsync` (each with a `ProxyCredentials` overload), implemented by a shared private
  `LocalFollowDecisionAsync` (a Basic-authenticated POST to the acting actor's own instance — a local
  decision, not a signed inbox delivery; the instance builds and delivers the `Accept`/`Reject`).
- **The sample UI "Inbound follows" card.** The sample server's primary actor can be seeded with
  `manuallyApprovesFollowers` (`Iris__ManuallyApprovesFollowers=true`), and the actor-detail page now
  lists inbound follows (from the followed actor's outbox) with an Accept / Reject button each.
- **Inbound follows are surfaced in the followed actor's outbox.** `FollowActivityHandler` (person
  path) now records the inbound follow in the *followed* actor's outbox, so a UI can enumerate it from
  the outbox (the activity store alone is not enumerable by a client) and offer the operator the
  decision.

## Key types & files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — the old `LocalFollowRejectHandler` (body-based)
  and the dead `LocalFollowAcceptHandler` are replaced by a single `LocalFollowDecisionHandler`
  (catch-all, `/accept`-suffix dispatch, follow fetched from the activity store, Basic-auth, builds +
  records + delivers the deterministic `Accept`/`Reject`, ensures/removes the edge). The route comment +
  registration (`POST /u/{handle}/follows/{**followId}`, `local-follow-decision-endpoint`) are updated.
- `src/Iris.Server/Inbox/FollowActivityHandler.cs` — the person path now adds the inbound follow to the
  followed actor's outbox (`AddToOutboxAsync(RecipientIri, follow)`) before the manual-approve early
  return, so the UI can list it.
- `src/Iris.Client/IActivityPubClient.cs` — adds `AcceptFollowAsync` + `RejectFollowAsync` (4 methods,
  each with a `ProxyCredentials` overload).
- `src/Iris.Client/ActivityPubClient.cs` — adds the impls + a private `LocalFollowDecisionAsync`
  (Basic-auth local decision; a trailing `/accept` selects acceptance; body-less for both).
- `samples/SampleServer/Program.cs` — opt-in `Iris__ManuallyApprovesFollowers=true` seeds the primary
  actor (alice) with the `manuallyApprovesFollowers` extension (re-persisted after `EnsureActor`), so
  the sample's accept/reject flow is meaningful.
- `samples/SampleBlazorClient/Pages/ActorDetail.razor` — the "Inbound follows" card: reads the followed
  actor's outbox for `Follow` activities and offers Accept / Reject buttons (Basic-auth, as the loaded
  actor).
- `docker-compose.yml` — iris-a sets `Iris__ManuallyApprovesFollowers: "true"` (the gate is active for
  the live two-instance manual test).
- `tests/Iris.Server.Tests/OperatorFollowDecisionEndpointIntegrationTests.cs` — the old
  `OperatorRejectEndpointIntegrationTests` (7 reject tests) is renamed + extended with 5 accept tests
  (the `/accept` half: 202 + `Accept` + edge ensured; idempotent re-accept; 401; 409 wrong target; 403
  local; 410 unknown) and the reject tests are re-pointed at the body-less endpoint (12 tests total).
- `tests/Iris.Server.Tests/Inbox/FollowActivityHandlerTests.cs` — adds
  `HandleAsync_LocalPerson_InboundFollowLandsInFollowedActorsOutbox` (the inbound follow is surfaced in
  the followed actor's outbox, both the manual-approve and auto-approve paths).

## Tests

1211 → **1217** passing (+6: 5 new accept-path integration tests + 1 outbox-surfacing unit test). Full
`dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`).

- `Accept_Authenticated_RemoteFollow_IsAcceptedAndRecordsEdgeAndAccept` — a recorded inbound follow is
  accepted via the `/accept` half: `202`, the deterministic `Accept` is in the activity store + the
  local actor's outbox, and the follow edge (follower → local actor) is recorded.
- `Accept_AlreadyAccepted_IsIdempotent` — a re-accept of an already-accepted follow is `202` (the edge
  is a no-op; the activity is stored under the same deterministic IRI).
- `Accept_Unauthenticated_Is401`, `Accept_FollowTargetIsNotThisActor_Is409`,
  `Accept_LocalFollower_Is403`, `Accept_NotRecordedLocally_Is410` — the guard matrix mirrors the reject
  path.
- `HandleAsync_LocalPerson_InboundFollowLandsInFollowedActorsOutbox` — an inbound follow of a local
  person is recorded in the *followed* actor's outbox under its own IRI (both the manual-approve and
  the auto-approve paths), so the UI can enumerate it from the outbox.

## Live manual test (two-instance Docker, via the Sample UI flow)

The two-instance env (`docker compose up --build -d`, fresh volumes) was exercised end-to-end over
genuine network I/O. iris-a's alice was gated (`Iris__ManuallyApprovesFollowers=true`); a signed
`Follow` from iris-b's alice → iris-a's alice was driven (IrisSigner, ServerToServer profile):

- The follow landed in iris-a's activity store **and** iris-a's alice outbox (the UI's "Inbound
  follows" source); the provisional edge was recorded; **no `Accept` was auto-scheduled** (the gate).
- **Accept** (Basic `alice:iris-sample`, `POST …/follows/{followIri}/accept`, no body) → `202`; the
  deterministic `Accept` was recorded in the outbox + activity store, the edge was confirmed, and
  iris-b **finalized its edge** (its `following` now lists iris-a's alice) — proving the server
  delivered a signed `Accept` the remote consumed.
- **Reject** (Basic, `POST …/follows/{followIri}`, no body) → `202`; the edge was removed on iris-a,
  the `Reject` was recorded + delivered, and iris-b **removed its edge** (its `following` no longer
  lists iris-a's alice) — proving the remote consumed the signed `Reject`.
- Unauthenticated decision → `401`.

A 60-second outbox page cache (`LocalCollectionPageCache`) means a just-added `Accept`/`Reject` appears
in the outbox collection within ~1 minute; the decision's `202` + the remote-side edge change are
immediate.

## Decisions

- **One catch-all endpoint, not two routes.** Accept and reject are the same decision (the followed
  side's response to an inbound follow) with a different outcome, so they share one handler + route.
  A trailing `/accept` selects acceptance because a catch-all (`{**followId}`) can only be the final
  route segment (ASP0017) — the follow's absolute IRI contains `/`, so the selector is a suffix, not a
  sibling segment. This also matches the client's URL shape
  (`{actorId}/follows/{followIri}` vs `{actorId}/follows/{followIri}/accept`).
- **Body-less for both, follow fetched from the store.** An inbound follow is *always* stored in the
  local activity store (the `InboxProcessor` stores it before dispatching), so the handler can resolve
  it by IRI from the store. The old reject-only endpoint read the follow from the request *body*, which
  forced the client to re-serialize the follow (and made accept/reject asymmetric). Fetching from the
  store makes both decisions identical in shape (just a different path suffix) and removes the body
  round-trip.
- **Local, Basic-authenticated decision (not a signed inbox delivery).** The decision is made *as* the
  followed actor on its own instance — the same owner-only, Basic-authenticated seam as the mute/relay
  endpoints. The instance (not the client) builds + signs + delivers the `Accept`/`Reject` to the
  follower's inbox, so the remote verifies it against the followed actor's key. This is the delivery
  model in 19.6.3 (the client posts, the server delivers).
- **Inbound follows are surfaced in the followed actor's outbox.** The activity store is not enumerable
  by a client, so the UI (and any client) lists a person's inbound follows from that person's *outbox*.
  `FollowActivityHandler` now adds the inbound follow to the followed actor's outbox. This is a
  deliberate (and small) extension of "the outbox is what the actor authored" — an inbound follow of an
  actor is surfaced in that actor's outbox because it is an activity *about* that actor that the actor
  must act on (accept/reject), not one the actor authored. The auto-accept path is unaffected (the
  `Accept` is also in the outbox; the follow's presence there is harmless and idempotent).
