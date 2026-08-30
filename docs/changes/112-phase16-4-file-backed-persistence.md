# 112 — Phase 16.4: File-backed persistence provider

> 2026-08-30 · Phase 16.4 (production persistence & scaling) · `Iris.Server`

## What was built

Phase 16.1–16.3 made the *outbound delivery* pipeline production-grade (bounded concurrency, a
persistent file-backed delivery queue, per-peer rate limiting). This slice closes the last remaining
Phase 16 item: a **persistent `IPersistenceProvider`** so that a host's actors, objects, activities,
communities, follow/like/reply/relay/moderation edges, and its signing keys **survive a restart** —
the data is durable on disk, not lost with the process.

A host opts in with one call, mirroring the Phase 16.2 opt-in:

```csharp
services.AddActivityPubServer(...)
        .AddRouting()
        .AddInMemoryPersistence()        // default; still fine for dev
        .UseFileBackedPersistence("/var/lib/iris"); // opt-in; last registration wins
```

`UseFileBackedPersistence(directory)` registers a `FileBackedPersistenceProvider` (and its
`FileBackedKeyStore` as `IKeyStore`) over a directory, one JSON file per store. The default remains the
in-memory provider, so behavior is unchanged unless a host explicitly opts in.

## Key types & files

- **`FilePersistence`** (`src/Iris.Server/Persistance/FilePersistence.cs`) — **new** atomic
  read-modify-write primitive over a single file. A `SemaphoreSlim` serializes every read/write; the
  file is read, the caller's state-mutator runs, and the result is written **atomically** (write to a
  `.tmp` sibling, then `File.Move(overwrite: true)`). Exposes both async (`WithStateAsync`,
  `SnapshotAsync`) and synchronous (`WithState`, `Snapshot`) forms — the sync forms exist because
  `IKeyStore` is synchronous and `out`-param document reads cannot be `async`. `JsonOptions` registers
  only `Iri` / `Iri?` converters (an `Iri` is a `readonly record struct` wrapping a `Uri` and is not
  natively JSON-serializable). `IriEdge` / `DocumentEntry` are the small serialization records.
- **Nine `FileBacked*Store` implementations** (`src/Iris.Server/Persistance/Stores/`) — **new.**
  `FileBackedActorStore`, `FileBackedObjectStore`, `FileBackedActivityStore`, `FileBackedCommunityStore`
  (document stores) and `FileBackedFollowStore`, `FileBackedLikeStore`, `FileBackedReplyStore`,
  `FileBackedRelayStore`, `FileBackedModerationStore` (edge stores). Each owns one `FilePersistence`.
  All implement the corresponding `I*Store` interface. Each is `IDisposable` (releases the file lock;
  the on-disk data is left in place).
- **`FileBackedKeyStore`** (`src/Iris.Server/Persistance/Stores/FileBackedKeyStore.cs`) — **new.**
  The synchronous `IKeyStore`. Stores `StoredKey(Algorithm, KeyId, PrivateKeyPem)` as a JSON array of
  PEM blobs; reconstructs `Ed25519Key` / `KeyPair` from PEM on read. A corrupt PEM for one entry
  yields a key-not-present (`null`) rather than throwing, so a single bad line never poisons the whole
  key store.
