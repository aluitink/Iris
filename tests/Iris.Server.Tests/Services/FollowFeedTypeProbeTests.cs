using System.Text;
using Iris.Client;
using Iris.Client.Collections;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Persistance;
using Iris.Server.Security;
using Iris.Server.Services;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Services;

/// <summary>
/// 31.9 verification probe: a local alice follows a local carol whose outbox holds one item of every
/// feed-relevant activity type (Create, Announce, Like, Accept, Note). Probes the followed-feed endpoint
/// and the client's <c>GetFollowFeedAsync</c> enumeration under both the in-memory and the file-backed
/// persistence provider (the live sample's configuration), plus a remote-follow case (alice on A follows
/// rayven on B; B's outbox is fetched over the in-process wire) to confirm the followed feed returns the
/// remote followed's data.
/// </summary>
public sealed class FollowFeedTypeProbeTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Alice = "alice";
    private const string Carol = "carol";
    private const string Password = "iris-sample";

    private static readonly string[] ExpectedSuffixes =
    [
        "objects/n-2",
        "activities/accept-1",
        "activities/like-1",
        "activities/announce-1",
        "activities/create-1",
    ];

    // The B host (rayven) is shared by the remote-follow probe and kept alive for the test's whole
    // lifetime (held as a field, disposed in Dispose) so its in-process TestServer transport target
    // outlives the feed request — the same pattern as the federation multi-host suites.
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _bPersistence = new();

    public FollowFeedTypeProbeTests()
    {
        var (_, rayvenIri, _) = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Rayven);
        SeedRemoteOutbox(_bPersistence, rayvenIri);
        _b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Rayven,
            Persistence = _bPersistence,
        });
    }

    public void Dispose() => _b.Dispose();

    [Fact]
    public async Task Probe_InMemory_Endpoint_ReturnsAllActivityTypes()
    {
        using var host = ProbeHost.Create(new InMemoryPersistenceProvider());
        await AssertFeedAsync(host, useClient: false);
    }

    [Fact]
    public async Task Probe_InMemory_Client_ReturnsAllActivityTypes()
    {
        using var host = ProbeHost.Create(new InMemoryPersistenceProvider());
        await AssertFeedAsync(host, useClient: true);
    }

    [Fact]
    public async Task Probe_FileBacked_Endpoint_ReturnsAllActivityTypes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iris-feed-probe-" + Guid.NewGuid().ToString("n"));
        try
        {
            using var host = ProbeHost.Create(new FileBackedPersistenceProvider(directory));
            await AssertFeedAsync(host, useClient: false);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Probe_FileBacked_Client_ReturnsAllActivityTypes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iris-feed-probe-" + Guid.NewGuid().ToString("n"));
        try
        {
            using var host = ProbeHost.Create(new FileBackedPersistenceProvider(directory));
            await AssertFeedAsync(host, useClient: true);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task AssertFeedAsync(ProbeHost host, bool useClient)
    {
        var expected = ExpectedSuffixes.Select(suffix => $"{host.Carol.Value}/{suffix}").ToList();

        if (!useClient)
        {
            using var http = new HttpClient(host.Server.CreateHandler(), disposeHandler: false);
            var response = await http.GetAsync($"https://{Host}/ap/v1/u/{Alice}/feed?limit=10");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(expected, JsonDoc.ItemIdsOf(body));
            return;
        }

        using var client = new ActivityPubClient(
            new HttpClient(host.Server.CreateHandler(), disposeHandler: false),
            null,
            new Iris.Client.Collections.CollectionPageCache());
        var items = new List<string>();
        await foreach (var item in client.GetFollowFeedAsync(
            new Iri($"https://{Host}/ap/v1/u/{Alice}"), new CollectionQuery { Limit = 10 }))
        {
            items.Add(item switch
            {
                IObject { Id: { } id } => id,
                ILink { Href: { } href } => href.ToString(),
                _ => "(unrecognized)",
            });
        }

        Assert.Equal(expected, items);
    }

    [Fact]
    public async Task Probe_UiPath_CollectionIriWithQuery_ReturnsAllActivityTypes()
    {
        using var host = ProbeHost.Create(new InMemoryPersistenceProvider());
        var client = NewClient(host);
        try
        {
            // Feed.razor's exact call: GetCollectionAsync on {actor}/feed (here with ?q=), one page at a
            // time (PageSize 5), following NextPage — the PagedCollection.razor loop.
            var items = new List<string>();
            Iri? resume = new Iri($"{host.Alice.Value}/feed?q=note");
            for (var guard = 0; guard < 10 && resume is not null; guard++)
            {
                var current = resume.Value;
                await foreach (var page in client.GetCollectionAsync(current, new CollectionQuery(Limit: 5)))
                {
                    foreach (var item in page.Items)
                    {
                        items.Add(ItemIri(item));
                    }

                    resume = page.NextPage;
                    break;
                }
            }

            Assert.Equal(
                [
                    $"{host.Carol.Value}/objects/n-2",
                    $"{host.Carol.Value}/activities/accept-1",
                    $"{host.Carol.Value}/activities/like-1",
                    $"{host.Carol.Value}/activities/announce-1",
                    $"{host.Carol.Value}/activities/create-1",
                ],
                items);
        }
        finally
        {
            client.Dispose();
        }
    }

    // --- 31.9 live-repro topology: a LOCAL follower (alice, A) of a REMOTE followed (rayven, B). ---
    // The live bug (PLAN 31.9): alice's /feed returned only Announce/Like/Note — the remote followed's
    // Create + Accept activities were dropped from the union. The local-follow probes above all pass;
    // this isolates the REMOTE-follow path (the only untested one): rayven's outbox on B is fetched
    // over the wire by A's FeedService and must contribute every activity type it holds.

    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Rayven = "rayven";

    [Fact]
    public async Task Probe_RemoteFollow_ReturnsAllActivityTypes()
    {
        var (aliceIri, rayvenIri) = SeedRemoteGraph(out var aPersistence, out var aKey);

        using var a = CreateFollowFeedHost(aPersistence, aliceIri, aKey);
        var body = await FetchFeedAsync(a, limit: 10);

        // rayven's 5 items (newest first: Note, Accept, Like, Announce, Create) + alice's 0 = 5.
        var expected = new[]
        {
            $"{rayvenIri.Value}/objects/n-2",
            $"{rayvenIri.Value}/activities/accept-1",
            $"{rayvenIri.Value}/activities/like-1",
            $"{rayvenIri.Value}/activities/announce-1",
            $"{rayvenIri.Value}/activities/create-1",
        };
        Assert.Equal(expected, JsonDoc.ItemIdsOf(body));
    }

    private (Iri Alice, Iri Rayven) SeedRemoteGraph(
        out InMemoryPersistenceProvider a, out KeyPair aKey)
    {
        a = new InMemoryPersistenceProvider();
        var (aliceKey, aliceIri, _) = TestSeeder.SeedPersonWithKey(a, AHost, Alice);
        var (_, rayvenIri, _) = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Rayven);

        // alice (A) follows rayven (B) — a remote follow.
        a.Follows.RecordFollowAsync(aliceIri, rayvenIri).GetAwaiter().GetResult();

        aKey = aliceKey;
        return (aliceIri, rayvenIri);
    }

    private static void SeedRemoteOutbox(InMemoryPersistenceProvider persistence, Iri rayven)
    {
        var iri = rayven.Value;
        var uri = new Uri(iri);
        var note = new Note { Id = $"{iri}/objects/n-1", Content = ["remote note"] };

        persistence.Activities.AddToOutboxAsync(rayven, new Create
        {
            Id = $"{iri}/activities/create-1",
            Actor = [new Link { Href = uri }],
            Object = [note],
        }).GetAwaiter().GetResult();

        persistence.Activities.AddToOutboxAsync(rayven, new Announce
        {
            Id = $"{iri}/activities/announce-1",
            Actor = [new Link { Href = uri }],
            Object = [new Link { Href = new Uri($"{iri}/objects/remote-1") }],
        }).GetAwaiter().GetResult();

        persistence.Activities.AddToOutboxAsync(rayven, new Like
        {
            Id = $"{iri}/activities/like-1",
            Actor = [new Link { Href = uri }],
            Object = [new Link { Href = new Uri($"{iri}/objects/other-1") }],
        }).GetAwaiter().GetResult();

        persistence.Activities.AddToOutboxAsync(rayven, new Accept
        {
            Id = $"{iri}/activities/accept-1",
            Actor = [new Link { Href = uri }],
            Object = [new Link { Href = new Iri($"https://{AHost}/ap/v1/u/{Alice}").Uri }],
        }).GetAwaiter().GetResult();

        persistence.Activities.AddToOutboxAsync(rayven, new Note
        {
            Id = $"{iri}/objects/n-2",
            Content = ["bare remote note"],
        }).GetAwaiter().GetResult();
    }

    private static async Task<string> FetchFeedAsync(TestServer a, int limit)
    {
        using var http = new HttpClient(a.CreateHandler(), disposeHandler: false);
        var response = await http.GetAsync($"https://{AHost}/ap/v1/u/{Alice}/feed?limit={limit}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Builds the A host (alice) wired to reach the shared B host (rayven) over the in-process
    /// TestServer transport, and returns the A host (the test disposes it with <c>using</c>; B is a
    /// field disposed by the test class). The production registration hardcodes a real <see
    /// cref="HttpClientHandler"/> (which cannot reach an in-process TestServer), so the feed client +
    /// document fetcher are overlaid onto B's handler; alice signs as herself so the outbound requests
    /// carry a valid signature.
    /// </summary>
    private TestServer CreateFollowFeedHost(
        InMemoryPersistenceProvider aPersistence,
        Iri aliceIri,
        KeyPair aKey)
    {
        // A's outbound client signs as alice and reaches B in-process (B is the shared field host).
        var bHandler = _b.CreateHandler();
        var aKeyStore = new InMemoryKeyStore();
        aKeyStore.PutKey(aKey);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        aKeyProvider.RegisterKey(aliceIri, new Iri($"{aliceIri.Value}#key-1"));
        var aSigner = new HttpSignatureSigner(aKeyStore);
        var bWiredClientFactory = new ActivityPubClientFactory(aKeyStore, aKeyProvider, aSigner);

        var a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = aPersistence,
            RegisterLocalKey = false,
            ExtraServices = s =>
            {
                var bClient = bWiredClientFactory.Create(
                    new ActivityPubClientOptions { ActorId = aliceIri, EnableRetry = false },
                    bHandler);
                s.AddSingleton<IActorDocumentFetcher>(sp => new IrisActorDocumentFetcher(
                    bClient, sp.GetRequiredService<RemoteActorCache>()));
                s.AddSingleton<IFollowFeedService>(sp => new FeedService(
                    sp.GetRequiredService<IPersistenceProvider>(),
                    sp.GetRequiredService<ILocalActorResolver>(),
                    sp.GetRequiredService<IActorDocumentFetcher>(),
                    bClient,
                    sp.GetRequiredService<IOptions<FeedOptions>>()));
            },
        });

        return a;
    }

    private static ActivityPubClient NewClient(ProbeHost host)
        => new(new HttpClient(host.Server.CreateHandler(), disposeHandler: false), null, new Iris.Client.Collections.CollectionPageCache());

    private static string ItemIri(IObjectOrLink item)
        => item switch
        {
            IObject { Id: { } id } => id,
            ILink { Href: { } href } => href.ToString(),
            _ => "(unrecognized)",
        };

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// The probe host: a real <see cref="TestServer"/> (via <see cref="ActivityPubHostFactory"/>)
    /// seeded with alice following carol, whose outbox holds one item of every feed-relevant activity
    /// type. The given <see cref="IPersistenceProvider"/> is bound as the server's persistence
    /// aggregate (overlaid via the factory's extra-service escape hatch, so it wins for
    /// <c>GetService&lt;IPersistenceProvider&gt;</c>); the factory's key seam is an empty key store
    /// (the feed endpoints perform no outbound delivery, so no signing is needed).
    /// </summary>
    private sealed class ProbeHost : IDisposable
    {
        public TestServer Server { get; }
        public Iri Alice { get; }
        public Iri Carol { get; }

        private ProbeHost(TestServer server, Iri alice, Iri carol)
        {
            Server = server;
            Alice = alice;
            Carol = carol;
        }

        public static ProbeHost Create(IPersistenceProvider persistence)
        {
            var alice = SeedActor(persistence, "alice");
            var carol = SeedActor(persistence, "carol");
            persistence.Follows.RecordFollowAsync(alice, carol).GetAwaiter().GetResult();
            SeedOutbox(persistence, carol);

            var options = new ActivityPubHostOptions
            {
                Host = Host,
                Handle = "alice",
                Persistence = new InMemoryPersistenceProvider(),
                CredentialValidator = new BasicAuthCredentialValidator((_, username, password) =>
                {
                    var valid = username == "alice" &&
                        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
                    return new ValueTask<bool>(valid);
                }),
                ExtraServices = services =>
                {
                    services.AddSingleton<IPersistenceProvider>(persistence);
                    services.AddSingleton<IKeyStore>(new InMemoryKeyStore());
                },
            };

            return new ProbeHost(ActivityPubHostFactory.Create(options), alice, carol);
        }

        public void Dispose() => Server.Dispose();

        private static Iri SeedActor(IPersistenceProvider persistence, string handle)
        {
            var actorIri = new Iri($"https://{Host}/ap/v1/u/{handle}");
            persistence.Actors.PutActorAsync(new Person
            {
                Id = actorIri.Value,
                PreferredUsername = handle,
                Name = [handle],
            }).GetAwaiter().GetResult();
            return actorIri;
        }

        private static void SeedOutbox(IPersistenceProvider persistence, Iri carol)
        {
            var carolIri = carol.Value;
            var carolUri = new Uri(carolIri);
            var note = new Note { Id = $"{carolIri}/objects/n-1", Content = ["probe note"] };

            persistence.Activities.AddToOutboxAsync(carol, new Create
            {
                Id = $"{carolIri}/activities/create-1",
                Actor = [new Link { Href = carolUri }],
                Object = [note],
            }).GetAwaiter().GetResult();

            persistence.Activities.AddToOutboxAsync(carol, new Announce
            {
                Id = $"{carolIri}/activities/announce-1",
                Actor = [new Link { Href = carolUri }],
                Object = [new Link { Href = new Uri($"{carolIri}/objects/remote-1") }],
            }).GetAwaiter().GetResult();

            persistence.Activities.AddToOutboxAsync(carol, new Like
            {
                Id = $"{carolIri}/activities/like-1",
                Actor = [new Link { Href = carolUri }],
                Object = [new Link { Href = new Uri($"{carolIri}/objects/other-1") }],
            }).GetAwaiter().GetResult();

            persistence.Activities.AddToOutboxAsync(carol, new Accept
            {
                Id = $"{carolIri}/activities/accept-1",
                Actor = [new Link { Href = carolUri }],
                Object = [new Link { Href = new Iri($"https://{Host}/ap/v1/u/alice").Uri }],
            }).GetAwaiter().GetResult();

            persistence.Activities.AddToOutboxAsync(carol, new Note
            {
                Id = $"{carolIri}/objects/n-2",
                Content = ["bare note"],
            }).GetAwaiter().GetResult();
        }
    }
}
