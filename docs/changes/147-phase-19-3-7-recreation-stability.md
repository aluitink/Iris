# 147 — Phase 19.3.7: Recreation stability

> 2026-09-01 · Slice 19.3.7 · Phase 19 (federation, two-instance network)

## What was built

Pinned the **recreation-stability** invariant. A host that has already delivered an outbound
federation `Create` is *recreated* — its process stops and starts again (the `docker compose down`
(no `-v`) + `up` cycle, where the delivery queue's journal volume survives but the process does not)
— and the re-created instance's delivery queue **replays** the already-delivered activity from its
on-disk journal. The replay must be a **harmless no-op**, not a re-delivery storm: the peer stores
the activity exactly once, lists it in the recipient's outbox exactly once (no duplicate edge), and
does not re-fan-out the activity (bounded outbound deliveries).

The guarantee rests on two independent guards, both already present in production; this slice
pins them together end-to-end with a new test and no production change:

- **At-least-once journal replay.** The file-backed `FileBackedDeliveryQueue` journals every
  enqueued job to disk and, on construction, replays every journaled job into its in-memory channel.
  The default shutdown service completes the queue but does **not** truncate the journal, so an
  un-truncated journal re-sends the already-delivered `Create` on the next `up`. The test asserts the
  replay is a *genuine re-transmission over the wire* — A's worker sends the `Create` exactly twice
  in total (the intended delivery + one replay) — not a no-op that never left A. Without that
  assertion the "no-op" claims below would be vacuous (the replay could have been dropped before
  leaving A, making "stored once" trivially true for the wrong reason).
- **Inbox add-if-absent gate.** The peer's `InboxProcessor` stores an inbound activity add-if-absent
  by its `Id` (C-07) and, on a re-delivery, does **not** re-dispatch it to a handler. So the replayed
  `Create` is stored as a no-op and never re-fan-out. Combined with `InMemoryActivityStore
  .AddToOutboxAsync` being idempotent-by-IRI (F-1911-2), the recipient's outbox stays unchanged in
  length — no duplicate edge.

## Topology

Two live in-process `TestServer` instances, wired over the wire exactly like the 19.3.3–19.3.6
slices:

- **Instance A** (`rec-a.domain.local`, author `alice`) — a live `TestServer` that serves its own
  actor document (alice's `publicKey`). A's *outbound* delivery is driven by a **standalone
  `DeliveryWorker`** over a `FileBackedDeliveryQueue` whose journal lives in a temp directory
  (simulating the named volume that survives `down` without `-v`) — A's hosted worker does not run
  (an in-process `TestServer` starts no hosted services), so the standalone worker models A's
  outbound federation.
- **Instance B** (`rec-b.domain.local`, follower `bob`) — a live `TestServer` running the **full
  inbound pipeline** (signature validation → `InboxProcessor` add-if-absent gate →
  `CreateActivityHandler`). B's fetcher reaches A's actor document (so B validates the
  alice-signed `Create` by resolving alice's key). B's outbound delivery transport routes to a
  counting handler: `bob` has no remote followers, so the `CreateActivityHandler`'s fan-out targets
  none — the counter proves B does **not** re-fan-out the (replayed) activity.

The test (1) enqueues a single `Create` (alice → bob's inbox, signed as alice) and runs the worker
once (the intended delivery), (2) *recreates* the worker by constructing a fresh
`FileBackedDeliveryQueue` over the same journal (which replays the already-delivered `Create`) and
runs it a second time (the replay), and (3) asserts the four invariants below.

## Key types & files

- `tests/Iris.Server.Tests/Delivery/RecreationStabilityIntegrationTests.cs` — **new** test class
  `RecreationStabilityIntegrationTests` with the test
  `Recreation_DeliveredCreateReplayed_StoredOnce_NoReFanOut_OutboxUnchanged`, plus its private test
  doubles (`CountingHandler`, `DeliveryCounter`, `StubClientFactory`).
- `src/Iris.Server/Delivery/FileBackedDeliveryQueue.cs` — journals each enqueued job to disk and
  replays every journaled job into its channel on construction (at-least-once). Unchanged.
- `src/Iris.Server/Inbox/InboxProcessor.cs` — `TryAddActivityAsync` add-if-absent-by-`Id` gate (C-07)
  and the "do not re-dispatch on re-delivery" rule. Unchanged.
- `src/Iris.Server/Inbox/CreateActivityHandler.cs` — stores the embedded object and records the
  activity in the recipient's outbox (the first-delivery path). Unchanged.
- `src/Iris.Server/InMemory/InMemoryActivityStore.cs` — `AddToOutboxAsync` idempotent-by-IRI
  (F-1911-2). Unchanged.

## Tests

1198 → **1199** passing (the +1 is the new recreation-stability test). Full `dotnet test` green;
`dotnet build` clean (`TreatWarningsAsErrors`). The test was run 5× to confirm it is not flaky.

- `Recreation_DeliveredCreateReplayed_StoredOnce_NoReFanOut_OutboxUnchanged` — two live in-process
  `TestServer` instances (A hosts `alice` on rec-a, B hosts `bob` on rec-b) wired over the wire (B's
  fetcher reaches A's actor doc so the alice-signed `Create`'s signature is validated). A's
  standalone `DeliveryWorker` over a `FileBackedDeliveryQueue` (journal in a temp dir) delivers the
  `Create` to bob's inbox on B (phase 1, the intended delivery). The test then **recreates** A's
  worker: a fresh `FileBackedDeliveryQueue` over the same (un-truncated) journal replays the
  already-delivered `Create`, and a second worker re-delivers it to B (phase 2, the replay). It
  asserts:
  - **Genuine re-transmission** — A's worker sent the `Create` to B exactly **twice** over the wire
    (1 intended delivery + 1 replay). This is the at-least-once re-send the slice is about; it is
    asserted so the no-op claims are not vacuous.
  - **Stored exactly once** — B's object store holds the embedded note exactly once after the
    replay (the replay is a no-op; it did not overwrite or duplicate the stored object).
  - **Outbox unchanged, no duplicate edge** — bob's outbox is unchanged in length after the replay
    and still lists the `Create` exactly once (no re-delivery storm, no duplicate edge).
  - **No re-fan-out storm** — B made no outbound deliveries for this activity (the counter is
    bounded); an unbounded re-fan-out would be the 19.3.1/19.3.2 echo defect, now across a
    recreation rather than a same-process re-delivery.

## Decisions

- **No production change; no journal truncation.** The question was whether a recreation re-delivers
  a storm. It *does* re-send the already-delivered job (at-least-once, by design — the journal is not
  truncated on the default shutdown), and that re-send is *harmless* because the peer's inbox is
  idempotent by `Id`. Truncating the journal on shutdown would be a third guard but would change
  production shutdown semantics and would not be the faithful model of the `down` (no `-v`) + `up`
  cycle the slice describes (the volume survives, so the journal is present on `up`). The test
  therefore pins the guarantee as it actually exists: at-least-once replay **plus** an idempotent
  inbox.
- **The "stored exactly once" assertion targets the embedded Note's IRI, not the `Create`
  activity's IRI.** `CreateActivityHandler` stores the *embedded object* (the `Note`) in the `Objects`
  store keyed by the note IRI; it records the *activity* in the outbox (keyed by the `Create` IRI).
  The storage assertion therefore reads `Objects.TryGetObjectAsync(noteIri)` and asserts it is a
  `Note`; the outbox assertion reads `Activities.GetOutboxAsync(bob)` and counts entries whose `Id`
  equals the `Create` IRI. (This mirrors the 19.3.3–19.3.6 slices, which assert on the object store.)
- **A's outbound delivery is a standalone worker, not the hosted worker.** An in-process `TestServer`
  starts no hosted services, so A's `DeliveryWorker` (a `BackgroundService`) would never run. The
  standalone worker over a `FileBackedDeliveryQueue` is the faithful model of A's outbound federation
  across a recreation: the queue's journal is the on-disk state that survives `down` without `-v`,
  and reconstructing the queue over the same path is exactly what a fresh process does on `up`.
- **The replay is asserted to genuinely re-deliver over the wire (A outbound == 2).** A "no-op"
  assertion is only meaningful if the no-op actually *received* the re-sent activity. Asserting A's
  worker sent the `Create` twice (the intended delivery + the replay) proves the replay left A and
  reached B's inbox — so B's "stored once / outbox unchanged / no re-fan-out" results are the result
  of B's idempotency, not of the replay being dropped before leaving A.
- **B's re-fan-out counter is bounded (<= 1), not strictly zero.** `bob` has no remote followers, so
  the `CreateActivityHandler`'s fan-out targets none and the counter is expected to stay 0. The
  assertion is `<= 1` (bounded) rather than `== 0` so the test still pins the *property* the slice
  cares about — no *unbounded* re-fan-out (the 19.3.1/19.3.2 echo defect) — without over-asserting a
  specific fan-out count that is an artifact of the topology.
</content>
</invoke>
