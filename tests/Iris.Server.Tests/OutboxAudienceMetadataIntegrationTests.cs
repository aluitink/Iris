using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.6.5 — <strong>Audience metadata (the on-the-wire <c>to</c>/<c>cc</c> enumeration)</strong>:
/// when a local actor publishes an outbound <see cref="Create"/> or <see cref="Announce"/> to their own
/// outbox, the server rewrites the activity's audience so the federated document enumerates the actual
/// distribution list, not just the author's composed address. This is the production half that change
/// 158 scoped out (the delivery-recipients half is pinned in <c>OutboxAudienceMatchIntegrationTests</c>).
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local) hosts author <c>alice</c>; instance B (b.domain.local,
/// <c>bob</c>) hosts a <em>remote follower</em> (bob→alice recorded on A); instance C (c.domain.local,
/// <c>carol</c>) hosts a remote non-follower AND is the author of a parent note (stored on A's object
/// store) used for the reply test. Alice publishes, signed as alice, to her own outbox; A's server
/// rewrites the audience and delivers to bob's inbox. Assertions read the <em>federated</em> (stored on
/// B) activity's <c>to</c>/<c>cc</c>:
/// <list type="bullet">
/// <item>public <see cref="Create"/> → the Create's <c>cc</c> enumerates bob (the follower), the Create's
/// <c>to</c> keeps <c>as:Public</c>, and the embedded Note's own <c>to</c> still carries <c>as:Public</c>
/// (the Note is not rewritten).</item>
/// <item><see cref="Announce"/> (boost) → the Announce's <c>to</c> enumerates bob, its <c>cc</c> is alice
/// (the announcer) — mirroring the inbound boost convention.</item>
/// <item><see cref="Create"/> reply → the Create's <c>to</c> additionally carries the reply target (the
/// parent note's author, carol), in addition to <c>as:Public</c>; the <c>cc</c> still enumerates bob.</item>
/// </list>
/// </remarks>
public sealed class OutboxAudienceMetadataIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string CHost = "c.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Carol = "carol";

    /// <summary>The ActivityStreams public collection address (the conventional <c>to</c> for public notes).</summary>
    private static readonly Iri AsPublic = Iri.Public;

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly TestServer _c;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly InMemoryPersistenceProvider _cPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _carolActorIri;

    public OutboxAudienceMetadataIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();
        _cPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;

        var cSeeded = TestSeeder.SeedPersonWithKey(_cPersistence, CHost, Carol);
        _carolActorIri = cSeeded.ActorIri;

        // bob→alice is recorded on A (A is alice's home). carol does NOT follow alice (remote
        // non-follower).
        _aPersistence.Follows.RecordFollowAsync(_bobActorIri, _aliceActorIri).GetAwaiter().GetResult();

        _a = StartAuthorServer(_aPersistence, _aliceKey, _aliceActorIri,
            bServer: () => _b!, cServer: () => _c!, selfServer: () => _a!);
        _b = StartServer(BHost, Bob, _bPersistence, bSeeded.Key,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, _a.CreateHandler()));
        _c = StartServer(CHost, Carol, _cPersistence, cSeeded.Key,
            fetcher: BuildFetcherFor(CHost, Carol, cSeeded.Key, _a.CreateHandler()));
        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _c.Dispose();
    }

    // --- A public Create: cc enumerates the follower; to keeps as:Public; the Note is untouched ---

    [Fact]
    public async Task OutboxPublish_PublicCreate_FederatedCcEnumeratesFollower_ToKeepsAsPublic()
    {
        var create = BuildPublicCreate(_aliceActorIri);

        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        // bob (the follower, on B) received the federated Create (signed as alice).
        await WaitForAsync(
            async () => await _bPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out var storedB),
            "B (the follower) should have stored the Create federated by A's server");
        Assert.IsType<Create>(storedB);
        var federated = (Create)storedB;

        // 19.6.5: the federated Create's `cc` enumerates the follower set (bob) — the distribution list
        // is on the wire, not just implicit in the delivery.
        var ccHrefs = ActivityHrefs(federated, cc: true);
        Assert.Contains(_bobActorIri.Value, ccHrefs, StringComparer.OrdinalIgnoreCase);

        // The Create's `to` keeps the composed public address (as:Public).
        var toHrefs = ActivityHrefs(federated, cc: false);
        Assert.Contains(AsPublic.Value, toHrefs, StringComparer.OrdinalIgnoreCase);

        // The embedded Note is NOT rewritten — its own `to` still carries as:Public.
        var noteToHrefs = NoteToHrefsOf(federated);
        Assert.Contains(AsPublic.Value, noteToHrefs, StringComparer.OrdinalIgnoreCase);
    }

    // --- A boost: to enumerates the follower; cc is the announcer -------------------------------------

    [Fact]
    public async Task OutboxPublish_Announce_FederatedToEnumeratesFollower_CcIsAnnouncer()
    {
        var announce = BuildAnnounce(_aliceActorIri);

        using var request = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        await WaitForAsync(
            async () => await _bPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            timeout: TimeSpan.FromSeconds(15));
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out var storedB),
            "B (the follower) should have stored the Announce federated by A's server");
        Assert.IsType<Announce>(storedB);
        var federated = (Announce)storedB;

        // 19.6.5: the federated boost is addressed `to` each follower (bob) and carbon-copied `cc` to the
        // announcer (alice) — mirroring AnnounceIris.BuildAnnounce for the inbound path.
        var toHrefs = ActivityHrefs(federated, cc: false);
        Assert.Contains(_bobActorIri.Value, toHrefs, StringComparer.OrdinalIgnoreCase);

        var ccHrefs = ActivityHrefs(federated, cc: true);
        Assert.Contains(_aliceActorIri.Value, ccHrefs, StringComparer.OrdinalIgnoreCase);
    }

    // --- A reply: to additionally carries the reply target (the parent note's author) ----------------

    [Fact]
    public async Task OutboxPublish_ReplyCreate_FederatedToCarriesReplyTarget_CcEnumeratesFollower()
    {
        // Seed a parent note on A's object store authored by carol (the reply target). The parent IRI is
        // on A (the author's home instance) so the reply's GetParentIri resolves to it and the server can
        // read carol from the parent's attributedTo.
        var parentIri = new Iri($"https://{AHost}/objects/parent-{Guid.NewGuid():N}");
        await _aPersistence.Objects.PutObjectAsync(new Note
        {
            Id = parentIri.Value,
            Content = ["the parent note"],
            AttributedTo = [new Link { Href = new Uri(_carolActorIri.Value) }],
            To = [new Link { Href = new Uri(AsPublic.Value) }],
        });

        var replyCreate = BuildReplyCreate(_aliceActorIri, parentIri);

        using var request = SignedRequest(_aliceActorIri, _aliceKey, replyCreate, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        await WaitForAsync(
            async () => await _bPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out var storedB),
            "B (the follower) should have stored the reply Create federated by A's server");
        Assert.IsType<Create>(storedB);
        var federated = (Create)storedB;

        // 19.6.5: the reply's `to` carries the reply target (the parent note's author, carol) in addition
        // to the composed public address (as:Public).
        var toHrefs = ActivityHrefs(federated, cc: false);
        Assert.Contains(_carolActorIri.Value, toHrefs, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(AsPublic.Value, toHrefs, StringComparer.OrdinalIgnoreCase);

        // And the `cc` still enumerates the follower set (bob).
        var ccHrefs = ActivityHrefs(federated, cc: true);
        Assert.Contains(_bobActorIri.Value, ccHrefs, StringComparer.OrdinalIgnoreCase);
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on the
    /// author's outbox. Uses the client pipeline (via a capture handler) to produce a correctly signed
    /// request, then replays the signed headers onto a fresh request for delivery to A's TestServer.
    /// </summary>
    private HttpRequestMessage SignedRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClient(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{AHost}{path}")
                    {
                        Content = signedContent,
                    },
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            response.Dispose();
        }

        var captured = capture.Captured!;
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{AHost}{path}")
        {
            Content = content,
        };
        foreach (var (name, values) in captured.Headers)
        {
            if (string.Equals(name, "content-type", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "date", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in values)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (captured.Headers.TryGetValue("date", out var dateValues))
        {
            foreach (var value in dateValues)
            {
                request.Headers.TryAddWithoutValidation("date", value);
            }
        }

        return request;
    }

    private static IActivityPubClient BuildClient(Iri actorIri, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);
    }

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private static TestServer StartAuthorServer(
        InMemoryPersistenceProvider persistence, KeyPair authorKey, Iri authorActorIri,
        Func<TestServer> bServer, Func<TestServer> cServer, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(authorActorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            DeliveryTransport = () => new RoutingHandler(BHost, bServer, CHost, cServer),
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => selfServer().CreateHandler()),
                BHost, new LazyHandler(() => bServer().CreateHandler()),
                CHost, new LazyHandler(() => cServer().CreateHandler()),
                authorKey, authorActorIri),
        });
    }

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        KeyPair instanceKey, IActorDocumentFetcher? fetcher = null)
    {
        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
        });
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that routes to the B or C server based on the request's host
    /// header (A's delivery worker sends to the followers' inboxes, which are on different instances).
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly string _bHost;
        private readonly Func<TestServer> _bServer;
        private readonly string _cHost;
        private readonly Func<TestServer> _cServer;

        public RoutingHandler(string bHost, Func<TestServer> bServer, string cHost, Func<TestServer> cServer)
        {
            _bHost = bHost;
            _bServer = bServer;
            _cHost = cHost;
            _cServer = cServer;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var handler = host == _cHost ? _cServer().CreateHandler() : _bServer().CreateHandler();

            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (request.Content is { } content)
            {
                clone.Content = new ByteArrayContent(
                    content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return new HttpClient(handler, disposeHandler: false).SendAsync(clone, cancellationToken);
        }
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents
    /// based on the actor IRI's host.
    /// </summary>
    private sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            string aHost, HttpMessageHandler aHandler,
            string bHost, HttpMessageHandler bHandler,
            string cHost, HttpMessageHandler cHandler,
            KeyPair signingKey, Iri signingActor)
        {
            _ = signingActor;
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [aHost] = BuildFetcherFor(aHost, "local", signingKey, aHandler),
                [bHost] = BuildFetcherFor(bHost, "local", signingKey, bHandler),
                [cHost] = BuildFetcherFor(cHost, "local", signingKey, cHandler),
            };
        }

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            var host = new Uri(actorIri.Value).Host;
            if (_fetchers.TryGetValue(host, out var fetcher))
            {
                return fetcher.GetActorAsync(actorIri, ct);
            }

            return Task.FromResult<Actor?>(null);
        }
    }

    /// <summary>
    /// Captures a signed request (its body + headers) instead of forwarding it, so the signed body can
    /// be replayed through a plain <see cref="HttpClient"/>.
    /// </summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public CapturedRequest? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = values.ToList();
            }

            if (request.Content is not null)
            {
                foreach (var (name, values) in request.Content.Headers)
                {
                    if (headers.TryGetValue(name, out var existing))
                    {
                        existing.AddRange(values);
                    }
                    else
                    {
                        headers[name] = values.ToList();
                    }
                }
            }

            Captured = new CapturedRequest(body, headers);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(byte[] Body, Dictionary<string, List<string>> Headers);

    /// <summary>
    /// Builds an id-less public <see cref="Create"/>: the embedded <see cref="Note"/> carries the
    /// <c>as:Public</c> address in its <c>to</c> (the conventional public audience). Decision 055: the
    /// Create and the embedded Note are id-less; the server mints both.
    /// </summary>
    private static Create BuildPublicCreate(Iri actorIri) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        // The conventional outbox-publish shape addresses the public audience on the Create's `to` (and
        // the embedded Note mirrors it). The server preserves as:Public on the Create's `to` and appends
        // the follower set to the Create's `cc` (19.6.5).
        To = [new Link { Href = new Uri(AsPublic.Value) }],
        Object =
        [
            new Note
            {
                Content = ["a public post addressed to everyone"],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
                To = [new Link { Href = new Uri(AsPublic.Value) }],
            },
        ],
    };

    /// <summary>
    /// Builds an id-less reply <see cref="Create"/>: the embedded <see cref="Note"/> is a reply to
    /// <paramref name="parentIri"/> (<c>inReplyTo</c>) and carries <c>as:Public</c> in its <c>to</c>.
    /// </summary>
    private static Create BuildReplyCreate(Iri actorIri, Iri parentIri) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        // A reply addresses the public audience (as:Public) on the Create's `to`; the server appends the
        // reply target (the parent note's author) to the Create's `to` and the follower set to the
        // Create's `cc` (19.6.5).
        To = [new Link { Href = new Uri(AsPublic.Value) }],
        Object =
        [
            new Note
            {
                Content = ["a reply to the parent note"],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
                InReplyTo = [new Link { Href = new Uri(parentIri.Value) }],
                To = [new Link { Href = new Uri(AsPublic.Value) }],
            },
        ],
    };

    private static Announce BuildAnnounce(Iri actorIri)
    {
        var objectIri = new Iri($"https://{AHost}/objects/note-{Guid.NewGuid():N}");
        return new Announce
        {
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(objectIri.Value) }],
        };
    }

    /// <summary>
    /// Learns the server-minted id of an activity from the 202 outbox-publish response body (decision
    /// 055): the server returns the created activity (with its minted id) in the 202 body.
    /// </summary>
    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var created = ActivityJson.Deserialize<Activity>(body);
        Assert.NotNull(created);
        Assert.NotNull(created!.Id);
        return new Iri(created.Id);
    }

    /// <summary>Reads the federated activity's <c>to</c> (when <paramref name="cc"/> is false) or
    /// <c>cc</c> (when true) hrefs, as plain strings.</summary>
    private static IReadOnlyList<string> ActivityHrefs(Activity activity, bool cc)
    {
        var hrefs = new List<string>();
        var entries = cc ? activity.Cc : activity.To;
        if (entries is { } sequence)
        {
            foreach (var item in sequence)
            {
                if (item is ILink { Href: { } href })
                {
                    hrefs.Add(href.ToString());
                }
            }
        }

        return hrefs;
    }

    /// <summary>Reads the embedded Note's <c>to</c> hrefs from a stored (federated) <see cref="Create"/>.</summary>
    private static IReadOnlyList<string> NoteToHrefsOf(Create create)
    {
        var hrefs = new List<string>();
        if (create.Object is { } objects)
        {
            foreach (var obj in objects)
            {
                if (obj is Note { To: { } noteTo })
                {
                    foreach (var item in noteTo)
                    {
                        if (item is ILink { Href: { } href })
                        {
                            hrefs.Add(href.ToString());
                        }
                    }
                }
            }
        }

        return hrefs;
    }

    private static async Task WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }

            await Task.Delay(50);
        }
    }
}
