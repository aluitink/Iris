# Production App — Persistence & Media

> **Level 2.** Parent: [production-app-overview.md](production-app-overview.md). Children: [production-app-persistence-schema.md](production-app-persistence-schema.md), [production-app-media-storage.md](production-app-media-storage.md).

## 1. The contract you're implementing

`Iris.Server` already defines everything the server needs from persistence as interfaces (bundled behind `IPersistenceProvider`), with two existing implementations to use as reference:

- `Iris.Server.InMemory` — `ConcurrentDictionary`-backed, ephemeral. Good reference for *behavior* (what each method must do).
- `Iris.Server/Persistance/*` (file-backed: `FileBackedActorStore`, `FileBackedActivityStore`, etc., bundled by `FileBackedPersistenceProvider`) — durable, JSON-file-per-store. Good reference for *durability semantics* (what must survive a restart) but not a production-scale design (single JSON file + full-file rewrite per store).

The interfaces to implement (all in `Iris.Server.Stores`): `IActorStore`, `IActivityStore`, `IFollowStore`, `ILikeStore`, `IAnnounceStore`, `IReplyStore`, `IModerationStore`, `IRelayStore`, `IObjectStore`, `ICreateIndex`, `ICommunityStore`, `IKeyStore`, `IMediaStore`. Read each interface file under [`src/Iris.Server/Stores`](../../src/Iris.Server/Stores) before implementing it — this document does not repeat their method signatures, since they are the authoritative contract and may evolve.

**Rule: the new persistence project depends on `Iris.Core` + `Iris.Server` (for the interfaces) and your chosen data-access package. `Iris.Server`'s handlers, endpoints, and options never depend on the new project or reference EF Core types.** DI wiring is the only place that knows both sides — a single `AddEntityFrameworkPersistence(this IServiceCollection, IConfiguration)` extension method (mirroring `AddInMemoryPersistence()` / `UseFileBackedPersistence(directory)`) that registers the new provider against `IPersistenceProvider`.

## 2. Persistence technology — options

