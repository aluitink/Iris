using Iris.Client;
using Iris.Core;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleServer;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 8 S6 tests: the explorer's read screens. Each screen's client call is exercised in-process
/// (no Blazor rendering harness — the screens are thin wrappers over the same <see cref="IActivityPubClient"/>
/// reads these tests drive directly, so covering the call covers the screen): the instance overview
/// (<c>GetNodeInfoAsync</c>), the actors directory/search (<c>SearchAsync</c>), the actor detail
/// (<c>GetObjectAsync</c> + outbox + moderation), the object view (<c>GetObjectAsync</c> + replies), and
/// the community (feed + members + search). A signed logon is established first so the reads are signed,
/// exactly as the screens perform them through <c>ExplorerSession.GetClient()</c>.
/// </summary>
public sealed class S6ScreenTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// Hosts an in-process <see cref="SampleServer"/> whose advertised host is a port-less label, so the
    /// screens' IRIs (built from the dial base) resolve to the seeded graph in-process.
    /// </summary>
    private static TestServer StartLabeledServer(string hostName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:HostName"] = hostName,
                ["Iris:Port"] = "80",
                ["Iris:Https"] = "false",
            })
            .Build();

        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(s => SampleServer.SampleServer.ConfigureServices(s, configuration))
            .Configure(SampleServer.SampleServer.ConfigureApp);

        return new TestServer(builder);
    }

    /// <summary>
    /// Logs on as the seeded <c>alice</c> and returns the signed client the screens use.
    /// </summary>
    private static async Task<(TestServer Server, IActivityPubClient Client)> LogOnAsync(string host)
    {
        var server = StartLabeledServer(host);
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        return (server, session.GetClient());
    }

    private static async Task<IReadOnlyList<IObjectOrLink>> CollectAsync(IAsyncEnumerable<IObjectOrLink> items)
    {
        var list = new List<IObjectOrLink>();
        await foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    // --- Instance overview (GetNodeInfoAsync) ---------------------------------------

    [Fact]
    public async Task InstanceOverview_NodeInfo_ReturnsInstanceMetadata()
    {
        var (server, client) = await LogOnAsync("iris-a");
        using var _ = server;

        // The screens derive the instance base as {dialAuthority}/ap/v1; NodeInfo is served at
        // {base}/nodeinfo/2.0.
        var instanceBase = new Iri("http://localhost/ap/v1");
        var nodeInfo = await client.GetNodeInfoAsync(instanceBase);

        Assert.NotNull(nodeInfo);
        Assert.Equal("2.0", nodeInfo!.Version);
        Assert.Equal("iris", nodeInfo.SoftwareName);
        Assert.Contains("activitypub", nodeInfo.Protocols);
        Assert.False(nodeInfo.OpenRegistrations);
        Assert.Equal("iris-iris-a", nodeInfo.InstanceName);
    }

    [Fact]
    public async Task InstanceOverview_NodeInfo_BadBase_ReturnsNull()
    {
        var (server, client) = await LogOnAsync("iris-a");
        using var _ = server;

        // A base that does not resolve to a NodeInfo endpoint returns null (the screen shows "not
        // served") rather than throwing.
        var nodeInfo = await client.GetNodeInfoAsync(new Iri("http://localhost/no-such-base"));
        Assert.Null(nodeInfo);
    }

    // --- Actors directory + search (SearchAsync) ------------------------------------

    [Fact]
    public async Task ActorsDirectory_EmptyQuery_ReturnsActorsAndContent()
    {
        var (server, client) = await LogOnAsync("iris-a");
        using var _ = server;

        var instanceBase = new Iri("http://localhost/ap/v1");
        var results = await CollectAsync(client.SearchAsync(instanceBase, null));

        // The directory includes every local actor (alice, bob) and the seeded content objects.
        Assert.True(results.Count >= 2, $"the directory must list at least the two local actors (got {results.Count})");
        var actorIris = results
            .Where(o => o is IObject { Id: { } id } && id.Contains("/ap/v1/u/"))
            .Select(o => (o as IObject)!.Id!)
            .ToList();
        Assert.Contains(actorIris, id => id.EndsWith("/u/alice"));
        Assert.Contains(actorIris, id => id.EndsWith("/u/bob"));
    }

    [Fact]
    public async Task ActorsDirectory_SearchByHandle_FiltersToMatch()
    {
        var (server, client) = await LogOnAsync("iris-a");
        using var _ = server;

        var instanceBase = new Iri("http://localhost/ap/v1");
        var results = await CollectAsync(client.SearchAsync(instanceBase, "bob"));

        // A handle search matches the actor by preferredUsername / IRI.
        Assert.Contains(results, o => o is IObject { Id: { } id } && id.Contains("/u/bob"));
        Assert.DoesNotContain(results, o => o is IObject { Id: { } id } && id.Contains("/u/alice"));
    }

    // --- Actor detail (GetObjectAsync + outbox + moderation) -------------------------

    [Fact]
    public async Task ActorDetail_LoadsActor_OutboxAndModeration()
    {
        var (server, client) = await LogOnAsync("iris-a");
        using var _ = server;

        var actorIri = new Iri("http://localhost/ap/v1/u/alice");
        var actor = await client.GetObjectAsync(actorIri);

        Assert.NotNull(actor);
        Assert.True(typeof(Actor).IsAssignableFrom(actor!.GetType()), $"the actor must be an Actor (got {actor.GetType().Name})");
        Assert.Equal("alice", (actor as Actor)!.PreferredUsername);

        // The outbox carries alice's seeded note.
        var outbox = await CollectAsync(client.GetCollectionItemsAsync(actorIri.OutboxOf()));
        Assert.True(outbox.Count >= 1, $"alice's outbox must have at least one item (got {outbox.Count})");

        // The moderation collections are readable (counts may be zero on a clean instance).
        var mutes = await CountAsync(client.GetMutesAsync(actorIri));
        var blocks = await CountAsync(client.GetBlocksAsync(actorIri));
        var flags = await CountAsync(client.GetFlagsAsync(actorIri));
        Assert.True(mutes >= 0 && blocks >= 0 && flags >= 0);
    }

    private static async Task<int> CountAsync(IAsyncEnumerable<IObjectOrLink> items)
    {
        var count = 0;
        await foreach (var _ in items)
        {
            count++;
        }

        return count;
    }

    // --- Object view (+ replies) -----------------------------------------------------

    [Fact]
    public async Task ObjectView_LoadsObjectAndReplies()
    {
        var (server, client) = await LogOnAsync("iris-a");
        using var _ = server;

        // The seeded alice note lives at {actor}/notes/1 on the advertised host (iris-a); the object
        // screen loads it by that IRI (the §4.4 split: the transport dials localhost, but the object
        // IRI carries the advertised host the server stored it under).
        var target = new Iri("http://iris-a/ap/v1/u/alice/notes/1");
        var note = await client.GetObjectAsync(target);
        Assert.NotNull(note);
        Assert.IsType<Note>(note);
        Assert.Equal("<p>Welcome to the Iris sample server!</p>", (note as Note)!.Content?.FirstOrDefault() ?? "");

        // Replies: bob replied to alice's note, so the thread is non-empty.
        var replies = await CollectAsync(client.GetRepliesAsync(target));
        Assert.True(replies.Count >= 1, $"alice's note must have at least one reply (got {replies.Count})");
    }

    // --- Community (feed + members + search) ----------------------------------------

    [Fact]
    public async Task Community_FeedMembersAndSearch()
    {
        var (server, client) = await LogOnAsync("iris-a");
        using var _ = server;

        var communityIri = new Iri("http://localhost/ap/v1/c/iris");

        // Feed: the seeded community merges alice + bob outboxes (>= 2 notes).
        var feed = await CollectAsync(client.GetCommunityFeedAsync(communityIri));
        Assert.True(feed.Count >= 2, $"the seeded community feed must have at least two items (got {feed.Count})");

        // Members: the community's membership collection lists its members.
        var membersIri = new Iri("http://localhost/ap/v1/c/iris/members");
        var members = await CollectAsync(client.GetCollectionItemsAsync(membersIri));
        Assert.True(members.Count >= 2, $"the community must have at least two members (got {members.Count})");

        // Search over the instance (the community screen's search box reuses the directory search).
        var instanceBase = new Iri("http://localhost/ap/v1");
        var search = await CollectAsync(client.SearchAsync(instanceBase, "alice"));
        Assert.True(search.Count >= 1, "searching 'alice' must return at least the alice actor");
    }
}
