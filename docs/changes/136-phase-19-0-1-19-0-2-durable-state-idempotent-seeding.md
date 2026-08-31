# 136 — Phase 19.0.1 + 19.0.2: durable sample server state + idempotent seeding

## Summary

Phase 19.0 prepares the live stack for the evaluation program. This change adds opt-in file-backed persistence to the sample server (19.0.1) and makes its seeding idempotent by IRI (19.0.2), so `docker compose down` (no `-v`) + `up` recreates the containers over a durable volume with zero data loss: actors, signing keys, follows, outbox content, and pending deliveries all survive, and re-running the seed against a non-empty volume never duplicates or clobbers.

## What changed

### `samples/SampleServer/Program.cs`

- `ConfigureServices` now reads `Iris:PersistenceDirectory` from the host's `IConfiguration`. When set, it creates the directory, builds a `FileBackedPersistenceProvider` (Phase 16.4), and calls `UseFileBackedDelivery` (Phase 16.2) with the delivery queue + dead-letter paths in the same directory. When unset (the default — every local dev run and the whole existing test surface), the `InMemoryPersistenceProvider` is used, exactly as before.
- `SeedSampleData` signature widened from `InMemoryPersistenceProvider` → `IPersistenceProvider` (provider-agnostic) and gained an optional `IConfiguration? configuration` parameter (for the key-dump switch, read per-host rather than from the process environment).
- New `EnsureKey(IPersistenceProvider, Iri, Func<Iri, ISigningKey>)` — idempotent key minting: `persistence.Keys.TryGetKey(keyIri, out var existing)` recovers the persisted key on recreation, else mints a fresh one. A signature made before the recreation still verifies after.
- New `EnsureActor(IPersistenceProvider, string, Iri, Iri, ISigningKey)` — idempotent actor put: reads the existing actor first; if present, returns it (no overwrite of post-seed mutations like a new `publicKey` extension); if absent, builds and stores the full actor document.
- New `AddSeededOutboxItem(IPersistenceProvider, Iri, IObject)` — per-IRI outbox dedup guard: reads the outbox, checks whether the item's IRI is already present (matching by `Id` for objects or `Href` for links, the same convention the store uses for removal), and only appends when absent. All five seeded outbox items (alice/bob/carla notes, bob's reply, carla's like) go through this helper.
- `Iris__DumpKeyTo` (the S10 smoke-test key-dump switch) is now read from the host's `IConfiguration` (`Iris:DumpKeyTo`) rather than `Environment.GetEnvironmentVariable`, for the same per-host isolation reason as `PersistenceDirectory` — an in-process `TestServer` host cannot leak the setting across tests via process-level environment state.

### `docker-compose.yml`

- `iris-a` and `iris-b` services: added `Iris__PersistenceDirectory: /data` environment variable and a named volume mount (`iris-a-data:/data` / `iris-b-data:/data`).
- Top-level `volumes:` section added: `iris-a-data:` and `iris-b-data:`. A `docker compose down` without `-v` leaves these volumes in place (state survives a recreation); `down -v` wipes them for a clean reset.

### Tests (`tests/SampleServer.Tests/SampleServerPersistenceTests.cs`)

Three new integration tests:

1. **`SeedSampleData_FileBacked_SurvivesRecreation_WithoutDuplicatesOrRekey`** — the core durability + idempotency test. Seeds a fresh `FileBackedPersistenceProvider` over a temp directory, records a post-seed follow + a post-seed user post (state created "during a prior turn"), then rebuilds the provider over the same directory (simulating a container recreation) and re-runs the seed. Asserts: the signing keys are recovered (PEM-identical), not re-minted; the seeded outbox items are not duplicated (exact IRI set, count preserved); the post-seed follow and user post survive; the community and its seeded edges are intact; the object store serves the seeded notes by IRI.
2. **`CreateWebHostBuilder_WithPersistenceDirectory_ServesSeededGraphFromVolume`** — hosts the sample server in a `TestServer` with `Iris:PersistenceDirectory` set (via per-host `ConfigurationBuilder`, not process env). Asserts the seeded actor document, community document, and a seeded note are all served over HTTP from the file-backed store.
3. **`CreateWebHostBuilder_WithoutPersistenceDirectory_StaysInMemory`** — the default path: no `Iris:PersistenceDirectory` → the DI container holds an `InMemoryPersistenceProvider` (not the file-backed one), and the seeded graph is still served.

## Verification

- `dotnet build Iris.slnx` — 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test Iris.slnx` — 1183 tests, all passing (1180 pre-existing + 3 new).
- Live compose verification: `docker compose up -d --build` → both instances healthy → seeded actor/community/note all 200 → `docker compose down` (no `-v`) → volumes preserved → `docker compose up -d` → both instances healthy → seeded actor/community/note all 200 again, all 8 state files present in `/data/`.
