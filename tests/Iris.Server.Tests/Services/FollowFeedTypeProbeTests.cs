using System.Text;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Collections;
using Iris.Core;
using Iris.Core.Collections;
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

        // The follow feed is owner-gated (139.2-s5c): only a signed request as the actor (alice) is
        // accepted. The signed client signs as alice; the server's signature validator resolves alice's
        // public key via the host's self-fetching IActorDocumentFetcher.
        using var client = host.CreateSignedClient();
        var items = new List<string>();
        if (!useClient)
        {
            var collection = await client.GetObjectAsync(
                new Iri($"https://{Host}/ap/v1/u/{Alice}/feed?limit=10"));
            Assert.NotNull(collection);
            items = CollectionPageFactory
                .ResolveCollectionItems((Collection)collection!)
                .Select(ItemIri)
                .ToList();
        }
        else
        {
            await foreach (var item in client.GetFollowFeedAsync(
                new Iri($"https://{Host}/ap/v1/u/{Alice}"), new CollectionQuery { Limit = 10 }))
            {
                items.Add(ItemIri(item));
            }
        }

        Assert.Equal(expected, items);
    }

    [Fact]
    public async Task Probe_UiPath_CollectionIriWithQuery_ReturnsAllActivityTypes()
    {
        using var host = ProbeHost.Create(new InMemoryPersistenceProvider());
        var client = host.CreateSignedClient();
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
        var items = await FetchFeedItemsAsync(a, aliceIri, aKey, limit: 10);

        // rayven's 5 items (newest first: Note, Accept, Like, Announce, Create) + alice's 0 = 5.
        var expected = new[]
        {
            $"{rayvenIri.Value}/objects/n-2",
            $"{rayvenIri.Value}/activities/accept-1",
            $"{rayvenIri.Value}/activities/like-1",
            $"{rayvenIri.Value}/activities/announce-1",
            $"{rayvenIri.Value}/activities/create-1",
        };
        Assert.Equal(expected, items);
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

    private static async Task<List<string>> FetchFeedItemsAsync(TestServer a, Iri aliceIri, KeyPair aliceKey, int limit)
    {
        // The follow feed is owner-gated (139.2-s5c): the request must be signed as alice. A's host is
        // wired with a self-fetching IActorDocumentFetcher, so the signature validator can resolve
        // alice's public key from A's own actor-document endpoint.
        using var client = NewSignedClient(a, aliceIri, aliceKey);
        var collection = await client.GetObjectAsync(
            new Iri($"https://{AHost}/ap/v1/u/{Alice}/feed?limit={limit}"));
        Assert.NotNull(collection);
        return CollectionPageFactory
            .ResolveCollectionItems((Collection)collection!)
            .Select(ItemIri)
            .ToList();
    }

    private static IActivityPubClient NewSignedClient(TestServer server, Iri actorIri, KeyPair actorKey)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(actorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, actorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            server.CreateHandler());
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

        // A's inbound signature validator resolves alice's public key by fetching A's OWN actor
        // document over the in-process wire (the production default routes to a real
        // HttpClientHandler that cannot reach a TestServer). The lazy handler defers the handler
        // lookup until the first fetch, when the TestServer already exists.
        TestServer? aServerRef = null;
        var selfHandlerFactory = () => aServerRef!.CreateHandler();
        var selfClient = bWiredClientFactory.Create(
            new ActivityPubClientOptions { ActorId = aliceIri, EnableRetry = false },
            new LazyHandler(selfHandlerFactory));

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
                    selfClient, sp.GetRequiredService<RemoteActorCache>()));
                s.AddSingleton<IFollowFeedService>(sp => new FeedService(
                    sp.GetRequiredService<IPersistenceProvider>(),
                    sp.GetRequiredService<ILocalActorResolver>(),
                    sp.GetRequiredService<IActorDocumentFetcher>(),
                    bClient,
                    sp.GetRequiredService<IOptions<FeedOptions>>()));
            },
        });
        aServerRef = a;

        return a;
    }

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
    /// <c>GetService&lt;IPersistenceProvider&gt;</c>). The follow feed is owner-gated (139.2-s5c), so
    /// alice is seeded with a real key and the host's <see cref="IActorDocumentFetcher"/> is wired to
    /// fetch the host's OWN actor documents over the in-process wire (the production default routes to
    /// a real <c>HttpClientHandler</c> that cannot reach a TestServer) — that is what lets the inbound
    /// signature validator resolve alice's public key for the owner gate.
    /// </summary>
    private sealed class ProbeHost : IDisposable
    {
        private readonly IActivityPubClientFactory _clientFactory;

        public TestServer Server { get; }
        public Iri Alice { get; }
        public Iri Carol { get; }
        public KeyPair AliceKey { get; }

        private ProbeHost(TestServer server, Iri alice, Iri carol, KeyPair aliceKey, IActivityPubClientFactory clientFactory)
        {
            Server = server;
            Alice = alice;
            Carol = carol;
            AliceKey = aliceKey;
            _clientFactory = clientFactory;
        }

        /// <summary>
        /// A signed <see cref="IActivityPubClient"/> that signs as alice and reaches this host's
        /// in-process <see cref="TestServer"/> (the follow feed is owner-gated, so the request must be
        /// signed as the actor).
        /// </summary>
        public IActivityPubClient CreateSignedClient()
            => _clientFactory.Create(
                new ActivityPubClientOptions { ActorId = Alice, EnableRetry = false },
                Server.CreateHandler());

        public static ProbeHost Create(IPersistenceProvider persistence)
        {
            var (aliceKey, alice, aliceKeyId) = SeedActorWithKey(persistence, "alice");
            var carol = SeedActor(persistence, "carol");
            persistence.Follows.RecordFollowAsync(alice, carol).GetAwaiter().GetResult();
            SeedOutbox(persistence, carol);

            // The host's self-fetching client: signed as alice, routed to this host's own TestServer
            // (which does not exist yet — the lazy handler defers the handler lookup until the first
            // fetch). Its publicKey (seeded by SeedPersonWithKey) is what the signature validator
            // verifies against.
            var keyStore = new InMemoryKeyStore();
            keyStore.PutKey(aliceKey);
            var keyProvider = new InMemoryKeyProvider(keyStore);
            keyProvider.RegisterKey(alice, aliceKey.KeyId);
            var signer = new HttpSignatureSigner(keyStore);
            var clientFactory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

            TestServer? serverRef = null;
            var selfClient = clientFactory.Create(
                new ActivityPubClientOptions { ActorId = alice, EnableRetry = false },
                new LazyHandler(() => serverRef!.CreateHandler()));

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
                Fetcher = new IrisActorDocumentFetcher(selfClient, new RemoteActorCache()),
            };

            var server = ActivityPubHostFactory.Create(options);
            serverRef = server;

            return new ProbeHost(server, alice, carol, aliceKey, clientFactory);
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

        private static (KeyPair Key, Iri ActorIri, Iri KeyId) SeedActorWithKey(
            IPersistenceProvider persistence, string handle)
        {
            var actorIriString = $"https://{Host}/ap/v1/u/{handle}";
            var actorIri = new Iri(actorIriString);
            var keyId = new Iri($"{actorIriString}#key-1");

            var key = KeyPairGenerator.GenerateRsa(keyId);
            persistence.Keys.PutKey(key);

            var actor = new Person
            {
                Id = actorIriString,
                PreferredUsername = handle,
                Name = [handle],
            };
            actor.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            actor.ExtensionData[ActivityPubExtensionNames.PublicKey] =
                System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    id = keyId.Value,
                    owner = actorIriString,
                    publicKeyPem = key.ExportPublicKeyPem(),
                });
            persistence.Actors.PutActorAsync(actor).GetAwaiter().GetResult();

            return (key, actorIri, keyId);
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
