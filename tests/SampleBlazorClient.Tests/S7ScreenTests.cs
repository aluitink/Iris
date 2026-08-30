using Iris.Client;
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

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 8 S7 tests: the explorer's write screens. Each write the screens perform is exercised in-process
/// against a live <see cref="SampleServer"/> (TestServer), exactly as the screens issue it through
/// <c>ExplorerSession.GetClient()</c>: compose (<c>PostNoteAsync</c> / <c>PostReplyAsync</c>), like
/// (<c>LikeAsync</c>), and follow / un-follow (<c>FollowAsync</c> / <c>UndoFollowAsync</c>). The last two
/// are also driven over a genuine two-instance federation (A signed as its own actor → B's inbox, with
/// cross-instance key resolution) to cover the slice's federated-write requirement.
/// </summary>
public sealed class S7ScreenTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// Hosts a real <see cref="Iris.Server"/> ActivityPub pipeline (in-memory) for the write screens.
    /// Alice and bob are seeded at the <em>dial base</em> (<c>http://localhost/ap/v1/u/…</c>), the host
    /// the <see cref="TestServer"/> transport dials in-process: the Basic-auth logon fetches the
    /// owner-only document at the dial-base IRI, loads the dial-base key, and the signed client signs
    /// every write as the dial-base actor — so the signature, the activity's body <c>actor</c>, and the
    /// key id all agree on the dial-base IRI. Object/follow targets are also dial-base IRIs. An inbound
    /// <see cref="IActorDocumentFetcher"/> serves actor documents straight from the in-process
    /// persistence, so the inbound key resolver verifies the signature by reading the actor's
    /// <c>publicKey</c>.
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

        // Built by hand (rather than ActivityPubHostFactory) so BaseUri is the dial base — the host the
        // actor-document handler resolves the requesting actor IRI from. That makes the Basic-auth logon
        // (dial-base actor IRI), the signed writes (signed as the dial-base actor), and the activity body
        // actor all agree on one IRI, so the inbound key resolver verifies the signature by reading the
        // actor document's publicKey.
        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
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

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that serves an actor's document directly from the
    /// in-process persistence (no network), so the inbound key resolver verifies the signature by
    /// reading the actor's <c>publicKey</c>.
    /// </summary>
    private sealed class PersistenceActorFetcher(IPersistenceProvider persistence) : IActorDocumentFetcher
    {
        private readonly IPersistenceProvider _persistence = persistence;

        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => await _persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct)
                ? actor
                : null;
    }

    /// <summary>
    /// Logs on as the dial-base <c>alice</c> and returns the signed client plus alice's dial-base actor
    /// IRI (the IRI every write is addressed to and signed as — the logon, signature, and body actor
    /// all agree on it).
    /// </summary>
    private static async Task<(TestServer Server, IActivityPubClient Client, Iri ActorIri)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        return (server, session.GetClient(), new Iri("http://localhost/ap/v1/u/alice"));
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

    private static async Task<string> ContentOfAsync(IActivityPubClient client, Iri objectIri)
    {
        var obj = await client.GetObjectAsync(objectIri);
        return obj?.Content?.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// Resolves a collection item's IRI the same way the screens do (<c>ObjectView</c>): an
    /// <see cref="IObject"/> carries it in <c>Id</c>; an <see cref="ILink"/> in <c>Href</c>.
    /// </summary>
    private static Iri? IriOf(IObjectOrLink item)
    {
        if (item is IObject { Id: { } id })
        {
            return new Iri(id);
        }

        if (item is ILink { Href: { } href })
        {
            return new Iri(href);
        }

        return null;
    }

    // --- Compose: post a note ----------------------------------------------------

    [Fact]
    public async Task Compose_PostNote_SurfacesInActorOutboxAndObjectView()
    {
        var (server, client, actorIri) = await LogOnAsync();
        using var _ = server;

        var content = "<p>S7: a note from the compose screen.</p>";
        var status = await client.PostNoteAsync(actorIri, content);
        Assert.Equal(202, status);

        // The note is stored as a fetchable content object (the object view loads it by IRI).
        var objects = await server.Services.GetRequiredService<IPersistenceProvider>().Objects.ListObjectsAsync();
        var posted = objects.FirstOrDefault(o => o.Content?.FirstOrDefault() == content);
        Assert.NotNull(posted);
        var noteIri = new Iri(posted!.Id!);
        Assert.Equal(content, await ContentOfAsync(client, noteIri));

        // The note appears in the author's outbox (the actor detail screen's feed): the outbox lists the
        // post's Create, whose IRI derives from the note IRI (same content hash). The object view renders
        // each outbox item by its IRI; the posted note itself is fetchable by IRI (asserted above).
        var outbox = await CollectAsync(client.GetCollectionItemsAsync(actorIri.OutboxOf()));
        Assert.Contains(outbox, o => IriOf(o) is { } iri && iri.Value.StartsWith($"{actorIri.Value}/creates/"));
    }

    // --- Compose: post a reply ---------------------------------------------------

    [Fact]
    public async Task Compose_PostReply_SurfacesUnderParentReplies()
    {
        var (server, client, actorIri) = await LogOnAsync();
        using var _ = server;

        // Seed a parent note so the reply threads under it (the object view's thread).
        var parent = new Iri($"{actorIri.Value}/notes/1");
        await server.Services.GetRequiredService<IPersistenceProvider>().Objects.PutObjectAsync(new Note
        {
            Id = parent.Value,
            AttributedTo = [new Link { Href = actorIri.Uri }],
            Content = ["<p>parent</p>"],
        });
        var status = await client.PostReplyAsync(
            actorIri, parent, "<p>S7: a reply from the compose screen.</p>", to: [Iri.Public]);
        Assert.Equal(202, status);

        // The reply is stored and lists under the parent's replies collection (the object view's thread).
        // The replies surface items as links (the object view renders each by Href); the reply's content
        // is fetchable by its IRI.
        var replies = await CollectAsync(client.GetRepliesAsync(parent));
        var expected = "<p>S7: a reply from the compose screen.</p>";
        var replyIris = replies
            .Select(r => IriOf(r))
            .Where(i => i is not null)
            .Select(i => i!.Value)
            .ToList();
        var matching = await Task.WhenAll(replyIris.Select(async r =>
            (r, match: (await ContentOfAsync(client, r)) == expected)));
        Assert.True(matching.Any(m => m.match), $"a reply with the posted content must list under the parent (replies: {replyIris.Count})");
    }

    // --- Like --------------------------------------------------------------------

    [Fact]
    public async Task ObjectLike_Like_SurfacesInLikersLikedCollection()
    {
        var (server, client, actorIri) = await LogOnAsync();
        using var _ = server;

        // Seed a target note (the object alice likes).
        var bob = new Iri("http://localhost/ap/v1/u/bob");
        var target = new Iri($"{bob.Value}/notes/1");
        await server.Services.GetRequiredService<IPersistenceProvider>().Objects.PutObjectAsync(new Note
        {
            Id = target.Value,
            AttributedTo = [new Link { Href = bob.Uri }],
            Content = ["<p>a note to like</p>"],
        });
        var status = await client.LikeAsync(actorIri, target);
        Assert.Equal(202, status);

        // The like is stored and the liker's `liked` collection lists the liked object's IRI.
        var activity = await server.Services.GetRequiredService<IPersistenceProvider>()
            .Activities.TryGetActivityAsync(new Iri($"{actorIri.Value}/likes/{target.Value}"), out var stored);
        Assert.True(activity, "the like activity must be stored on the receiving instance");
        Assert.NotNull(stored);

        // The liker's `liked` collection lists the liked object's IRI (as a link — the object view
        // renders it by Href).
        var liked = await CollectAsync(client.GetCollectionItemsAsync(actorIri.LikedOf()));
        Assert.Contains(liked, o => IriOf(o) is { } iri && iri == target);
    }

    // --- Follow / un-follow (local single instance) ------------------------------

    [Fact]
    public async Task ActorFollow_Follow_SurfacesInFollowersCollection()
    {
        var (server, client, follower) = await LogOnAsync();
        using var _ = server;

        var target = new Iri("http://localhost/ap/v1/u/bob");
        var status = await client.FollowAsync(follower, target);
        Assert.Equal(202, status);

        // The follow edge is recorded: bob's followers collection lists alice (by IRI).
        var followers = await CollectAsync(client.GetCollectionItemsAsync(target.FollowersOf()));
        Assert.Contains(followers, o => IriOf(o) is { } iri && iri == follower);
    }

    [Fact]
    public async Task ActorUnfollow_AfterFollow_RemovesFollowEdge()
    {
        var (server, client, follower) = await LogOnAsync();
        using var _ = server;

        var target = new Iri("http://localhost/ap/v1/u/bob");
        Assert.Equal(202, await client.FollowAsync(follower, target));

        // The un-follow is an Undo delivered to the follower's own inbox; the receiver resolves the
        // original Follow (stored when alice sent it) and removes the recorded edge.
        Assert.Equal(202, await client.UndoFollowAsync(follower, target));

        var followers = await CollectAsync(client.GetCollectionItemsAsync(target.FollowersOf()));
        Assert.DoesNotContain(followers, o => IriOf(o) is { } iri && iri == follower);
    }

    // --- Follow / un-follow (two-instance federation) ----------------------------

    [Fact]
    public async Task ActorFollow_FederatedAcrossInstances_RecordsEdgeOnTarget()
    {
        // A (host a.example, actor alice) follows B (host b.example, actor bob) over the wire. Per the
        // delivery model, the client publishes the authored Follow to alice's OWN outbox — which lives on
        // A (alice's home instance) — NOT to bob's inbox. A records the Follow in alice's outbox and then
        // (the server's job) delivers it to bob's inbox on B; B's inbound key resolver verifies the
        // signature by fetching alice's actor document from A. The un-follow is an Undo published to
        // alice's own outbox (A) likewise; A resolves the stored Follow and removes the edge, and the
        // server delivers the Undo to bob's inbox on B.
        const string AHost = "a.example";
        const string BHost = "b.example";
        var aPersistence = new Iris.Server.InMemory.InMemoryPersistenceProvider();
        var bPersistence = new Iris.Server.InMemory.InMemoryPersistenceProvider();
        var (aliceKey, aliceActorIri, _) = TestSeeder.SeedPersonWithKey(aPersistence, AHost, "alice");
        var (bobKey, bobActorIri, _) = TestSeeder.SeedPersonWithKey(bPersistence, BHost, "bob");

        // Each instance's inbound key resolver resolves a signing actor's public key by fetching the
        // actor's document, and each instance's outbound DeliveryWorker delivers to the other instance's
        // inbox. A's TestServer does not yet exist while A is being constructed, so both the fetcher and
        // the delivery transport are deferred: they capture a reference to A that is filled in once A is
        // built (the LazyHandler resolves it on first use, after construction completes).
        TestServer? aRef = null;
        TestServer? bRef = null;
        Func<HttpMessageHandler> aHandler = () => new LazyHandler(() => aRef!.CreateHandler());
        Func<HttpMessageHandler> bHandler = () => new LazyHandler(() => bRef!.CreateHandler());
        using var a = TestFederation.StartServer(AHost, "alice", aPersistence, aliceKey,
            new RemoteDocumentFetcher(aHandler, AHost), bHandler);
        aRef = a;
        using var b = TestFederation.StartServer(BHost, "bob", bPersistence, bobKey,
            new RemoteDocumentFetcher(aHandler, AHost), aHandler);
        bRef = b;

        // A client signed as alice (A's key), routed to A — alice's home instance — because her authored
        // activities are published to her OWN outbox (which lives on A), never to a recipient's inbox.
        var keyStore = new Iris.Core.Identity.InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        var keyProvider = new Iris.Client.Auth.InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(aliceActorIri, aliceKey.KeyId);
        var signer = new Iris.Core.Signing.HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var toA = factory.Create(new ActivityPubClientOptions { ActorId = aliceActorIri, EnableRetry = false }, a.CreateHandler());
        using var _ = toA;

        // alice follows bob: the Follow is published to alice's own outbox (A); A records it in its own
        // follow store (the actor's `following` collection lists even a remote target) and the server
        // delivers it to bob's inbox (B), where B records the follow edge.
        Assert.Equal(202, await toA.FollowAsync(aliceActorIri, bobActorIri));
        Assert.True(
            await aPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            "after a federated Follow, alice must follow bob in A's follow store (her own outbox)");

        // The server→server delivery of the Follow to bob's inbox (B) is asynchronous (the DeliveryWorker
        // pumps the queue), so poll for B's recorded edge.
        await TestFederation.WaitForAsync(
            () => bPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            TimeSpan.FromSeconds(5));
        Assert.True(
            await bPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            "after a federated Follow, B must record the follow edge (server delivered it to bob's inbox)");

        // alice un-follows bob: the Undo is published to alice's own outbox (A); A resolves the stored
        // Follow (the same deterministic IRI FollowAsync used) and removes the edge.
        Assert.Equal(202, await toA.UndoFollowAsync(aliceActorIri, bobActorIri));
        var aliceFollowing = await aPersistence.Follows.GetFollowingAsync(aliceActorIri);
        Assert.DoesNotContain(bobActorIri, aliceFollowing);
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that resolves an actor document by fetching the actor from
    /// a specific in-process <see cref="TestServer"/> (the source instance that hosts the actor). Used to
    /// wire cross-instance key resolution in a two-instance federation test: B's resolver fetches alice's
    /// document from A. The handler is a deferred factory (a <see cref="LazyHandler"/>) so an instance's
    /// fetcher can reach its own (not-yet-constructed) TestServer.
    /// </summary>
    private sealed class RemoteDocumentFetcher(Func<HttpMessageHandler> handlerFactory, string host) : IActorDocumentFetcher
    {
        private readonly Func<HttpMessageHandler> _handlerFactory = handlerFactory;
        private readonly string _host = host;

        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            var uri = new Uri(actorIri.Value);
            var handler = _handlerFactory();
            using var http = new HttpClient(handler, disposeHandler: false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/activity+json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return ActivityJson.Deserialize<Actor>(body);
        }
    }
}