- **`FileBackedPersistenceProvider`** (`src/Iris.Server/Persistance/FileBackedPersistenceProvider.cs`)
  — **new.** The aggregate `IPersistenceProvider`. The directory constructor creates one `FilePersistence`
  per store (`actors.json`, `objects.json`, `activities.json`, `communities.json`, `follows.json`,
  `likes.json`, `replies.json`, `relays.json`, `moderation.json`, `keys.json`); a second constructor
  takes pre-built stores for tests. `IDisposable` (disposes each store's file lock).
- **`ActivityPubServerExtensions`** — **new `UseFileBackedPersistence(IServiceCollection, string
  directory)`** (after `UseFileBackedDelivery`). Validates the directory exists (throws
  `DirectoryNotFoundException`), then registers `AddSingleton<IPersistenceProvider>` and
  `AddSingleton<IKeyStore>` — last registration wins over `AddInMemoryPersistence`.

## How it works

- **Documents round-trip through `ActivityJson`.** ActivityStreams documents (actors, notes, creates,
  groups) are serialized with `ActivityJson.Serialize(doc)` (a string) and read back via
  `ActivityJson.Deserialize<IObjectOrLink>(json)` then cast to the expected type. The store keeps the
  payload as a **JSON string** in `DocumentEntry.Json` so `System.Text.Json` never has to round-trip a
  polymorphic ActivityStreams object graph (only the `Iri`-typed fields use the registered converters).
- **Edges are `IriEdge { Source, Target }` lists.** The five edge stores (follow/like/reply/relay/
  moderation) serialize as JSON arrays of `{ "Source": "...", "Target": "..." }` objects. The moderation
  store holds three such arrays (`blocks` / `flags` / `mutes`) under a single file.
- **Atomic writes.** Every mutation is `WithStateAsync`: lock → read file into a `ConcurrentDictionary`
  state → mutate → serialize → write `.tmp` → `File.Move(overwrite)`. A crash mid-write leaves the prior
  good file intact (the `.tmp` is orphaned, never half-written over the real file).
- **Restart = new instance, same directory.** "Restart" in the tests is modeled by constructing a second
  `FileBackedPersistenceProvider` over the same directory and asserting the second instance sees exactly
  what the first wrote.
- **Missing file = empty store; corrupt file = empty store (no throw).** A store whose file is absent
  (first run) or whose file is unreadable JSON treats the state as empty and continues — a corrupted
  file degrades to "no data" rather than taking the host down.

## Tests

20 new tests in `FileBackedPersistenceTests` (`tests/Iris.Server.Tests/Persistance/`):

- **Per-store restart survival** (one test each): actor, follow, like, reply, moderation, relay, object,
  activity, community — write through a first provider, construct a second over the same directory,
  assert the second sees the same data (and that a removed/absent entry stays absent).
- **Key store:** all three `KeyAlgorithm`s (Ed25519, Rsa, Ecdsa) round-trip across a restart, and a
  removed key stays removed.
- **Aggregate single restart:** populate every store through one `FileBackedPersistenceProvider`,
  dispose it, construct a second over the same directory, and assert all nine stores + keys survive.
- **Missing file:** a store over a nonexistent file reads as empty (no throw).
- **Corrupt file:** a store over a file containing invalid JSON reads as empty (no throw).
- **DI (3 tests):** `UseFileBackedPersistence` registers a `FileBackedPersistenceProvider` as
  `IPersistenceProvider`; a second `AddInMemoryPersistence` after it is overridden (last wins); the
  provider's `IKeyStore` is the file-backed one.

Test count: 1054 → 1074 total (full suite green; all prior `Iris.Server.Tests` and the rest of the
suite still pass unchanged).

## Decisions

- **File-backed persistence lives in `Iris.Server`, not a new `Iris.Server.FilePersistence` project.**
  The in-memory provider is a separate project (`Iris.Server.InMemory`) only because it is the *default*
  that every host gets by default and that sample apps reference unconditionally. The file-backed
  provider is **opt-in** and its dependencies (`ActivityJson`, `Iri`, `IKeyStore`) already live in
  `Iris.Core`, so it has no reason to be a separate assembly — it is a sub-namespace of `Iris.Server`
  (`Iris.Server.Persistance`), exactly like the Phase 16.2 `FileBackedDeliveryQueue` lives in
  `Iris.Server.Delivery`. No new NuGet package: only `System.IO` (BCL) + `System.Text.Json` are used.
- **`IKeyStore` stays synchronous.** The key store is the only synchronous `I*Store` (signing happens
  synchronously on the hot request path). `FileBackedKeyStore` therefore uses the sync `WithState` /
  `Snapshot` forms on `FilePersistence`. Because `out`-param document reads cannot be `async` (CS1988),
  the document stores' `TryGet*` also use the sync `Snapshot` form.
- **Corrupt file degrades to empty, not an exception.** A persistence backend that throws on a corrupt
  file would crash the host at startup (or on first access) after a disk fault or an interrupted write.
  Treating unreadable state as empty is the safe default: the host comes up, the delivery queue
  (Phase 16.2) still replays, and an operator can inspect/restore the file. This matches the
  "never take the host down on data" stance of the rest of the persistence layer.

## Files changed

- `src/Iris.Server/Persistance/FilePersistence.cs` — **new**
- `src/Iris.Server/Persistance/FileBackedPersistenceProvider.cs` — **new**
- `src/Iris.Server/Persistance/Stores/FileBackedActorStore.cs` — **new**
- `src/Iris.Server/Persistance/Stores/FileBackedObjectStore.cs` — **new**
- `src/Iris.Server/Persistance/Stores/FileBackedActivityStore.cs` — **new**
- `src/Iris.Server/Persistance/Stores/FileBackedCommunityStore.cs` — **new**
- `src/Iris.Server/Persistance/Stores/FileBackedFollowStore.cs` — **new**
- `src/Iris.Server/Persistance/Stores/FileBackedLikeStore.cs` — **new**
- `src/Iris.Server/Persistance/Stores/FileBackedReplyStore.cs` — **new**
- `src/Iris.Server/Persistance/Stores/FileBackedRelayStore.cs` — **new**
- `src/Iris.Server/Persistance/Stores/FileBackedModerationStore.cs` — **new**
- `src/Iris.Server/Persistance/Stores/FileBackedKeyStore.cs` — **new**
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `UseFileBackedPersistence` + new usings
- `tests/Iris.Server.Tests/Persistance/FileBackedPersistenceTests.cs` — **new** (20 tests)
