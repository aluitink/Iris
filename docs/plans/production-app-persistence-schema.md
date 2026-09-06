# Production App — Persistence Schema Design (EF Core + PostgreSQL)

> **Level 3.** Parent: [production-app-persistence.md](production-app-persistence.md). Grandparent: [production-app-overview.md](production-app-overview.md).

## 1. Project layout

```
src/Iris.Server.Data/
├── Iris.Server.Data.csproj        (net10.0; refs Iris.Core, Iris.Server, Npgsql.EntityFrameworkCore.PostgreSQL, Microsoft.EntityFrameworkCore.Design)
├── IrisDbContext.cs               (the EF Core DbContext)
├── Entities/                      (one file per entity below)
├── Stores/                        (one class per store interface, e.g. EfActorStore : IActorStore)
├── EntityFrameworkPersistenceProvider.cs   (bundles the Stores/* into IPersistenceProvider)
├── EntityFrameworkPersistenceExtensions.cs (AddEntityFrameworkPersistence(IServiceCollection, IConfiguration))
├── Accounts/                      (IUserAccountStore + EfUserAccountStore — declared *and* implemented here,
│                                   not in Iris.Server; see production-app-authentication.md §5)
├── Migrations/                    (dotnet-ef generated)
└── IrisDbContextFactory.cs        (IDesignTimeDbContextFactory<IrisDbContext>, for `dotnet ef migrations add` without running the full app host)
```

## 2. Guiding rule for every entity

