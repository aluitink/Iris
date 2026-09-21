using Iris.Client;
using Iris.Server.Data;
using KristofferStrube.ActivityStreams;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iris.Server.Data.Tests;

/// <summary>
/// Integration tests for the EF Core (PostgreSQL) <see cref="EntityFrameworkPersistenceProvider"/>:
/// they boot a real PostgreSQL via Testcontainers, run the EF migrations, and assert the provider
/// implements the <see cref="IPersistenceProvider"/> contract for each store. A shared
/// <see cref="PostgresFixture"/> (one container for the whole class) keeps the tests fast; each test
/// uses a distinct IRI namespace so tests do not interfere.
/// </summary>
public sealed class EfPersistenceContractTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public EfPersistenceContractTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static Link Link(string iri) => new() { Href = new Uri(iri) };

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
        var provider = services.BuildServiceProvider().GetRequiredService<IPersistenceProvider>();
        return provider;
    }

    [Fact]
    public async Task ActorStore_PutGetRemove_RoundTrips()
    {
        var p = NewProvider();
        var iri = new Iri($"https://test.local/ap/v1/u/actor-{Guid.NewGuid():N}");
        var actor = new Person { Id = iri.Value, PreferredUsername = "u" + Guid.NewGuid().ToString("N")[..8] };

        await p.Actors.PutActorAsync(actor);
        var found = await p.Actors.TryGetActorAsync(iri, out var got);
        Assert.True(found);
        Assert.Equal(iri.Value, got!.Id);
        Assert.Equal(actor.PreferredUsername, got.PreferredUsername);

        var removed = await p.Actors.RemoveActorAsync(iri);
        Assert.True(removed);
        var missing = await p.Actors.TryGetActorAsync(iri, out _);
        Assert.False(missing);
    }

    [Fact]
    public async Task ActivityStore_PutTryAddOutbox_RoundTrips()
    {
        var p = NewProvider();
        var actorIri = new Iri($"https://test.local/ap/v1/u/act-{Guid.NewGuid():N}");
        await p.Actors.PutActorAsync(new Person { Id = actorIri.Value, PreferredUsername = "a" });

        var note = new Note
        {
            Id = $"https://test.local/ap/v1/objects/{Guid.NewGuid():N}",
            Content = ["hello"],
            AttributedTo = [Link(actorIri.Value)],
        };
        var create = new Create
        {
            Id = $"https://test.local/ap/v1/activities/{Guid.NewGuid():N}",
            Actor = [Link(actorIri.Value)],
            Object = [note],
        };

        Assert.True(await p.Activities.TryAddActivityAsync(create));
        Assert.False(await p.Activities.TryAddActivityAsync(create)); // idempotent

        await p.Activities.AddToOutboxAsync(actorIri, create);
        var outbox = await p.Activities.GetOutboxAsync(actorIri);
        Assert.Single(outbox);

        Assert.True(await p.Activities.TryGetActivityAsync(new Iri(create.Id), out var got));
        Assert.Equal("Create", got!.Type?.FirstOrDefault());
    }

    /// <summary>
    /// 139.3 scenario 7 (duplicate/replay delivery idempotency) — the "no duplicate rows" evidence at
    /// the physical (EF/PostgreSQL) level. The existing <see cref="ActivityStore_PutTryAddOutbox_RoundTrips"/>
    /// asserts the <em>logical</em> no-op (<c>TryAddActivityAsync</c> returns <c>false</c> on the second
    /// add) but not the <em>physical</em> row count, so a regression that inserted a second row while
    /// still reporting <c>false</c> would slip through. This drives the inbox's add-if-absent twice (a
    /// redelivery) and then counts the rows in the <c>Activities</c> and <c>BoxItems</c> tables directly
    /// against the real Postgres, asserting each is exactly one.
    /// <para>
    /// The <c>Activities.Id</c> and <c>BoxItems(Direction, ActorId, ItemIri)</c> primary keys make a
    /// second row impossible at the database level; this test pins that guarantee (and the add-if-absent
    /// guard that keeps the insert a no-op) so a future schema or store change that would allow a
    /// duplicate row is caught here, not in production.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RedeliveredActivity_StoredOnce_SingleRowInDatabase()
    {
        var p = NewProvider();
        var actorIri = new Iri($"https://test.local/ap/v1/u/redel-{Guid.NewGuid():N}");
        await p.Actors.PutActorAsync(new Person { Id = actorIri.Value, PreferredUsername = "rd" });

        var activityIri = $"https://test.local/ap/v1/activities/redel-{Guid.NewGuid():N}";
        var note = new Note
        {
            Id = $"https://test.local/ap/v1/objects/redelnote-{Guid.NewGuid():N}",
            Content = ["redelivered"],
            AttributedTo = [Link(actorIri.Value)],
        };
        var create = new Create
        {
            Id = activityIri,
            Actor = [Link(actorIri.Value)],
            Object = [note],
        };

        // Simulate the inbox's add-if-absent: a first delivery stores the activity, a redelivery of the
        // SAME IRI is a no-op. Then record it in the recipient's inbox twice (the same redelivery).
        Assert.True(await p.Activities.TryAddActivityAsync(create), "the first delivery should store the activity.");
        Assert.False(await p.Activities.TryAddActivityAsync(create), "a redelivery of the same IRI should be a no-op.");

        await p.Activities.AddToInboxAsync(actorIri, create);
        await p.Activities.AddToInboxAsync(actorIri, create); // redelivery — must not duplicate the inbox entry

        // THE PHYSICAL ROW-COUNT EVIDENCE (139.3 s7): count the rows directly in the database.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        int Count(string sql, string iri)
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@iri", iri);
            // Postgres count(*) is a bigint (Int64).
            return Convert.ToInt32(cmd.ExecuteScalar()!);
        }

        Assert.True(
            Count("SELECT count(*) FROM \"Activities\" WHERE \"Id\" = @iri", activityIri) == 1,
            $"the redelivered activity must occupy exactly one row in \"Activities\" (got the count above).");

        Assert.True(
            Count("SELECT count(*) FROM \"BoxItems\" WHERE \"ItemIri\" = @iri", activityIri) == 1,
            "the redelivered activity must appear exactly once in the recipient's inbox (BoxItems).");
    }

    [Fact]
    public async Task ObjectStore_PutGetDelete_RoundTrips()
    {
        var p = NewProvider();
        var iri = new Iri($"https://test.local/ap/v1/objects/obj-{Guid.NewGuid():N}");
        var note = new Note { Id = iri.Value, Content = ["persisted"] };

        await p.Objects.PutObjectAsync(note);
        Assert.True(await p.Objects.TryGetObjectAsync(iri, out var got));
        Assert.Equal("Note", got!.Type?.FirstOrDefault());
        Assert.Equal("persisted", ((Note)got).Content!.Single());

        Assert.True(await p.Objects.TryDeleteObjectAsync(iri));
        Assert.False(await p.Objects.TryGetObjectAsync(iri, out _));
    }

    [Fact]
    public async Task FollowStore_RecordRemove_Directions()
    {
        var p = NewProvider();
        var follower = new Iri($"https://test.local/ap/v1/u/f-{Guid.NewGuid():N}");
        var target = new Iri($"https://test.local/ap/v1/u/t-{Guid.NewGuid():N}");

        await p.Follows.RecordFollowAsync(follower, target);
        Assert.True(await p.Follows.IsFollowingAsync(follower, target));
        Assert.Contains(target, await p.Follows.GetFollowingAsync(follower));
        Assert.Contains(follower, await p.Follows.GetFollowersAsync(target));

        Assert.True(await p.Follows.RemoveFollowAsync(follower, target));
        Assert.False(await p.Follows.IsFollowingAsync(follower, target));
    }

    [Fact]
    public async Task KeyStore_PutGetRemove_RoundTrips()
    {
        var p = NewProvider();
        var keyIri = new Iri($"https://test.local/ap/v1/u/k-{Guid.NewGuid():N}/#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyIri);
        p.Keys.PutKey(key);

        Assert.True(p.Keys.TryGetKey(keyIri, out var got));
        Assert.NotNull(got);
        Assert.Equal(keyIri, got!.KeyId);

        Assert.True(p.Keys.RemoveKey(keyIri));
        Assert.False(p.Keys.TryGetKey(keyIri, out _));
    }

    [Fact]
    public async Task CommunityStore_MembershipAndFollows_RoundTrips()
    {
        var p = NewProvider();
        var community = new Iri($"https://test.local/ap/v1/c/comm-{Guid.NewGuid():N}");
        var member = new Iri($"https://test.local/ap/v1/u/m-{Guid.NewGuid():N}");
        var follower = new Iri($"https://test.local/ap/v1/u/cf-{Guid.NewGuid():N}");

        await p.Communities.PutCommunityAsync(new Group { Id = community.Value, PreferredUsername = "c" });
        Assert.True(await p.Communities.TryGetCommunityAsync(community, out var got));
        Assert.Equal(community.Value, got!.Id);

        Assert.True(await p.Communities.AddFollowerAsync(community, member));
        Assert.False(await p.Communities.AddFollowerAsync(community, member)); // idempotent
        Assert.Contains(member, await p.Communities.GetFollowersAsync(community));

        Assert.True(await p.Communities.AddFollowAsync(community, follower));
        Assert.Contains(follower, await p.Communities.GetFollowsAsync(community));
    }

    [Fact]
    public async Task MediaStore_PutGet_RoundTrips()
    {
        var p = NewProvider();
        var baseUrl = new Iri("https://test.local");
        var bytes = System.Text.Encoding.UTF8.GetBytes("media-payload-" + Guid.NewGuid());

        var iri = await p.Media.PutAsync(bytes, "text/plain", "file.txt", baseUrl);
        Assert.True(await p.Media.TryGetAsync(iri, out var content, out var contentType, out var fileName));
        Assert.Equal(bytes, content);
        Assert.Equal("text/plain", contentType);
        Assert.Equal("file.txt", fileName);
    }

    /// <summary>
    /// Regression: the EF actor full-text search (<see cref="IActorStore.SearchActorsAsync"/> /
    /// <see cref="IActorStore.CountSearchMatchesAsync"/>) built its raw SQL with a <c>$$"""</c>
    /// raw-interpolated string, so the <c>{0}</c>..<c>{3}</c> tokens were consumed by C# string
    /// interpolation instead of reaching <c>FromSqlRaw</c> as parameter placeholders — every query with a
    /// non-empty search term threw <c>FormatException</c> (the <c>GET /ap/v1/search</c> endpoint 500'd on
    /// the EF/Postgres host). This drives the EF store against a real Postgres with a match and a
    /// no-match query, in both <c>localOnly</c> directions, and asserts the results (not just that no
    /// exception is thrown).
    /// </summary>
    [Fact]
    public async Task ActorStore_Search_MatchesAndCounts_DoesNotThrow()
    {
        var p = NewProvider();
        var ns = "search" + Guid.NewGuid().ToString("N")[..8];

        // A local actor (carries a Handle) whose name matches the query.
        var localActorIri = new Iri($"https://test.local/ap/v1/u/{ns}-local");
        await p.Actors.PutActorAsync(new Person
        {
            Id = localActorIri.Value,
            PreferredUsername = $"{ns}-local",
            Name = [$"needle-{ns}"],
        });

        var query = $"needle-{ns}";

        // Both localOnly directions must run without throwing and return the matching actor.
        foreach (var localOnly in new[] { false, true })
        {
            var found = await p.Actors.SearchActorsAsync(query, 100, 0, localOnly: localOnly);
            Assert.Contains(found, a => a.Id == localActorIri.Value);

            var count = await p.Actors.CountSearchMatchesAsync(query, localOnly: localOnly);
            Assert.True(count >= 1, $"expected >= 1 match for localOnly={localOnly}, got {count}");
        }

        // A no-match query returns zero rows (and does not throw).
        var empty = await p.Actors.SearchActorsAsync("zzz-no-such-needle-" + Guid.NewGuid().ToString("N")[..8], 100, 0, localOnly: false);
        Assert.Empty(empty);
        Assert.Equal(0, await p.Actors.CountSearchMatchesAsync("zzz-no-such-needle-" + Guid.NewGuid().ToString("N")[..8], localOnly: false));
    }

    /// <summary>
    /// S36 repro over the EF (PostgreSQL) store: an actor's outbox holds a content <c>Create</c> of a
    /// note plus actor-document noise (an <c>Update</c> and an <c>Add</c> whose object is the actor IRI).
    /// The <see cref="Iris.Server.Services.FeedService"/> (the home-timeline feed) must surface the actor's
    /// own note <c>Create</c>. This drives the <em>real</em> EF <see cref="IActivityStore"/> (the
    /// production store, <c>BoxItems</c>/<c>Activities</c> jsonb round-trip) rather than the in-memory
    /// store, so a store-specific divergence (the EF outbox read returning the noise but not the
    /// content, or a document round-trip that degrades the <c>Create</c>) is caught here.
    /// </summary>
    [Fact]
    public async Task S36_FeedService_OverEfStore_SurfacesOwnNoteCreate_AmongActorDocNoise()
    {
        var p = NewProvider();
        var ns = "s36" + Guid.NewGuid().ToString("N")[..8];
        var actorIri = new Iri($"https://test.local/ap/v1/u/{ns}");
        await p.Actors.PutActorAsync(new Person { Id = actorIri.Value, PreferredUsername = ns, Name = [$"{ns}"] });

        // The actor's own content post (a Create of a public Note).
        var noteIri = $"https://test.local/ap/v1/{ns}/notes/{Guid.NewGuid():N}";
        var createIri = $"https://test.local/ap/v1/{ns}/creates/{Guid.NewGuid():N}";
        var note = new Note
        {
            Id = noteIri,
            Content = ["s36 own post must surface in the home feed"],
            AttributedTo = [Link(actorIri.Value)],
            To = [Link("https://www.w3.org/ns/activitystreams#Public")],
        };
        var create = new Create
        {
            Id = createIri,
            Actor = [Link(actorIri.Value)],
            Object = [note],
        };
        await p.Activities.AddToOutboxAsync(actorIri, create);

        // Actor-document noise (profile update + a self add), the same shape the live outbox carries.
        var updateIri = $"https://test.local/ap/v1/{ns}/activities/{Guid.NewGuid():N}";
        await p.Activities.AddToOutboxAsync(actorIri, new Update
        {
            Id = updateIri,
            Actor = [Link(actorIri.Value)],
            Object = [Link(actorIri.Value)],
        });
        var addIri = $"https://test.local/ap/v1/{ns}/adds/{Guid.NewGuid():N}";
        await p.Activities.AddToOutboxAsync(actorIri, new Add
        {
            Id = addIri,
            Actor = [Link(actorIri.Value)],
            Object = [Link(actorIri.Value)],
        });

        // Sanity: the EF outbox read returns all three (the store round-trip works).
        var outbox = await p.Activities.GetOutboxAsync(actorIri);
        Assert.Equal(3, outbox.Count);

        // Run the real home-timeline feed over the EF store. The actor has no follows, so the feed is
        // exactly the actor's own outbox items — the note Create must survive.
        var feed = new Iris.Server.Services.FeedService(
            p,
            new EfLocalActorResolver(p),
            new EfNullActorDocumentFetcher(),
            new EfNullClient(),
            Microsoft.Extensions.Options.Options.Create(new Iris.Server.Services.FeedOptions()));

        var items = await feed.GetFeedAsync(actorIri);

        // The note's Create must be present in the feed (S36: it was absent on the live EF host).
        Assert.Contains(items, i =>
        {
            if (i is not Activity a)
            {
                return false;
            }
            var first = a.Object?.FirstOrDefault();
            return a.Type?.FirstOrDefault() == "Create"
                   && first is IObject o
                   && o.Id == noteIri;
        });
    }

    private sealed class EfLocalActorResolver(IPersistenceProvider persistence) : Iris.Server.Caching.ILocalActorResolver
    {
        public async Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default)
            => await persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false);
    }

    private sealed class EfNullActorDocumentFetcher : Iris.Server.Security.IActorDocumentFetcher
    {
        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => Task.FromResult<Actor?>(null);
    }

    /// <summary>
    /// A null <see cref="Iris.Client.IActivityPubClient"/> for the feed service over the EF store:
    /// the actor under test has no remote follows, so no remote collection fetch occurs. The client
    /// methods that would touch the wire are inert (empty enumerables / 202 results) and the rest are
    /// unimplemented stubs (the feed path never calls them).
    /// </summary>
    private sealed class EfNullClient : Iris.Client.IActivityPubClient
    {
        public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default)
            => Task.FromResult<IObject?>(null);

        public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
            => Task.FromResult<Actor?>(null);

        public Task<NodeInfo?> GetNodeInfoAsync(Iri instanceBase, CancellationToken ct = default)
            => Task.FromResult<NodeInfo?>(null);

        public Task<Iris.Client.LemmyPostScore?> GetLemmyPostScoreAsync(Iri iri, CancellationToken ct = default)
            => Task.FromResult<Iris.Client.LemmyPostScore?>(null);

        public Task<Iris.Client.DeliveryResult> DeliverAsync(Iri targetId, IObject activity, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> UndoFollowAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> RejectAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> RequestJoinAsync(Iri actorId, Iri communityIri, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> RequestLeaveAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> AcceptJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> RejectJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> SetManuallyApprovesMembersAsync(Iri communityIri, bool enabled, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> SetManuallyApprovesFollowersAsync(Iri actorIri, bool enabled, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> UnlikeAsync(Iri actorId, Iri originalLikeId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> DislikeAsync(Iri objectIri, Iri actorIri, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> UndislikeAsync(Iri objectIri, Iri actorIri, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> UnannounceAsync(Iri actorId, Iri originalAnnounceId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> CreateCommunityAsync(
            Iri actorId,
            string name,
            string displayName,
            string? description = null,
            CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> UpdateActorAsync(Iri actorId, Actor updatedActor, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> UpdateNoteAsync(Iri actorId, Note updatedNote, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(0, false, ""));

        public Task<Iris.Client.DeliveryResult> UnblockAsync(Iri actorId, Iri originalBlockId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(0, false, ""));

        public Task<Iris.Client.DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(0, false, ""));

        public Task<Iris.Client.DeliveryResult> UnflagAsync(Iri actorId, Iri originalFlagId, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(0, false, ""));

        public Task<Iris.Client.DeliveryResult> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> PostNoteAsync(Iri actorId, Note note, CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> PostQuestionAsync(
            Iri actorId,
            string content,
            IEnumerable<string> options,
            DateTime? endsAt = null,
            bool multiple = false,
            IEnumerable<Iri>? to = null,
            IEnumerable<Iri>? cc = null,
            IEnumerable<Iri>? mentions = null,
            IEnumerable<string>? hashtags = null,
            Func<string, string?>? hashtagHrefFactory = null,
            CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<Iris.Client.DeliveryResult> PostReplyAsync(
            Iri actorId,
            Iri parentIri,
            string content,
            IEnumerable<Iri>? mentions = null,
            IEnumerable<Iri>? to = null,
            IEnumerable<string>? hashtags = null,
            Iri? conversationIri = null,
            CancellationToken ct = default)
            => Task.FromResult(new Iris.Client.DeliveryResult(202, true, ""));

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent(string.Empty),
            });

        public async IAsyncEnumerable<Iris.Core.Collections.CollectionPage> GetCollectionAsync(
            Iri collectionId,
            Iris.Client.Collections.CollectionQuery? query = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }

        public IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(
            Iri collectionId,
            Iris.Client.Collections.CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(
            Iri communityId,
            Iris.Client.Collections.CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(
            Iri actorId,
            Iris.Client.Collections.CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetRepliesAsync(
            Iri objectIri,
            Iris.Client.Collections.CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetLikesAsync(
            Iri objectIri,
            Iris.Client.Collections.CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetSharesAsync(
            Iri objectIri,
            Iris.Client.Collections.CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> SearchAsync(
            Iri instanceBase,
            string? query = null,
            Iris.Client.SearchOptions? options = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(
            Iri actorId,
            Iris.Client.Collections.CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(
            Iri actorId,
            Iris.Client.Collections.CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(
            Iri actorId,
            Iris.Client.Collections.CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetRelaysAsync(
            Iri actorId,
            Iris.Client.Collections.CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public async IAsyncEnumerable<IObjectOrLink> GetInboxItemsAsync(
            Iri actorId,
            Iris.Client.Pipeline.ProxyCredentials credentials,
            Iris.Client.Collections.CollectionQuery? query = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }

        public void Dispose()
        {
        }

        private static async IAsyncEnumerable<T> EmptyAsync<T>(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
