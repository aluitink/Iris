using Iris.Client;
using Iris.Client.Collections;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleServer;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 19.8.4 tests: the <c>Community</c> page now renders the community document (name/icon/summary/
/// url, via <see cref="IActivityPubClient.GetActorAsync"/>), the community's **following** and
/// **followers** collections (paged, clickable), and community **follow/unfollow**. These tests exercise
/// the exact client calls the page makes — <see cref="IActivityPubClient.GetActorAsync(Iri,
/// CancellationToken)"/> against <c>{community}</c>, <see cref="IActivityPubClient.GetCollectionAsync(Iri,
/// CollectionQuery, CancellationToken)"/> against <c>{community}/following</c> and
/// <c>{community}/followers</c>, and <see cref="IActivityPubClient.FollowAsync(Iri, Iri,
/// CancellationToken)"/> / <see cref="IActivityPubClient.UndoFollowAsync(Iri, Iri,
/// CancellationToken)"/>.
/// </summary>
/// <remarks>
/// The harness mirrors <see cref="S19ActorDetailCompletenessTests.StartHost"/> (the S3-style in-memory
/// ActivityPub pipeline with <c>BaseUri = http://localhost</c>, so writes dial the dial base directly —
/// not through the home proxy — and the in-process <see cref="TestServer"/> handler intercepts them).
/// A community (the library's <see cref="Group"/> actor) is seeded with a name, two members, and a follow
/// of a remote actor (so its <c>following</c> collection is non-empty).
/// </remarks>
public sealed class S19CommunityViewCompletenessTests
{
    private static Uri DialBase => new("http://localhost");

    private static readonly Iri AliceIri = new("http://localhost/ap/v1/u/alice");
    private static readonly Iri BobIri = new("http://localhost/ap/v1/u/bob");
    private static readonly Iri CommunityIri = new("http://localhost/ap/v1/c/iris");

    private static TestServer StartHost()
    {
        const string dialBase = "http://localhost";
        var persistence = new InMemoryPersistenceProvider();

        // alice — the logged-on actor (the community's key owner, mirroring the SampleServer seed).
        var aliceKeyId = new Iri($"{AliceIri.Value}#key-1");
        var aliceKey = KeyPairGenerator.GenerateRsa(aliceKeyId);
        persistence.Keys.PutKey(aliceKey);
        var alice = new Person
        {
            Id = AliceIri.Value,
            PreferredUsername = "alice",
            Name = ["alice"],
        };
        alice.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["publicKey"] = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id = aliceKeyId.Value,
                owner = AliceIri.Value,
                publicKeyPem = aliceKey.ExportPublicKeyPem(),
            }),
        };
        persistence.ActorStore.PutActorAsync(alice).GetAwaiter().GetResult();

        // bob — a second member.
        persistence.ActorStore.PutActorAsync(new Person
        {
            Id = BobIri.Value,
            PreferredUsername = "bob",
            Name = ["bob"],
        }).GetAwaiter().GetResult();

        // The community: a Group (an Actor) with a name, alice + bob as members, and a follow of a
        // remote actor (so its following collection is non-empty, mirroring the SampleServer seed of
        // carla). The community carries the alice key (the community is authored by its owner).
        var community = new Group
        {
            Id = CommunityIri.Value,
            Name = ["The Iris Community"],
            PreferredUsername = "iris",
        };
        community.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["publicKey"] = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id = aliceKeyId.Value,
                owner = AliceIri.Value,
                publicKeyPem = aliceKey.ExportPublicKeyPem(),
            }),
        };
        persistence.Communities.PutCommunityAsync(community).GetAwaiter().GetResult();
        persistence.Communities.AddMemberAsync(CommunityIri, AliceIri).GetAwaiter().GetResult();
        persistence.Communities.AddMemberAsync(CommunityIri, BobIri).GetAwaiter().GetResult();
        persistence.Communities.AddFollowAsync(CommunityIri, BobIri).GetAwaiter().GetResult();

        var builder = new WebHostBuilder()
            .ConfigureLogging(l => { l.ClearProviders(); l.SetMinimumLevel(LogLevel.None); })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri(dialBase);
                    opts.InstanceName = "iris-a";
                    opts.InstanceActorId = AliceIri;
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(persistence.Keys);
                s.AddSingleton<IActorDocumentFetcher>(new PersistenceActorFetcher(persistence));
                s.AddSingleton<IActorCredentialValidator>(new BasicAuthCredentialValidator(
                    (_, username, password) =>
                    {
                        var valid = username == "alice"
                            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                                System.Text.Encoding.UTF8.GetBytes(password),
                                System.Text.Encoding.UTF8.GetBytes(SampleServer.SampleServer.Password));
                        return new ValueTask<bool>(valid);
                    }));
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }

    private sealed class PersistenceActorFetcher(IPersistenceProvider persistence) : IActorDocumentFetcher
    {
        private readonly IPersistenceProvider _persistence = persistence;

        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => await _persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct) ? actor : null;
    }

    private static async Task<(TestServer Server, IActivityPubClient Client)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        return (server, session.GetClient());
    }

    private static async Task<IReadOnlyList<IObjectOrLink>> CollectPagesAsync(IAsyncEnumerable<CollectionPage> pages)
    {
        var items = new List<IObjectOrLink>();
        await foreach (var page in pages)
        {
            items.AddRange(page.Items);
        }

        return items;
    }

    private static string? IriOf(IObjectOrLink item)
    {
        if (item is IObject { Id: { } id })
        {
            return id;
        }

        if (item is ILink { Href: { } href })
        {
            return href.ToString();
        }

        return null;
    }

    /// <summary>
    /// Reads the community's followers collection, bypassing the local collection-page response cache
    /// (<c>?refresh=true</c>) so a freshly-recorded follow edge is visible (a cached empty read from before
    /// the follow would otherwise be served).
    /// </summary>
    private static Task<IReadOnlyList<IObjectOrLink>> ReadFollowersFreshAsync(IActivityPubClient client)
        => CollectPagesAsync(client.GetCollectionAsync(new Iri($"{CommunityIri.FollowersOf().Value}?refresh=true"), null, CancellationToken.None));

    /// <summary>
    /// Polls the community's followers collection (fresh, cache-bypassing) until it contains
    /// <paramref name="expectedIri"/> (or the timeout elapses). The follow's edge is recorded by the async
    /// <c>FollowActivityHandler</c> on the delivery worker, so the read must wait for the worker to drain
    /// the enqueued delivery (and bypass the response cache to see it).
    /// </summary>
    private static async Task<IReadOnlyList<IObjectOrLink>> PollFollowersUntilAsync(IActivityPubClient client, string expectedIri, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        IReadOnlyList<IObjectOrLink> last = [];
        while (Environment.TickCount < deadline)
        {
            last = await ReadFollowersFreshAsync(client);
            if (last.Select(IriOf).Contains(expectedIri))
            {
                return last;
            }

            await Task.Delay(50);
        }

        return last;
    }

    /// <summary>
    /// Polls the community's followers collection (fresh, cache-bypassing) until it no longer contains
    /// <paramref name="expectedIri"/> (or the timeout elapses) — for the undo (unfollow) path, whose edge
    /// removal is also async.
    /// </summary>
    private static async Task<IReadOnlyList<IObjectOrLink>> PollFollowersUntilGoneAsync(IActivityPubClient client, string expectedIri, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        IReadOnlyList<IObjectOrLink> last = [];
        while (Environment.TickCount < deadline)
        {
            last = await ReadFollowersFreshAsync(client);
            if (!last.Select(IriOf).Contains(expectedIri))
            {
                return last;
            }

            await Task.Delay(50);
        }

        return last;
    }

    /// <summary>
    /// The community document read: <c>GetActorAsync(communityIri)</c> returns the community (a
    /// <see cref="Group"/>, which is an <see cref="Actor"/>) carrying its <c>Name</c> — the exact call the
    /// page makes to render the community's name/icon/summary/url via <c>ObjectView</c>.
    /// </summary>
    [Fact]
    public async Task CommunityView_CommunityDoc_RendersName()
    {
        var (server, client) = await LogOnAsync();
        using var _ = server;

        // The page's exact read: GetActorAsync against the community IRI (a Group).
        var doc = await client.GetActorAsync(CommunityIri);

        Assert.True(doc is not null, "GetActorAsync must return the community document");
        // A community is the library's Group actor type.
        Assert.IsAssignableFrom<Group>(doc);
        // The seeded community carries its display name.
        var name = doc.Name?.FirstOrDefault();
        Assert.Equal("The Iris Community", name);
    }

    /// <summary>
    /// The community's following read: <c>GetCollectionAsync(communityIri.FollowingOf())</c> returns the
    /// actors the community follows. The seeded <c>iris</c> community follows bob, so the following
    /// collection contains bob's IRI.
    /// </summary>
    [Fact]
    public async Task CommunityView_FollowingCollection_ContainsFollowed()
    {
        var (server, client) = await LogOnAsync();
        using var _ = server;

        // The page's exact read: GetCollectionAsync against {community}/following.
        var following = await CollectPagesAsync(client.GetCollectionAsync(CommunityIri.FollowingOf(), null, CancellationToken.None));

        Assert.NotEmpty(following);
        // The seeded community follows bob; the following collection surfaces it as a bare IRI link.
        var iris = following.Select(IriOf).ToList();
        Assert.Contains(iris, iri => iri == BobIri.Value);
    }

    /// <summary>
    /// The community's followers read: <c>GetCollectionAsync(communityIri.FollowersOf())</c> returns the
    /// actors that follow the community. After a follow of the community (as alice), the followers
    /// collection contains alice's IRI — mirroring the actor followers behavior (the follower →
    /// community edge is recorded).
    /// </summary>
    [Fact]
    public async Task CommunityView_FollowersCollection_ContainsFollowers_AfterFollow()
    {
        var (server, client) = await LogOnAsync();
        using var _ = server;

        // Before any follow of the community, the followers collection is empty.
        var before = await CollectPagesAsync(client.GetCollectionAsync(CommunityIri.FollowersOf(), null, CancellationToken.None));
        Assert.Empty(before);

        // Alice (the logged-on actor) follows the community — the page's Follow button does exactly this.
        var followResult = await client.FollowAsync(AliceIri, CommunityIri);
        Assert.True(followResult.IsSuccess, $"the follow of the community must succeed: {followResult.StatusCode}");

        // The follow's edge (community's followers set) is recorded by the async FollowActivityHandler on
        // the delivery worker, so poll the followers collection until it reflects the edge (the delivery
        // is enqueued by the 202 and drained by the worker).
        var after = await PollFollowersUntilAsync(client, AliceIri.Value);
        Assert.NotEmpty(after);
        var iris = after.Select(IriOf).ToList();
        Assert.Contains(iris, iri => iri == AliceIri.Value);
    }

    /// <summary>
    /// The community follow/unfollow write round-trip: <c>FollowAsync</c> (the page's Follow button)
    /// succeeds and mints an id the page learns for a later undo; <c>UndoFollowAsync</c> (the page's
    /// Unfollow button, referencing the learned id) succeeds and clears the follower edge — the
    /// followers collection is empty again after the undo.
    /// </summary>
    [Fact]
    public async Task CommunityView_FollowThenUnfollow_RoundTrips()
    {
        var (server, client) = await LogOnAsync();
        using var _ = server;

        // Follow (the page's FollowAsync): succeeds and mints the activity id the page learns.
        var followResult = await client.FollowAsync(AliceIri, CommunityIri);
        Assert.True(followResult.IsSuccess, $"the follow must succeed: {followResult.StatusCode}");
        Assert.True(followResult.MintedId is { Length: > 0 }, "the follow must mint an activity id for a later undo");
        Assert.True(Iri.TryParse(followResult.MintedId, out var followIri), "the minted id must be a parseable IRI");

        // The undo (the page's UnfollowAsync) references the learned id.
        var undoResult = await client.UndoFollowAsync(AliceIri, followIri);
        Assert.True(undoResult.IsSuccess, $"the undo must succeed: {undoResult.StatusCode}");

        // After the undo the follower edge is cleared (async, on the delivery worker): the followers
        // collection no longer contains alice.
        var followers = await PollFollowersUntilGoneAsync(client, AliceIri.Value);
        Assert.Empty(followers);
    }

    /// <summary>
    /// The community's members read: <c>GetCollectionItemsAsync(communityIri + "/members")</c> returns the
    /// member actor IRIs as bare <see cref="ILink"/> items (ActorIrisToLinks) — the exact shape the page's
    /// Members card renders as clickable links to each member's actor detail (19.8.4 — members clickable).
    /// </summary>
    [Fact]
    public async Task CommunityView_MembersCollection_ItemsAreClickableActorIris()
    {
        var (server, client) = await LogOnAsync();
        using var _ = server;

        // The page's exact read: GetCollectionItemsAsync against {community}/members.
        var membersIri = new Iri($"{CommunityIri.Value.TrimEnd('/')}/members");
        var members = await CollectItemsAsync(client.GetCollectionItemsAsync(membersIri));

        // The seeded community has alice + bob as members.
        Assert.True(members.Count >= 2, $"the seeded community must have at least two members (got {members.Count})");

        // Every member item is a bare actor IRI link (clickable to the actor detail) — never a null/empty IRI.
        var iris = members.Select(IriOf).ToList();
        Assert.True(iris.All(iri => iri is not null && iri.Length > 0),
            $"every member item must carry a resolvable IRI (got {string.Join(", ", iris)})");
        Assert.Contains(iris, iri => iri == AliceIri.Value);
        Assert.Contains(iris, iri => iri == BobIri.Value);
        // The items are ILink (the server's ActorIrisToLinks shape), so the page deep-links each.
        Assert.All(members, item => Assert.IsAssignableFrom<ILink>(item));
    }

    /// <summary>
    /// The community's feed items are clickable (19.8.4 — feed items clickable): after a member posts a
    /// note, the community's <c>feed</c> collection carries that note as an item with a resolvable IRI
    /// (the object id) — the target of the page's object deep-link (<c>/object?iri=…</c>).
    /// </summary>
    [Fact]
    public async Task CommunityView_FeedItems_CarryResolvableObjectIris()
    {
        var (server, client) = await LogOnAsync();
        using var _ = server;

        // Seed a note in alice's outbox (a community member), so the community's unified feed surfaces it.
        var postResult = await client.PostNoteAsync(AliceIri, "<p>hello from alice</p>");
        Assert.True(postResult.IsSuccess, $"posting alice's note must succeed: {postResult.StatusCode}");

        // The page's exact read: GetCommunityFeedAsync (the {community}/feed paged collection).
        var feed = await CollectItemsAsync(client.GetCommunityFeedAsync(CommunityIri));

        Assert.True(feed.Count >= 1, $"the community feed must surface the member's note (got {feed.Count})");

        // Every feed item carries a resolvable IRI (the object the deep-link targets) — never null/empty.
        var iris = feed.Select(IriOf).ToList();
        Assert.True(iris.All(iri => iri is not null && iri.Length > 0),
            $"every feed item must carry a resolvable IRI (got {string.Join(", ", iris)})");
    }

    private static async Task<IReadOnlyList<IObjectOrLink>> CollectItemsAsync(IAsyncEnumerable<IObjectOrLink> items)
    {
        var list = new List<IObjectOrLink>();
        await foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }
}
