using System.Diagnostics;
using Iris.Server.Data;
using KristofferStrube.ActivityStreams;
using Npgsql;
using Xunit;

namespace Iris.Server.Data.Tests;

/// <summary>
/// 139.3 Scenario 10 (data volume growth sanity): seed a larger-than-typical dataset (hundreds of
/// posts/activities in one actor's outbox, plus a corpus of searchable objects) and confirm the read
/// query paths stay correct and responsive — the feed read (<see cref="IActivityStore.GetOutboxAsync"/>)
/// and the full-text search (<see cref="IObjectStore.SearchObjectsAsync"/> /
/// <see cref="IActorStore.SearchActorsAsync"/>) return the right rows in the right order with correct
/// pagination, and complete in a sane time. The bar is correctness (not raw speed): a generous
/// ceiling catches catastrophic degradation (e.g. a missing index turning a search into a full
/// sequential scan) without asserting a specific wall-clock figure.
/// </summary>
public sealed class DataVolumeGrowthTests : IClassFixture<PostgresFixture>
{
    /// <summary>Posts in the seeded actor's outbox (larger than a typical user's post count).</summary>
    private const int OutboxPosts = 500;

    /// <summary>Additional searchable objects in the corpus (beyond the outbox notes).</summary>
    private const int SearchCorpus = 1000;

    /// <summary>How many outbox notes carry the search needle (a known, non-trivial hit count).</summary>
    private const int NeedleHits = 50;

    /// <summary>
    /// Generous ceiling for the read queries: catches catastrophic degradation (a full sequential
    /// scan over the corpus, a missing index, an O(N^2) merge) without pinning a specific speed.
    /// The bar for this scenario is correctness, not raw speed.
    /// </summary>
    private static readonly TimeSpan ReadCeiling = TimeSpan.FromSeconds(30);

    private readonly PostgresFixture _fixture;

