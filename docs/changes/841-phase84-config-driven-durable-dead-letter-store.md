# 84.1 — Config-driven durable dead-letter store

**Commit:** `a1645e1`
**Status:** COMPLETE.

## The gap

Phase 83.3 surfaced the dead-letter queue (deliveries that exhausted their retry budget) via
`GET /ap/v1/dead-letters`, and Phase 16.2 already built a durable
`FileBackedDeliveryDeadLetterStore` (journaled to disk, survives a restart). But the durable store could
only be selected by calling `UseFileBackedDelivery` — which **also forces the file-backed delivery queue**
and requires an explicit code call. There was no **config-driven** way to select a durable dead-letter
store **independently of the queue**, mirroring how `Iris:ConnectionString` config-drives persistence.

## What was built

A single config key: **`Iris:Delivery:DeadLetterJournalPath`**.

In `AddActivityPubServer(IServiceCollection, IConfiguration)`'s `Iris:Delivery` section handling, when the
key is set (and non-empty), the `IDeliveryDeadLetterStore` binding is rebound to a
`FileBackedDeliveryDeadLetterStore` over that path:

```csharp
if (deliverySection["DeadLetterJournalPath"] is { } deadLetterPath and not "")
{
    services.AddSingleton<IDeliveryDeadLetterStore>(_ => new FileBackedDeliveryDeadLetterStore(deadLetterPath));
}
```

### Design decisions

- **Independent of the delivery queue.** The key selects *only* the dead-letter store. The `IDeliveryQueue`
  stays its in-memory default unless a host separately calls `UseFileBackedDelivery`. A dead-letter store
  is an operational sink (an operator inspects + re-drives it); a durable *queue* is a separate
  durability concern (at-least-once delivery). Keeping them decoupled means an operator who only wants
  "I can see what failed to federate after a restart" does not have to also opt into a file-backed
  delivery queue (with its `TruncateAsync` clean-shutdown requirement).
- **Default stays in-memory.** No config key = current behavior, so every existing test and the bare
  `AddActivityPubServer()` host are unaffected. The durable store is an opt-in.
- **Registration order is respected.** This is a `services.AddSingleton` placed after the `TryAddSingleton`
  in-memory default in `AddActivityPubServer(IServiceCollection)`; a host that calls
  `UseFileBackedDelivery` (or its own `AddSingleton`) *after* still wins (DI first-registration-wins).
- **No new production code beyond the wiring.** The `FileBackedDeliveryDeadLetterStore` (its journaling,
  restore-on-construction, bounded newest-first view, eviction, and the `Iri`/`Activity` JSON converters)
  all existed since Phase 16.2; this slice only makes it **selectable from configuration**.

## Tests

`tests/Iris.Server.Tests/Delivery/DurableDeadLetterStoreTests.cs` — **5 new tests**:

- **`DeadLetteredDelivery_PersistsAndSurvivesRestart`:** "process 1" adds 3 dead letters (journaled);
  "process 2" (a fresh `FileBackedDeliveryDeadLetterStore` over the same path) restores the same 3
  entries (newest-first) + the round-tripped `Activity` is a real object (`Id` preserved).
- **`CapacityEviction_EvictsOldest_NewestFirst_Kept`:** with an in-memory bound of 5, adding 8 entries
  keeps only the newest 5 in memory (the file holds all 8 as the full log); a restart re-applies the same
  bound (the file's 8 lines → the in-memory view drops the oldest 3, keeps the newest 5).
- **`DeadLetterJournalPathConfig_SelectsFileBackedStore`:** a set key → the resolved
  `IDeliveryDeadLetterStore` is a `FileBackedDeliveryDeadLetterStore` with the configured `JournalPath`.
- **`NoDeadLetterJournalPath_KeepsInMemoryStore`:** no key → the resolved store is the
  `InMemoryDeliveryDeadLetterStore` default.
- **`DeadLetterJournalPathConfig_DoesNotChangeDeliveryQueueBinding`:** the key selects *only* the
  dead-letter store — the resolved `IDeliveryQueue` is still the `InMemoryDeliveryQueue` default.

## Suite impact

- `dotnet build -c Release` — 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test -c Release --no-build --filter "Category!=Slow"` — **1625 passed, 0 failed, 1 skipped**
  (Iris.Server.Tests 1000 → 1005). The new 5 tests pass in isolation + on a clean full-suite run; the
  occasional `Iris.Server.Tests` failure under heavy parallel full-suite load is the known federation
  timing/contention flake (passes 1005/1005 in isolation).