The core tension: ActivityPub objects/activities are **semi-structured JSON-LD documents** whose shape varies by type and evolves as the spec (and Iris's `iris:` extensions) grow, but the *relationships* between them (who follows whom, what liked what, community membership) are naturally relational/graph-like and benefit from real query support, indexes, and transactional integrity.

| Option | What it is | Pros | Cons |
|---|---|---|---|
| **A. EF Core + PostgreSQL, hybrid schema** *(recommended)* | Relational tables for the indexable/queryable shape (actor ids, follow edges, like edges, membership, media metadata) **plus** a `jsonb` column on the content-bearing tables (`objects`, `activities`) holding the full canonical AS2.0 JSON-LD document. The relational shape is *derived index*, not the source of truth for content — the `jsonb` payload is. | Familiar EF Core workflow (matches stated preference); PostgreSQL is free, mature, excellent `jsonb` support (indexable, queryable with `@>`/`->>` operators) so "the document is the truth" doesn't sacrifice queryability; migrations are **small and rare** because the relational shape is driven by the stable store interfaces, not by AP vocabulary churn (new AP fields land inside the existing `jsonb` column, zero migration); strong tooling (`dotnet ef`, pgAdmin, Npgsql); works great with Testcontainers for integration tests. | Two data shapes to keep mentally in sync (relational index vs jsonb payload) — mitigated by a thin repository layer per store that owns the mapping; still uses EF Core migrations for the (small, rarely-changing) relational shape. |
| **B. EF Core + SQL Server, hybrid schema** | Same idea as A, using SQL Server's native `JSON` type (SQL Server 2025) or `NVARCHAR(MAX)` + `JSON_VALUE`/`OPENJSON` on older versions instead of `jsonb`. | Matches the user's stated familiarity ("MSSQL is easy to work with"); same EF Core workflow as A; strong tooling (SSMS/Azure Data Studio). | Historically weaker JSON indexing/query ergonomics than Postgres `jsonb` (improving in SQL Server 2025, but less mature); licensing/footprint heavier for a self-hosted Docker stack than Postgres; Docker image is larger and slower to start. |
| **C. Marten (PostgreSQL document DB + event store, .NET-native)** | A .NET library that turns PostgreSQL into a schema-less document database (every "document" is a `jsonb` row, auto-mapped from a POCO) with a built-in event-sourcing/projection system. | Extremely good fit for "AP objects are documents that vary in shape" — near-zero schema ceremony, no migrations for document shape changes; Marten auto-manages *its own* schema (tables/indexes) via `store.Schema.ApplyAllConfiguredChangesToDatabase()`, so there's effectively no hand-written migration file to maintain; event sourcing pairs naturally with "an actor's outbox is an append-only activity log" (the library's own architectural principle); still PostgreSQL underneath, so ops tooling is the same as option A. | A less mainstream choice — smaller community/hiring pool than raw EF Core; still new-ish for complex relational queries (e.g., "all local actors this remote actor's followers overlap with") which need Marten's own query idioms rather than LINQ-to-SQL; the team has zero prior experience with it (learning curve for the agent implementing it). |
| **D. MongoDB (pure document NoSQL)** | Store activities/objects/actors as native BSON documents; no relational layer at all. | Zero schema/migration ceremony of any kind; naturally matches JSON-LD; horizontally scalable if that's ever needed; mature `MongoDB.Driver` for .NET. | Weakest fit for graph-ish relational queries (follow/follower fan-out, feed aggregation across many actors) — would need denormalized read-model collections maintained by hand; multi-document transactions exist but are less idiomatic than in a relational DB; introduces a second database technology family into the stack (team has SQL/EF Core familiarity, not Mongo); doesn't match the stated SQL preference at all. |
| **E. Pure normalized relational (EF Core, no JSON column, every AP field is its own column)** | The "traditional ORM" approach — a table (or table-per-type hierarchy) for every ActivityStreams type. | Fully queryable/reportable with plain SQL; no `jsonb` mental model needed. | This is the option that **violates the AP-native constraint worst** — every new ActivityStreams field or `iris:` extension requires a schema migration and an EF model change, which is exactly the "hate migrations" pain point and couples the DB schema tightly to the AP vocabulary. **Not recommended.** |

### Recommendation

**Option A: EF Core + PostgreSQL, hybrid schema.** It keeps the tool the user already likes (EF Core), swaps SQL Server for PostgreSQL (free, best-in-class JSON support, trivial to run in Compose), and — most importantly — solves the *actual* migrations pain by design: the relational tables only model the **stable store-interface shape** (ids, foreign keys, edges, timestamps, a `jsonb` payload column), so adding ActivityPub/`iris:` fields over time does not touch the schema at all. Migrations become a rare event tied to genuine structural changes (a new store interface, a new indexed query), not routine AP feature growth.

If, after building this, PostgreSQL's JSON ergonomics feel limiting, **Marten (option C)** is the strongest fallback — it is a smaller pivot than it looks (same PostgreSQL instance, same Docker Compose service) and would mostly replace the EF Core `DbContext` internals of `Iris.Server.Data`, not the surrounding store interfaces or anything above them.

**SQL Server stays available as a swap-in**, not a dead end: because the new project only depends on EF Core abstractions, offering both an `UseNpgsql(...)` and a `UseSqlServer(...)` registration path behind the same `AddEntityFrameworkPersistence` extension (selected by a config flag, e.g. `Iris:Persistence:Provider`) is a reasonable stretch goal once option A is proven — useful for a user who already runs SQL Server infrastructure. Don't build this speculatively in the MVP; note it as a Phase 2 nicety.

See [production-app-persistence-schema.md](production-app-persistence-schema.md) for the concrete table design, entity list, and migration workflow.

## 3. Media storage

`IMediaStore` already exists (`PutAsync`/read/metadata) with a file-backed reference implementation (`FileBackedMediaStore` — JSON metadata file + one sibling file per media id). Production needs:

1. **Metadata** (media id, content-type, file name, owner, content hash, size, created-at) — lives in the same PostgreSQL database as everything else (a normal EF Core table; this is exactly the "indexable, stable-shape" data option A is good at — no `jsonb` needed here).
2. **Blob bytes** — behind a new abstraction, `IMediaBlobStorage` (put/get/delete raw bytes by key), with two implementations:
   - **Local disk** (default for the MVP Compose stack) — bytes live under a bind-mounted/named Docker volume. Simplest possible production option; fine for a single-instance deployment.
   - **S3-compatible object storage** (MinIO self-hosted, or AWS S3 in the cloud) — for when the app needs to scale past a single host's disk, or wants offloaded/CDN-served media. Same interface, swapped via config.

The existing `GET /ap/v1/media/{id}` and `GET /ap/v1/media/proxy?url=…` routes, the 10 MiB upload cap, and the dedup-by-hash behavior all stay as-is — only the *storage backend* underneath `IMediaStore` changes.

See [production-app-media-storage.md](production-app-media-storage.md) for the concrete design.

## 4. What "done" looks like for this workstream

- `Iris.Server.Data` project implements every store interface against PostgreSQL via EF Core, registered via `AddEntityFrameworkPersistence(IServiceCollection, IConfiguration)`.
- A single EF Core migration (`InitialCreate`) creates the full schema from empty.
- `Iris.Web` boots against it with a real PostgreSQL container (Compose or Testcontainers) and passes the same class of integration tests the library uses for `Iris.Server.InMemory` today (actor CRUD, follow lifecycle, inbox/outbox, moderation, communities) — reusing the existing `Iris.Testing` harness where possible, pointed at the new provider instead of in-memory.
- Media upload/serve round-trips through local disk in dev/Compose; the abstraction is proven swappable (even if S3/MinIO isn't wired into the MVP Compose stack yet).
- Restarting the `Iris.Web` container (without wiping volumes) preserves all actors, activities, follows, likes, media, and keys.