    public DataVolumeGrowthTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private IPersistenceProvider NewProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:ConnectionString"] = _fixture.ConnectionString,
                ["Iris:MediaBlobDir"] = Path.Combine(_fixture.BlobRoot, Guid.NewGuid().ToString("N")),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddEntityFrameworkPersistence(config);
        return services.BuildServiceProvider().GetRequiredService<IPersistenceProvider>();
    }

    [Fact]
    public async Task LargeDataset_OutboxFeedAndSearch_StayCorrectAndResponsive()
    {
        var p = NewProvider();
        var ns = "growth" + Guid.NewGuid().ToString("N")[..8];
        var actorIri = new Iri($"https://growth.local/ap/v1/u/{ns}");
        var needle = $"growthneedle-{ns}";

        // --- Seed: one local actor, a 500-post outbox (Create -> Note), and a 1000-object search corpus. ---
        // The documents are built with the ActivityStreams library + ActivityJson (the same canonical
        // form the stores store), then bulk-inserted with one UNNEST per table so seeding is fast
        // regardless of row count. 50 outbox notes carry the needle so search has a known hit count.
        var actorDoc = ActivityJson.Serialize(new Person
        {
            Id = actorIri.Value,
            PreferredUsername = ns,
            Name = [$"growth user {ns}"],
        });

        var boxItemIris = new List<string>(OutboxPosts);
        var activityDocs = new List<string>(OutboxPosts);
        var objectDocs = new List<string>(OutboxPosts + SearchCorpus);
        var objectIds = new List<string>(OutboxPosts + SearchCorpus);
        var objectAttributedTo = new List<string>(OutboxPosts + SearchCorpus);

        for (var i = 0; i < OutboxPosts; i++)
        {
            var noteId = $"https://growth.local/ap/v1/objects/{ns}-out-{i}";
            var activityId = $"https://growth.local/ap/v1/activities/{ns}-out-{i}";
            // i < NeedleHits carry the needle (a known search hit set).
            var content = i < NeedleHits ? $"<p>{needle} post {i}</p>" : $"<p>ordinary post {i}</p>";
            var note = new Note
            {
                Id = noteId,
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            };
            var create = new Create
            {
                Id = activityId,
                Actor = [new Link { Href = new Uri(actorIri.Value) }],
                Object = [note],
            };
            boxItemIris.Add(activityId);
            activityDocs.Add(ActivityJson.Serialize(create));
            objectDocs.Add(ActivityJson.Serialize(note));
            objectIds.Add(noteId);
            objectAttributedTo.Add(actorIri.Value);
        }

        for (var i = 0; i < SearchCorpus; i++)
        {
            var noteId = $"https://growth.local/ap/v1/objects/{ns}-corp-{i}";
            var content = i < NeedleHits ? $"<p>{needle} corpus {i}</p>" : $"<p>corpus post {i}</p>";
            var note = new Note
            {
                Id = noteId,
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            };
            objectDocs.Add(ActivityJson.Serialize(note));
            objectIds.Add(noteId);
            objectAttributedTo.Add(actorIri.Value);
        }

        await BulkSeedAsync(actorIri.Value, actorDoc, ns, boxItemIris, activityDocs,
            objectIds, objectAttributedTo, objectDocs);

        // --- (1) The feed read (the EF SQL underlying every feed) returns the full outbox, in order. ---
        // GetOutboxAsync serves newest-first: the last item added has the lowest Position. We added
        // posts 0..499 in order, so the served list is 499 (newest) down to 0 (oldest).
        var sw = Stopwatch.StartNew();
        var outbox = await p.Activities.GetOutboxAsync(actorIri);
        sw.Stop();
        var outboxElapsed = sw.Elapsed;

        Assert.Equal(OutboxPosts, outbox.Count);
        // Each outbox item deserializes back to a Create wrapping a Note.
        Assert.All(outbox, item => Assert.True(item is Create, "every outbox item must be a Create activity."));
        // Order: the first served item is the newest post (index 499), the last is the oldest (index 0).
        var firstNote = ((Create)outbox[0]).Object!.First() as Note;
        var lastNote = ((Create)outbox[^1]).Object!.First() as Note;
        Assert.NotNull(firstNote);
        Assert.NotNull(lastNote);
        Assert.Equal($"https://growth.local/ap/v1/objects/{ns}-out-{OutboxPosts - 1}", firstNote!.Id);
        Assert.Equal($"https://growth.local/ap/v1/objects/{ns}-out-0", lastNote!.Id);
        Assert.InRange(outboxElapsed, TimeSpan.Zero, ReadCeiling);

        // --- (2) Object search: the GIN-indexed tsvector query returns the known hit count, the right
        // page, and paginates correctly (page 2 is the next distinct slice). ---
        sw.Restart();
        var page = await p.Objects.SearchObjectsAsync(needle, 20, 0);
        sw.Stop();
        var searchElapsed = sw.Elapsed;

        var totalMatches = await p.Objects.CountSearchMatchesAsync(needle);
        Assert.Equal(2 * NeedleHits, totalMatches); // 50 outbox notes + 50 corpus notes carry the needle
        Assert.Equal(20, page.Count);
        Assert.All(page, obj => Assert.Contains(needle, ((Note)obj).Content!.Single()));
        Assert.InRange(searchElapsed, TimeSpan.Zero, ReadCeiling);

        // Pagination: page 2 (offset 20) returns the next 20 distinct objects, no overlap with page 1.
        var page2 = await p.Objects.SearchObjectsAsync(needle, 20, 20);
        Assert.Equal(20, page2.Count);
        var page1Ids = new HashSet<string>(page.Select(o => o.Id!));
        Assert.DoesNotContain(page2.Select(o => o.Id!), page1Ids.Contains);

        // A no-needle query returns zero rows (and does not throw).
        var noMatch = await p.Objects.SearchObjectsAsync($"zzz-absent-{ns}", 20, 0);
        Assert.Empty(noMatch);

        // --- (3) Actor search: the local actor (carries a Handle) is found by its unique name. ---
        var foundActors = await p.Actors.SearchActorsAsync($"growth user {ns}", 100, 0, localOnly: true);
        Assert.Contains(foundActors, a => a.Id == actorIri.Value);

        // Record the timings for the change doc's evidence note (asserted only against the ceiling above).
        Console.WriteLine(
            $"[139.3-s10] outbox read of {OutboxPosts} items: {outboxElapsed.TotalMilliseconds:F1} ms; " +
            $"object search (limit 20) over {objectIds.Count} objects: {searchElapsed.TotalMilliseconds:F1} ms; " +
            $"total needle matches: {totalMatches}.");
    }

    /// <summary>
    /// Bulk-seeds the actor, the outbox (BoxItems + Activities), and the object corpus (Objects) with
    /// one UNNEST insert per table, then backfills the <c>SearchVector</c> tsvector for the seeded rows
    /// using the same SQL the <c>AddSearchVector</c> migration uses — so the GIN index is actually
    /// populated and exercised by the search read.
    /// </summary>
    private async Task BulkSeedAsync(
        string actorIri, string actorDoc, string ns,
        IReadOnlyList<string> boxItemIris, IReadOnlyList<string> activityDocs,
        IReadOnlyList<string> objectIds, IReadOnlyList<string> objectAttributedTo,
        IReadOnlyList<string> objectDocs)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        // The actor (carries a Handle so it is a "local" actor for localOnly search).
        await ExecuteAsync(conn,
            "INSERT INTO \"Actors\" (\"Id\", \"Handle\", \"Type\", \"CreatedAt\", \"Document\") " +
            "VALUES (@id, @handle, 'Person', @created, @doc::jsonb)",
            new NpgsqlParameter("@id", actorIri),
            new NpgsqlParameter("@handle", ns),
            new NpgsqlParameter("@created", DateTimeOffset.UtcNow),
            new NpgsqlParameter("@doc", actorDoc));

        // The outbox: BoxItems (Direction 0, newest-first Position) + the Create activity documents.
        // Position is (count - 1 - i) so the item added last (i = count - 1) has Position 0 (newest).
        // Arrays are passed as native .NET arrays; Npgsql serializes them as Postgres array literals.
        var positions = Enumerable.Range(0, boxItemIris.Count).Select(i => (long)(boxItemIris.Count - 1 - i)).ToArray();
        await ExecuteAsync(conn,
            "INSERT INTO \"BoxItems\" (\"Direction\", \"ActorId\", \"ItemIri\", \"Position\") " +
            "SELECT 0, @actor, u.iri, u.pos FROM unnest(@iries, @pos) AS u(iri, pos)",
            new NpgsqlParameter("@actor", actorIri),
            new NpgsqlParameter("@iries", boxItemIris.ToArray()),
            new NpgsqlParameter("@pos", positions));

        await ExecuteAsync(conn,
            "INSERT INTO \"Activities\" (\"Id\", \"ActivityType\", \"CreatedAt\", \"Document\") " +
            "SELECT u.id, 'Create', @created, u.doc::jsonb " +
            "FROM unnest(@ids, @docs) AS u(id, doc)",
            new NpgsqlParameter("@created", DateTimeOffset.UtcNow),
            new NpgsqlParameter("@ids", boxItemIris.ToArray()),
            new NpgsqlParameter("@docs", activityDocs.ToArray()));

        // The object corpus: the Note documents (outbox notes + search-corpus notes).
        await ExecuteAsync(conn,
            "INSERT INTO \"Objects\" (\"Id\", \"AttributedTo\", \"ObjectType\", \"IsTombstoned\", \"CreatedAt\", \"Document\") " +
            "SELECT u.id, u.attr, 'Note', false, @created, u.doc::jsonb " +
            "FROM unnest(@ids, @attrs, @docs) AS u(id, attr, doc)",
            new NpgsqlParameter("@created", DateTimeOffset.UtcNow),
            new NpgsqlParameter("@ids", objectIds.ToArray()),
            new NpgsqlParameter("@attrs", objectAttributedTo.ToArray()),
            new NpgsqlParameter("@docs", objectDocs.ToArray()));

        // Backfill the tsvector for the seeded rows — the exact SQL the AddSearchVector migration uses —
        // so the GIN index (IX_Actors_SearchVector / IX_Objects_SearchVector) is populated and the search
        // read exercises it. Scoped to this test's namespace (via the unique IRI host + needle) so it does
        // not touch other tests' rows.
        await ExecuteAsync(conn,
            "UPDATE \"Objects\" SET \"SearchVector\" = " +
            "setweight(to_tsvector('simple', COALESCE((\"Document\" ->> 'content')::text, '')), 'A') || " +
            "setweight(to_tsvector('simple', COALESCE((\"Document\" ->> 'name')::text, '')), 'B') || " +
            "setweight(to_tsvector('simple', COALESCE((\"Document\" ->> 'summary')::text, '')), 'C') " +
            "WHERE \"Id\" LIKE @p",
            new NpgsqlParameter("@p", $"https://growth.local/ap/v1/objects/{ns}-%"));

        await ExecuteAsync(conn,
            "UPDATE \"Actors\" SET \"SearchVector\" = " +
            "setweight(to_tsvector('simple', COALESCE((\"Document\" ->> 'name')::text, '')), 'A') || " +
            "setweight(to_tsvector('simple', COALESCE((\"Document\" ->> 'preferredUsername')::text, '')), 'B') || " +
            "setweight(to_tsvector('simple', COALESCE((\"Document\" ->> 'summary')::text, '')), 'C') " +
            "WHERE \"Id\" = @id",
            new NpgsqlParameter("@id", actorIri));
    }

    private static Task ExecuteAsync(NpgsqlConnection conn, string sql, params NpgsqlParameter[] parameters)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        return cmd.ExecuteNonQueryAsync();
    }
}
