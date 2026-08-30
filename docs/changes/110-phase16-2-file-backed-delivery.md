# 110 — Phase 16.2: Persistent file-backed delivery queue + dead-letter store

> 2026-08-30 · Phase 16.2 (production persistence & scaling) · `Iris.Server`

## What was built

The default `InMemoryDeliveryQueue` and `InMemoryDeliveryDeadLetterStore` are **ephemeral**: a host
restart loses every pending outbound delivery (a follow scheduled to be delivered, a boost, a relay
fan-out) and the entire dead-letter history (which deliveries permanently failed). A production
federation instance cannot afford to silently drop the edge it was about to deliver — or to lose the
operator's record of permanently-failed deliveries.

Phase 16.2 adds two **persistent, file-backed** implementations of the existing
`IDeliveryQueue` / `IDeliveryDeadLetterStore` seams, opt-in via a single extension method. Pending
deliveries (and dead-lettered deliveries) are journaled to disk (one JSON object per line) and
replayed on startup, so a restart does not drop the federation edge.

## Key types

- **`FileBackedDeliveryQueue`** (`src/Iris.Server/Delivery/FileBackedDeliveryQueue.cs`) — **new**.
  Implements `IDeliveryQueue` + `IAsyncDisposable`. Journals each enqueued job to disk (append + flush)
  **before** the in-memory channel write (at-least-once); replays the journal into the bounded
  `Channel<DeliveryJob>` at construction; `TruncateAsync` atomically rewrites the journal (temp file +
  `File.Move`) to only the still-pending jobs (call on a clean shutdown to bound the file). Malformed
  lines (a torn write) are skipped on replay.
- **`FileBackedDeliveryDeadLetterStore`** (`src/Iris.Server/Delivery/FileBackedDeliveryDeadLetterStore.cs`)
  — **new**. Implements `IDeliveryDeadLetterStore`. Journals each `DeadLetterEntry` to disk and holds an
  in-memory bounded, newest-first view; restores from the journal at construction.
- **`ActivityPubServerExtensions.UseFileBackedDelivery`** (`src/Iris.Server/ActivityPubServerExtensions.cs`)
  — **new** opt-in extension. Rebinds `IDeliveryQueue` / `IDeliveryDeadLetterStore` to the file-backed
  types. The default registration (`AddActivityPubServer`) is unchanged (in-memory), so existing hosts
  are unaffected.

## How it works

- **Durability model — at-least-once.** A job is journaled to disk (and flushed) **before** it is
  written to the channel. A crash after the flush leaves the job on disk for replay; a crash before the
  flush loses the job (the same window as the in-memory queue). A job that was in fact already delivered
  is re-delivered on replay and **deduped by its `Id`** (C-07) — a harmless no-op. The in-memory channel
  provides the same back-pressure and bounded capacity as `InMemoryDeliveryQueue`.
- **Replay and truncation.** On construction, the journal is read line-by-line, each line parsed into a
  `DeliveryJob`, and the jobs are written into the channel. A job that the previous process already
  dequeued is harmless (deduped). `TruncateAsync` rewrites the journal to contain only the jobs still
  pending, so the file does not grow without bound across many restarts.
- **Polymorphic `Activity` round-trip.** The `Activity` (a polymorphic ActivityStreams type) is
  serialized via `ActivityJson.Serialize` and read back through `IObjectOrLink` — **not** the base
  `Activity` type — so the `type` discriminator dispatches to the concrete CLR type (`Create`,
  `Follow`, …). Deserializing into the base `Activity` would return a plain `Activity` and lose the
  concrete type. The `Iri` value type (not directly JSON-serializable) round-trips via two custom
  converters (one for `Iri`, one for `Iri?`, since `Iri` is a readonly struct and the record's
  `ActorIri` property is nullable).

## Tests

9 new tests in `FileBackedDeliveryPersistenceTests` (a "restart" is simulated by disposing one queue /
store and constructing a fresh one over the same journal path — exactly what happens when a host process
stops and starts):

- **Queue:** pending jobs survive a restart and are replayed (3 enqueued → 3 replayed, all drained).
- **Queue:** a dequeued (delivered) job is NOT re-delivered after a `TruncateAsync` (3 enqueued, 2
  delivered, truncate → only the 1 pending is replayed).
- **Queue:** `EnqueueAsync` writes the journal file to disk (one line per job).
- **Queue:** `CompleteAsync` + empty → `TryDequeueAsync` returns null (drain).
- **Dead-letter:** entries survive a restart and are restored (newest-first, attempts preserved).
- **Dead-letter:** the `Activity` round-trips, preserving its polymorphic type (`Create`).
- **Dead-letter:** restore applies the capacity bound (evicts the oldest).
- **DI:** `UseFileBackedDelivery` swaps the in-memory defaults for the file-backed types (and the journal
  paths are honored).
- **DI:** `AddActivityPubServer` still defaults to the in-memory types (the file-backed swap is opt-in).

Test count: 1038 → 1047 total. Full suite green; all 606 existing `Iris.Server.Tests` (and the rest of the
suite) still pass unchanged.

## Decision: opt-in, not the default

The file-backed types require a journal **path**, which a generic `AddActivityPubServer` cannot know.
They are therefore behind an explicit `UseFileBackedDelivery(deliveryJournalPath, deadLetterJournalPath, …)`
call. The default registration stays in-memory, so every existing host and test is unaffected; a
production host opts in with a single line. This is a pure capability addition with no behavior change by
default. The journal file is append-only (not rotated) for now; a production host that wants a bounded
file would pair this with periodic rotation (out of scope here).

## Files changed

- `src/Iris.Server/Delivery/FileBackedDeliveryQueue.cs` — **new**
- `src/Iris.Server/Delivery/FileBackedDeliveryDeadLetterStore.cs` — **new**
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `UseFileBackedDelivery` opt-in extension
- `tests/Iris.Server.Tests/Delivery/FileBackedDeliveryPersistenceTests.cs` — **new** (9 tests)
- `tests/Iris.Server.Tests/Delivery/IriRoundTripIsolationTests.cs` — removed (covered by the persistence tests)
