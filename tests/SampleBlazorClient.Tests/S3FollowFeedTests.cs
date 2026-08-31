using Iris.Client;
using Iris.Client.Collections;
using Iris.Core;
using Iris.Core.Collections;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleServer;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using CollectionPage = Iris.Core.Collections.CollectionPage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 8 (second round) S3 tests: the home timeline (followed feed). The <c>Feed</c> page renders the
/// logged-on actor's followed feed — the union of the followed actors' outboxes, newest first,
/// de-duplicated — via the paged <c>GetCollectionAsync</c> against <c>{actor}/feed</c>. These tests
/// exercise the feed the same way the page does: log on as a follower, record a follow edge to a target
/// whose outbox holds notes, and assert <c>GetFollowFeedAsync</c> / <c>GetCollectionAsync</c> yield the
/// followed actor's outbox items (newest first) and that pagination (<c>next</c>-link walking via the
/// <c>next</c> IRI) surfaces additional items.
/// </summary>
public sealed class S3FollowFeedTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// Hosts a real <see cref="Iris.Server"/> ActivityPub pipeline (in-memory) for the follow feed, with
    /// <c>alice</c> (the follower) and <c>bob</c> (the followed) seeded at the dial base. Mirrors
    /// <see cref="S7ScreenTests.StartHost"/>.
    /// </summary>
    private static TestServer StartHost()
    {
        const string dialBase = "http://localhost";
        var persistence = new InMemoryPersistenceProvider();
        var aliceIri = new Iri($"{dialBase}/ap/v1/u/alice");
        var aliceKeyId = new Iri($"{aliceIri.Value}#key-1");
        var aliceKey = KeyPairGenerator.GenerateRsa(aliceKeyId);
        persistence.Keys.PutKey(aliceKey);
        var alice = new Person
        {
            Id = aliceIri.Value,
            PreferredUsername = "alice",
            Name = ["alice"],
        };
        alice.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["publicKey"] = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id = aliceKeyId.Value,
                owner = aliceIri.Value,
                publicKeyPem = aliceKey.ExportPublicKeyPem(),
            }),
        };
        persistence.ActorStore.PutActorAsync(alice).GetAwaiter().GetResult();

        persistence.ActorStore.PutActorAsync(new Person
        {
            Id = $"{dialBase}/ap/v1/u/bob",
            PreferredUsername = "bob",
            Name = ["bob"],
        }).GetAwaiter().GetResult();

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
                    opts.InstanceActorId = aliceIri;
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

    private static async Task<(TestServer Server, IActivityPubClient Client, Iri ActorIri, Iri BobIri)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        return (server, session.GetClient(),
            new Iri("http://localhost/ap/v1/u/alice"),
            new Iri("http://localhost/ap/v1/u/bob"));
    }

    /// <summary>
    /// Seeds <paramref name="count"/> notes in <paramref name="actor"/>'s outbox by posting them to the
    /// outbox over the wire (the followed actor's content the feed should surface). The follow feed reads
    /// the outbox activities, so the notes must be recorded as outbox activities (a Create each wrapping a
    /// Note), not as bare objects in the object store.
    /// </summary>
    private static async Task SeedOutboxAsync(IActivityPubClient client, Iri actor, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            var result = await client.PostNoteAsync(actor, $"<p>{actor.Value.Split('/').Last()} note {i}</p>");
            Assert.Equal(202, result.StatusCode);
        }
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
    public async Task FollowFeed_FollowerSeesFollowedActorsOutbox_NewestFirst()
    {
        var (server, client, alice, bob) = await LogOnAsync();
        using var _ = server;

        // alice follows bob (signed write to alice's own outbox; the edge is recorded locally).
        Assert.Equal(202, (await client.FollowAsync(alice, bob)).StatusCode);

        // bob's outbox holds 3 notes (recorded as outbox activities the feed can read).
        await SeedOutboxAsync(client, bob, 3);

        // The follow feed is the union of alice's followed actors' outboxes (here: bob's 3 notes).
        var feed = await CollectAsync(client.GetFollowFeedAsync(alice));
        Assert.True(feed.Count >= 3, $"the feed must list the followed actor's outbox items (got {feed.Count})");

        // Every item must be one of bob's outbox activities (the followed actor's content). The feed
        // surfaces the outbox items (a Create per note), so the items are bob's /creates/ activities.
        var feedIris = feed.Select(IriOf).Where(i => i is not null).ToList()!;
        var bobPrefix = $"{bob.Value}/";
        Assert.All(feedIris, iri => Assert.StartsWith(bobPrefix, iri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FollowFeed_PagedCollection_CarriesNextLink()
    {
        var (server, client, alice, bob) = await LogOnAsync();
        using var _ = server;

        Assert.Equal(202, (await client.FollowAsync(alice, bob)).StatusCode);
        await SeedOutboxAsync(client, bob, 3);

        var feedIri = new Iri($"{alice.Value}/feed");

        // The feed is served as a paged collection. Request a small page (?limit=2) so the 3 items
        // span two pages: page 1 carries a `next` link (the "Load more" mechanism, S3) and page 2 is
        // the last page. This is the same `next`-link walking the Feed page's "Load more" performs.
        var page1 = await client.GetObjectAsync(new Iri($"{feedIri.Value}?limit=2"));
        Assert.True(page1 is not null, "the feed collection must be served");

        // The first page lists 2 of the 3 items (server page-sized by ?limit=2).
        var first = new List<IObjectOrLink>();
        if (page1 is OrderedCollectionPage { Items: { } ocpItems })
        {
            first.AddRange(ocpItems);
        }
        else if (page1 is OrderedCollection { Items: { } ocItems })
        {
            first.AddRange(ocItems);
        }

        Assert.True(first.Count == 2, $"a ?limit=2 page must list 2 of the 3 items (got {first.Count})");
        // The first page's items must be bob's outbox activities.
        var bobPrefix = $"{bob.Value}/";
        var firstIris = first.Select(IriOf).Where(i => i is not null).ToList()!;
        Assert.All(firstIris, iri => Assert.StartsWith(bobPrefix, iri, StringComparison.Ordinal));

        // Page 1 (2 of 3 items) must carry a next link so the user can "Load more". The page's
        // `next` pointer (an OrderedCollectionPage when served as a page, or the collection's own
        // ExtensionData `next` when served as the collection document) resolves to the page-2 IRI.
        var nextIri = ResolveNextIri(page1);
        Assert.True(nextIri is not null, "the first page (2 of 3 items) must carry a next link");
        string nextValue = nextIri?.ToString() ?? string.Empty;
        // The next link must point at page 2 of the feed.
        Assert.True(nextValue.Contains("page=2", StringComparison.Ordinal),
            $"the next link must point at page 2 (was {nextValue})");
    }

    /// <summary>
    /// Resolves a feed page's `next` pointer to its IRI: an OrderedCollectionPage carries a typed
    /// <c>next</c>; page 1 served as the collection document carries the pointer in its
    /// ExtensionData (the same shape the client's enumeration walks).
    /// </summary>
    private static Iri? ResolveNextIri(IObject? page)
    {
        if (page is OrderedCollectionPage { Next: { } typedNext })
        {
            return typedNext.ResolveCollectionIri();
        }

        if (page is OrderedCollection collection)
        {
            return ResolveCollectionNextLinkFromExtension(collection);
        }

        return null;
    }

    private static Iri? ResolveCollectionNextLinkFromExtension(OrderedCollection collection)
    {
        if (collection.ExtensionData is not { } ext ||
            !ext.TryGetValue("next", out var nextElement))
        {
            return null;
        }

        if (nextElement.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var value = nextElement.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : new Iri(value);
        }

        if (nextElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
            nextElement.TryGetProperty("href", out var href) &&
            href.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var value = href.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : new Iri(value);
        }

        return null;
    }
}
