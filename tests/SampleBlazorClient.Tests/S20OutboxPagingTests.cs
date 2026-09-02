using Iris.Client;
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 20.3 tests: the actor-detail "Outbox" surface — a user can enumerate a local or remote
/// actor's outbox and page through it (the paged collection with <c>next</c>-link walking), each item
/// rendered as a navigable object view. These tests exercise the outbox the same way the page's
/// "Outbox" surface does: log on, seed the actor's outbox with enough notes to span more than one
/// page, and assert the paged <c>GetCollectionAsync</c> (the surface's enumeration, with the "Load
/// more" button walking <c>next</c> links via <c>CollectionPage.NextPage</c>) surfaces every item
/// page-by-page with no duplicates. This makes 20.1's "outbox = source of truth" visible in the UI.
/// </summary>
public sealed class S20OutboxPagingTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// The server's default collection page size (the <c>?limit=</c> default the outbox endpoint serves
    /// at). The actor-detail "Outbox" surface pages at this natural size (it does not override it), so a
    /// single "Load more" click appends one server page of this many items. The tests seed more items
    /// than this so the outbox spans more than one page (making <c>next</c>-link walking observable).
    /// </summary>
    private const int ServerPageSize = 20;

    /// <summary>
    /// Hosts a real <see cref="Iris.Server"/> ActivityPub pipeline (in-memory) with <c>alice</c> and
    /// <c>bob</c> seeded at the dial base. Mirrors <see cref="S3FollowFeedTests.StartHost"/>.
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

    private static async Task<(TestServer Server, IActivityPubClient Client, Iri AliceIri, Iri BobIri)> LogOnAsync()
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
    /// outbox over the wire (the actor's authored content the outbox surface should surface). The notes
    /// are recorded as outbox activities (a Create each wrapping a Note), so the outbox collection lists
    /// them.
    /// </summary>
    private static async Task SeedOutboxAsync(IActivityPubClient client, Iri actor, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            var result = await client.PostNoteAsync(actor, $"<p>{actor.Value.Split('/').Last()} note {i}</p>");
            Assert.Equal(202, result.StatusCode);
        }
    }

    /// <summary>
    /// Enumerates the actor's outbox exactly the way the actor-detail "Outbox" surface does: the paged
    /// <c>GetCollectionAsync</c> (one page per "Load more" click, walking <c>next</c> links via
    /// <c>CollectionPage.NextPage</c>), collecting every item across pages. Returns the flat item list
    /// and the per-page boundaries observed.
    /// </summary>
    private static async Task<(IReadOnlyList<IObjectOrLink> Items, int PageCount, bool LastPageIsLast)>
        EnumerateOutboxPagedAsync(IActivityPubClient client, Iri actor)
    {
        var items = new List<IObjectOrLink>();
        var pageCount = 0;
        var lastPageIsLast = false;

        // First load: start at the outbox's first page (no CollectionQuery — the server pages naturally).
        await foreach (var page in client.GetCollectionAsync(actor.OutboxOf(), null, CancellationToken.None))
        {
            items.AddRange(page.Items);
            pageCount++;
            lastPageIsLast = page.IsLastPage;
            if (page.IsLastPage)
            {
                return (items, pageCount, lastPageIsLast);
            }

            // "Load more": resume from the next page (one page per click).
            var resumeFrom = page.NextPage;
            while (!lastPageIsLast && resumeFrom is { } next)
            {
                CollectionPage? nextPage = null;
                await foreach (var p in client.GetCollectionAsync(next, null, CancellationToken.None))
                {
                    nextPage = p;
                    break;
                }

                Assert.True(nextPage is not null, "resuming from a next link must serve a page");
                items.AddRange(nextPage!.Items);
                pageCount++;
                lastPageIsLast = nextPage.IsLastPage;
                resumeFrom = nextPage.NextPage;
            }

            break;
        }

        return (items, pageCount, lastPageIsLast);
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
    public async Task Outbox_PagedEnumeration_WalksNextLinks_AllItemsSurface()
    {
        var (server, client, alice, _) = await LogOnAsync();
        using var _ = server;

        // alice's outbox holds 25 notes (server page size 20 -> two pages: 20 + 5). The surface must
        // walk the `next` link to surface all 25, exactly as the "Load more" button does.
        await SeedOutboxAsync(client, alice, ServerPageSize + 5);

        var (items, pageCount, lastPageIsLast) = await EnumerateOutboxPagedAsync(client, alice);

        // Every seeded note surfaced, no duplicates.
        Assert.Equal(ServerPageSize + 5, items.Count);
        var iriSet = items.Select(IriOf).Where(i => i is not null).Distinct().ToList()!;
        Assert.Equal(ServerPageSize + 5, iriSet.Count);

        // The items are alice's own outbox activities (the outbox = source of truth, 20.1).
        var alicePrefix = $"{alice.Value}/";
        Assert.All(iriSet, iri => Assert.StartsWith(alicePrefix, iri, StringComparison.Ordinal));

        // The enumeration spanned two server pages (20 + 5) and the final page was the last page.
        Assert.Equal(2, pageCount);
        Assert.True(lastPageIsLast, "the final page must be the last page (no further next link)");
    }

    [Fact]
    public async Task Outbox_FirstPage_CarriesNextLinkWhenMoreRemain()
    {
        var (server, client, alice, _) = await LogOnAsync();
        using var _ = server;

        // 25 notes at the server's page size (20): the first page lists 20 and must carry a next link
        // (the "Load more" mechanism). This is the exact page-boundary the actor-detail "Outbox"
        // surface's "Load more" button relies on (it resumes from CollectionPage.NextPage).
        await SeedOutboxAsync(client, alice, ServerPageSize + 5);

        CollectionPage? page1 = null;
        await foreach (var page in client.GetCollectionAsync(alice.OutboxOf(), null, CancellationToken.None))
        {
            page1 = page;
            break;
        }

        Assert.True(page1 is not null, "the outbox must be served as a paged collection");
        Assert.True(page1!.Items.Count == ServerPageSize,
            $"a {ServerPageSize}-item server page of a 25-item outbox must list {ServerPageSize} (got {page1!.Items.Count})");
        Assert.True(!page1.IsLastPage, "the first page (20 of 25) must not be the last page");
        Assert.True(page1.NextPage is not null, "the first page (20 of 25) must carry a next link");

        // Resume from the next link: page 2 holds the remaining 5 and is the last page.
        CollectionPage? page2 = null;
        await foreach (var page in client.GetCollectionAsync(page1!.NextPage!.Value, null, CancellationToken.None))
        {
            page2 = page;
            break;
        }

        Assert.True(page2 is not null, "resuming from the next link must serve page 2");
        Assert.True(page2!.Items.Count == 5, $"page 2 must hold the remaining 5 items (got {page2!.Items.Count})");
        Assert.True(page2.IsLastPage, "page 2 (the last of 25 items) must be the last page");
        Assert.True(page2.NextPage is null, "the last page must carry no next link");
    }

    [Fact]
    public async Task Outbox_SinglePage_NoNextLink()
    {
        // A small outbox (fewer items than the server page size) is a single page: the first page is
        // the last page (no "Load more" needed). This is the common case the surface handles (e.g. a
        // new user with a handful of notes).
        var (server, client, alice, _) = await LogOnAsync();
        using var _ = server;

        await SeedOutboxAsync(client, alice, 3);

        CollectionPage? page1 = null;
        await foreach (var page in client.GetCollectionAsync(alice.OutboxOf(), null, CancellationToken.None))
        {
            page1 = page;
            break;
        }

        Assert.True(page1 is not null, "the outbox must be served");
        Assert.True(page1!.Items.Count == 3, $"a 3-item outbox must list all 3 on one page (got {page1!.Items.Count})");
        Assert.True(page1.IsLastPage, "a single-page outbox must be the last page");
        Assert.True(page1.NextPage is null, "a single-page outbox must carry no next link");
    }

    [Fact]
    public async Task Outbox_PagedEnumeration_SecondLocalActor_AlsoPaged()
    {
        // 20.3's "local or remote" requirement: the paging contract is host-agnostic — the surface
        // pages whatever outbox it is pointed at, local or remote. bob (a second actor on the same
        // instance) is paged identically to alice: the same GetCollectionAsync + `next`-link walking
        // drives it, with the same server page size.
        var (server, client, _, bob) = await LogOnAsync();
        using var _ = server;

        await SeedOutboxAsync(client, bob, ServerPageSize + 6);

        var (items, pageCount, lastPageIsLast) = await EnumerateOutboxPagedAsync(client, bob);

        Assert.Equal(ServerPageSize + 6, items.Count);
        var iriSet = items.Select(IriOf).Where(i => i is not null).Distinct().ToList()!;
        Assert.Equal(ServerPageSize + 6, iriSet.Count);
        var bobPrefix = $"{bob.Value}/";
        Assert.All(iriSet, iri => Assert.StartsWith(bobPrefix, iri, StringComparison.Ordinal));
        // 26 items at server page size 20 -> two pages (20 + 6).
        Assert.Equal(2, pageCount);
        Assert.True(lastPageIsLast);
    }
}
