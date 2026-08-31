using Iris.Client;
using Iris.Client.Collections;
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
/// Phase 8 (second round) S5 tests: the Home page renders the community feed's actual items (not just a
/// count). The landing page's "Community feed" card now enumerates
/// <see cref="IActivityPubClient.GetCommunityFeedAsync"/> (capped by <c>CollectionQuery.Limit</c>) and
/// renders each item via the deep-linked <c>&lt;ObjectView&gt;</c>. These tests exercise the same call the
/// card uses: the seeded <c>iris</c> community feed yields real items (each an <c>IObjectOrLink</c> with a
/// resolvable IRI), and the <c>Limit</c> caps the enumeration the way the landing page caps it.
/// </summary>
public sealed class S5CommunityFeedTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// Hosts an in-process <see cref="SampleServer"/> (the seeded <c>iris</c> community, whose feed merges
    /// alice + bob's outboxes) with a port-less advertised host, so the screens' IRIs resolve in-process.
    /// Mirrors <see cref="S6ScreenTests.StartLabeledServer"/>.
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

    [Fact]
    public async Task CommunityFeed_YieldsRealItemsWithResolvableIris()
    {
        var (server, client) = await LogOnAsync("iris-a");
        using var _ = server;

        var communityIri = new Iri("http://localhost/ap/v1/c/iris");
        var feed = await CollectAsync(client.GetCommunityFeedAsync(communityIri));

        // The seeded community merges alice + bob's outboxes, so the feed has real items — the content
        // the Home page's community card renders (previously only the count was shown).
        Assert.True(feed.Count >= 2, $"the seeded community feed must have at least two items (got {feed.Count})");

        // Each item is renderable by the deep-linked ObjectView: it carries a resolvable IRI (an object id
        // or a link href), never a null/empty IRI.
        var iris = feed.Select(IriOf).ToList();
        Assert.True(iris.All(iri => iri is not null && iri.Length > 0),
            $"every community feed item must carry a resolvable IRI (got {string.Join(", ", iris)})");
    }

    [Fact]
    public async Task CommunityFeed_LimitCapsEnumeration()
    {
        var (server, client) = await LogOnAsync("iris-a");
        using var _ = server;

        var communityIri = new Iri("http://localhost/ap/v1/c/iris");

        // The landing page caps the enumeration (FeedPageSize); a small Limit must return at most that
        // many items (and still return items when the community is seeded).
        const int limit = 2;
        var capped = await CollectAsync(client.GetCommunityFeedAsync(communityIri, new CollectionQuery(Limit: limit)));

        Assert.True(capped.Count > 0, "a capped community feed read must still return items");
        Assert.True(capped.Count <= limit, $"the Limit must cap the enumeration to {limit} items (got {capped.Count})");
    }
}
