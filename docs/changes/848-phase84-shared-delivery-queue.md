# 84.6 (part 2) — Shared delivery queue with a cross-process consumer claim

**Phase 84.6, shared-state scale-out — the shared delivery queue.** Commit `5d50278`.

## Problem

Parts 1 + 1b made the **key binding** converge across instances (a rotation on A is visible to B's signer
automatically). But the **delivery queue** was still per-instance — the most dangerous remaining
divergence, because it **silently drops deliveries**:

- The default `InMemoryDeliveryQueue` is a per-process `Channel<T>`. A delivery enqueued on A is in
  **A's** channel; B's worker pumps **B's** (empty) channel. The delivery is never sent.
- The restart-durable `FileBackedDeliveryQueue` (Phase 16.2) journals to disk, but **each instance
  replays the journal into its own in-memory channel at construction** and dequeues from that private
  channel. Two instances over one origin therefore (a) each replay the same journal into their own
  channels (double-delivery), and (b) a delivery enqueued on A **after** B started is never visible to B
  (B's channel was populated at B's startup, before A's enqueue).

The 84.5 single-instance guard made this constraint explicit (a second instance on the same persistence
fails fast). This slice is the scale-out half for the delivery queue: a shared queue two instances can
coexist over, so **a delivery scheduled on A is delivered by A-or-B — not dropped**.

## What was built

**`SharedDeliveryQueue`** (`Iris.Server/Delivery`, new) — a shared, file-backed `IDeliveryQueue`:

- A **single JSON journal** (one record per line) that all instances over the origin share, guarded by a
  **cross-process file lock** (a separate `<journal>.lock` file opened with `FileShare.None`; every
  read-modify-write — an enqueue, a claim, a purge — holds it, so two instances never interleave a
  journal edit). The lock is acquired by opening the file exclusively; on contention the open is retried
  on a short interval (10 ms default) until acquired or the caller's `ct` cancels.
- **Model: a visibility-timeout claim protocol** (the Amazon SQS `ReceiveMessage` pattern). Each journaled
  `DeliveryQueueRecord` is a `DeliveryJob` plus:
  - `Seq` — a monotonically increasing queue position (assigned at enqueue, under the lock);
  - `Status` — `Pending` or `Claimed`;
  - a claim stamp — `ClaimedBy` (the claiming instance's `Guid`) + `ClaimedAt` (`DateTimeOffset` UTC).
  - `EnqueueAsync` appends a `Pending` record with the next `Seq`.
  - `TryDequeueAsync` **purges** records claimed past the **drop horizon** (24 h default — re-delivery is
    no longer useful, so the journal stays bounded), then **claims** the lowest-`Seq` record that is
    `Pending` or `Claimed` **past its visibility timeout** (10 min default — reclaimable), stamping this
    instance's id + the current time, and returns the job. The record stays in the journal (the claim is a
    *visibility* change, not a removal).
- **Delivery guarantee: at-least-once** (the same as `FileBackedDeliveryQueue`). A job is journaled +
  flushed before it is handed to a consumer. A consumer that claims a job and crashes (or is slow past the
  visibility timeout) leaves the job reclaimable — another instance re-claims and re-delivers it. The
  receiving instance dedupes a re-delivered activity by its `Id` (C-07), so a re-delivery (a job delivered
  successfully and then re-claimed when its visibility window lapses) is a harmless no-op. The worker's own
  F-22 retry / dead-letter policy (Phase 17.3) handles per-delivery failures; the queue only gets the job
  to a worker at least once.

**`UseSharedDelivery`** (`ActivityPubServerExtensions`, new) — registers the shared queue + a shared
`FileBackedDeliveryDeadLetterStore` (so an exhausted delivery on A is visible to an operator inspecting
B). The scale-out counterpart to `UseFileBackedDelivery` (per-instance). A host that wants multi-instance
scale-out calls it after `AddActivityPubServer`; the default single-instance deployment (the
`InMemoryDeliveryQueue` default) is unaffected.

## Key design decisions (recorded)

1. **Visibility-timeout claim, not per-job ack.** The `IDeliveryQueue` interface has no per-job ack
   (`CompleteAsync` is the *queue*-completion signal, not a per-job ack). A per-job ack would require an
   interface change (affecting all impls + the worker). The visibility-timeout model (a claim is exclusive
   for `VisibilityTimeout`; a job becomes reclaimable when the window lapses) requires **no interface
   change** and is the standard production pattern (SQS, RabbitMQ's `ack`/requeue). The cost is that a
   successfully-delivered job is re-claimed (and re-delivered) once, when its visibility window lapses —
   which is **harmless** (C-07 dedupe). The `VisibilityTimeout` (10 min) is deliberately longer than the
   worst-case F-22 retry budget (~15 s of backoff + 5 network calls), so a slow-but-healthy delivery is
   not re-delivered while it is still in flight.
2. **`Count` is an exact read of the shared journal, not a per-instance cache.** A per-instance `Count`
   cache would be stale when another instance enqueues or claims (instance B's count would not reflect A's
   enqueue). `Count` instead reads the journal (under the lock, synchronously) so it reflects the true
   pending pool across all instances. The interface contract is "approximate," but the shared queue
   provides an exact read. It is a file read + lock acquisition, so it is not intended for a hot loop
   (observability only).
3. **Purge-before-claim.** A record past the drop horizon is *also* past the visibility timeout, so if the
   candidate selection ran before the purge it would be re-claimed (and returned) even though it should be
   purged. The dequeue **prunes first**, then selects the candidate from the pruned set — this is what
   makes "purged, not re-claimed" hold.

## Tests (7 new, `Iris.Server.Tests/Delivery/SharedDeliveryQueueScaleOutTests.cs`)

1. **`Job_EnqueuedOnInstanceA_IsDequeuedByInstanceB_SameJournal`** — the core cross-instance visibility:
   two `SharedDeliveryQueue` instances over the same journal; A enqueues, B dequeues (a per-instance queue
   would never let B see A's job).
2. **`Delivery_QueuedOnInstanceA_IsDelivered_ByInstanceB_Worker`** — the acceptance test: A's
   `DeliveryService` enqueues into the shared journal; **only B's `DeliveryWorker`** runs; B dequeues the
   job and POSTs the activity to the target inbox. Proves "a delivery queued on A is delivered (by A or B),
   not dropped."
3. **`ClaimedJob_IsNotReclaimed_WithinVisibilityWindow`** — while A holds a claim (within the window), B
   does NOT re-claim it (a live in-flight delivery is not double-delivered).
4. **`ClaimedJob_IsReclaimed_ByInstanceB_PastVisibilityWindow`** — a crashed/slow claimer's job is
   reclaimable past the visibility timeout (the at-least-once guarantee).
5. **`ClaimedJob_PastDropHorizon_IsPurged_NotReclaimed`** — a job past the drop horizon is purged (removed
   from the journal), not re-claimed (the journal is bounded).
6. **`Count_ReflectsPendingPool_AcrossInstances`** — `Count` reflects the shared pending pool across
   instances (an exact read, not a stale per-instance cache).
7. **`MalformedJournalLine_IsSkipped_NotFatal`** — a torn (malformed) journal line (a crash mid-write) is
   skipped; the queue still dequeues the valid record.

## Not yet done (84.6 remaining)

- **(c) a cache-invalidation channel** — so the in-memory actor/edge caches (the `RemoteActorCache` /
  actor-document cache) don't serve stale reads across instances (an actor updated on A is not served
  stale by B's cache).
- **Lift the 84.5 single-instance guard** once (c) lands (make it a warning, not a startup failure, or
  document multi-instance as supported when the shared key provider + shared delivery queue + cache
  invalidation are all configured).