For each store interface, ask: *what does a caller need to query/filter/sort by?* Those fields become real columns. Everything else — the full ActivityStreams/`iris:` shape of the object/activity — goes in a single `jsonb` (Postgres `Npgsql` maps `JsonDocument`/`string` to `jsonb` natively; alternatively map a `string` column with `.HasColumnType("jsonb")`) column holding the canonical serialized document (the same JSON `Iris.Core`'s `ActivityJson` already produces). **This is the load-bearing design decision that keeps migrations rare** — a new ActivityStreams field or a new `iris:` extension term never requires a migration, because it just appears inside the existing `jsonb` payload the next time that row is written.

## 3. Entity sketch

| Entity | Indexed/relational columns | `jsonb` payload? | Backs interface |
|---|---|---|---|
| `ActorEntity` | `Id` (IRI, PK), `Handle` (unique), `Type` (Person/Group), `CreatedAt` | Yes (`Document` — the full actor doc) | `IActorStore` |
| `KeyEntity` | `KeyId` (PK), `ActorId` (FK), `Algorithm` | No — `PrivateKeyPem`/`PublicKeyPem` as plain text columns (already opaque strings, no benefit to jsonb) | `IKeyStore` |
| `ActivityEntity` | `Id` (IRI, PK), `ActorId` (FK, the actor whose outbox/inbox this belongs to), `Direction` (Inbox/Outbox enum), `ActivityType` (string, e.g. "Create"/"Follow" — for filtering), `CreatedAt` (sort key) | Yes (`Document` — the full activity) | `IActivityStore` |
| `ObjectEntity` | `Id` (IRI, PK), `AttributedTo` (actor IRI, nullable), `ObjectType`, `IsTombstoned` (bool), `CreatedAt` | Yes (`Document`) | `IObjectStore` |
| `CreateIndexEntity` | `ObjectId` (PK/FK → ObjectEntity), `CreateActivityId` | No | `ICreateIndex` |
| `FollowEdge` | `FollowerId`, `FollowedId` (composite PK), `State` (Pending/Accepted enum), `CreatedAt` | No | `IFollowStore` |
| `LikeEdge` | `LikerId`, `ObjectId` (composite PK), `CreatedAt` | No | `ILikeStore` |
| `AnnounceEdge` | `AnnouncerId`, `ObjectId` (composite PK), `CreatedAt` | No | `IAnnounceStore` |
| `ReplyEdge` | `ParentId`, `ReplyId` (composite PK) | No | `IReplyStore` |
| `ModerationEdge` | `SourceId`, `TargetId`, `Kind` (Block/Flag/Mute enum), composite PK on (Source, Target, Kind), `CreatedAt`, `Reason` (nullable, for Flag) | No | `IModerationStore` |
| `RelaySubscription` | `ActorId`, `RelayId` (composite PK) | No | `IRelayStore` |
| `CommunityMembership` | `CommunityId`, `MemberId` (composite PK), `Role` (nullable, if/when community roles are added) | No | `ICommunityStore` (membership half; the community's own actor doc reuses `ActorEntity`/`ObjectEntity` since a `Group` is an actor) |
| `MediaAsset` | `Id` (PK, matches the media IRI's id segment), `ContentType`, `FileName`, `SizeBytes`, `ContentHash`, `OwnerActorId`, `StorageBackend` (Local/S3 enum), `StorageKey`, `CreatedAt` | No | `IMediaStore` (metadata half — see [production-app-media-storage.md](production-app-media-storage.md) for the blob half) |
| `UserAccount` | `Id` (PK, Guid), `Username` (unique), `PasswordHash`, `Role` (User/Admin enum), `ActorId` (FK \u2192 ActorEntity, unique), `NotificationsReadAt` (`DateTimeOffset`, nullable \u2014 MVP "mark as read" cursor, see [production-app-feature-set.md](production-app-feature-set.md) \u00a72), `CreatedAt` | No | New `IUserAccountStore` \u2014 declared *and* implemented in `Iris.Server.Data`'s `Accounts/` folder, not `Iris.Server` (auth workstream, [production-app-authentication.md](production-app-authentication.md) \u00a75) |

Notes:
- Every `jsonb` column stores the object **exactly as served** (same JSON the endpoints already produce via `ActivityJson`), so a store's `GetAsync`-type method can deserialize it straight back into the `KristofferStrube.ActivityStreams` type — no lossy relational reconstruction.
- Composite-PK edge tables (`FollowEdge`, `LikeEdge`, etc.) are cheap, narrow, and index well for both directions (add a non-clustered index on the second column of each composite key for the reverse lookup, e.g. `FollowedId` for "who follows this actor").
- `ActivityEntity.ActivityType` and `Direction` are the two columns that make `IActivityStore`'s paging/filtering queries efficient without touching the `jsonb` payload.

## 4. Migrations workflow

**Yes, the relational shape will still change** as the app is built — a new table for a new store, a new index, a renamed column, a tweak to a composite key. The hybrid design (§2) only guarantees that *routine AP/`iris:` field growth* needs zero migrations, not that the handful of relational tables in §3 are frozen forever. The policy below is how schema changes get made **without** accumulating a long, layered migration history while the app is still pre-launch.

### 4.1 Pre-launch policy: squash, don't layer

While there is no real, un-replaceable user data in any running deployment (local dev, CI, and — importantly — **the `iris.luit.ink` deployment until it has real users on it**), treat the `Migrations/` folder as **disposable, regenerated state, not an accumulating history**:

1. Change the EF Core model (`IrisDbContext`, entity classes) however the feature needs.
2. Delete every existing file under `Migrations/` (the whole folder's contents, including the model snapshot).
3. Regenerate a single migration from scratch: `dotnet ef migrations add InitialCreate`.
4. Deploy by **wiping and recreating the database** — `docker compose down -v` (drops the `iris-db-data` volume) + `up` — never `dotnet ef database update` against an existing database whose old schema doesn't match. There is deliberately **no upgrade path** between schema versions during this phase; every deploy during active schema churn is a clean-slate deploy.

At every point in time there is **exactly one migration file**, named `InitialCreate`, that reflects the *current* model — not a chain of twenty incremental steps that happened to get there. This is the concrete answer to "will we ever need a new migration": individual migration *files* are still regenerated constantly as the schema evolves, but there is never a *second* migration stacked on top of the first — the first one is simply replaced. Nothing about EF Core's tooling requires layering; `dotnet ef migrations add` after deleting prior migrations happily produces a clean single migration against the current model.

**Why this is an acceptable tradeoff right now:** nobody's account, post, or follow graph is lost by wiping the dev/pre-launch database, because there isn't a real userbase yet to lose. The cost of "no upgrade path" is zero until real users exist; the benefit is never having to reason about a migration history, a partially-applied migration, or a schema drift between two migration chains. This is exactly the "hate migrations" pain point being designed away — not by avoiding EF Core, but by refusing to let its migration history grow in the first place while it doesn't need to.

### 4.2 The graduation point: when this stops applying

The day the `iris.luit.ink` deployment has **real registered users whose data must survive a deploy**, this policy flips — that's the line, not a calendar date, and it should be recorded in the [overview's decisions log](production-app-overview.md#9-decisions-log-fill-in-as-the-agent-resolves-open-questions) when crossed:

- From that point on, a schema change gets a **new, additive migration** on top of the existing history (normal EF Core practice) — `docker compose down -v` is no longer an acceptable deploy step, because it would destroy real data.
- Every new migration from that point is reviewed like a real production change: additive by default (new table, new nullable column), and any destructive-looking change (drop/rename a column, change a type) gets a hand-checked, explicit data-migration step — never a rubber-stamped auto-generated one (this half of the guidance doesn't change; only the "squash vs. layer" default flips).
- Squashing the history at that point is still possible in principle (EF Core supports it) but becomes a deliberate, rare operation done with a real backup and a maintenance window — not the default workflow it is pre-launch.

### 4.3 Mechanics

- **New AP/`iris:` fields → zero migrations regardless of which policy phase you're in** (they live in `jsonb`, §2) — validate this holds true as features are built; if a "just add a column" urge appears for something that's really AP content (not a relational edge), push back and put it in the payload instead.
- **Apply strategy (pre-launch):** apply the (single, current) migration automatically on startup (`dbContext.Database.Migrate()` in `Program.cs`). Since a schema change means a wiped database anyway during this phase, there's no "did the migration apply cleanly against existing data" risk to guard against yet — that guard becomes relevant only after graduation (§4.2), at which point switch to an explicit `dotnet ef database update` step in the deploy pipeline instead of an automatic startup call.
- Use `IDesignTimeDbContextFactory<IrisDbContext>` so `dotnet ef migrations add` works from the CLI without needing the full app's DI container/config resolved (point it at a fixed local dev connection string or read `ConnectionStrings:Iris` from `appsettings.Development.json`).

## 5. Testing

- Prefer **Testcontainers.PostgreSql** for integration tests over SQLite-in-memory or mocks — `jsonb` and Postgres-specific behavior don't exist in SQLite, and the whole point of this design is to prove it against the real engine.
- Reuse the existing `Iris.Testing` harness's shape (multi-instance `TestServer` federation tests) but parameterize the persistence provider so the *same* federation test suite that runs against `Iris.Server.InMemory` today can also run against `Iris.Server.Data` — this is the strongest proof that the AP-native interface boundary was actually respected (no server/handler code changed, only the DI registration).
