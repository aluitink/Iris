using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Iris.Client;
using Iris.Client.Caching;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 146.1 / 146.2 (F-136.12.7 + F-136.12.1): the outbound <see cref="IActivityPubClient"/> used by
/// the outbox publish path resolves a remote reply parent's author by fetching the parent document over
/// the wire. Before 146.1 the outbound client had no <see cref="ActorCache"/>, so <em>every</em> remote
/// object fetch hit the wire — including the <strong>duplicate</strong> fetch that occurred when a reply
/// to a remote parent triggered <c>ResolveObjectAuthorForDeliveryAsync</c> twice (once in the audience
/// rewrite, once for the parent-author delivery). 146.1 wires a server-side <see cref="ActorCache"/> into
/// the outbound client so the second fetch is served from cache.
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, actor <c>bob</c> — hosts the parent note m1) and instance B
/// (b.domain.local, actor <c>alice</c> — replies to m1). B's outbound object fetcher (the
/// <see cref="ActivityPubHostOptions.Client"/>) is wrapped in a counting handler that records every GET
/// request. The test publishes a reply to m1 from B and asserts that m1's document was fetched
/// <strong>at most once</strong> (the cache serves the second resolution from memory).
/// </remarks>
public sealed class OutboundClientCachingIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly CountingHandler _countingHandler;

    public OutboundClientCachingIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Bob);
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Alice);
        _aliceKey = bSeeded.Key;
        _aliceActorIri = bSeeded.ActorIri;

        var aServerHolder = new TestServerHolder();
        var bServerHolder = new TestServerHolder();
        _countingHandler = new CountingHandler(new LazyHandler(() => aServerHolder.Server!.CreateHandler()));

        // A: hosts bob (the parent's author). Its inbound fetcher reaches B (validates alice's reply).
        _a = aServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Bob,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentityKeys(aSeeded.Key, aSeeded.ActorIri),
            Fetcher = BuildFetcherFor(AHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
        });

        // B: hosts alice (the replier). Its outbound object fetcher (the Client option) routes to A
        // THROUGH the counting handler, so we can count how many times m1's document is fetched. The
        // client carries an ActorCache (146.1) so the second resolution is a cache hit.
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(bSeeded.Key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(bSeeded.ActorIri, bSeeded.Key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var actorCache = new ActorCache();
        var outboundClient = factory.Create(
            new ActivityPubClientOptions
            {
                ActorId = bSeeded.ActorIri,
                EnableRetry = false,
                Caches = new ClientCaches(Actors: actorCache),
            },
            _countingHandler);

        _b = bServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Alice,
            Persistence = _bPersistence,
            IdentityKeys = BuildIdentityKeys(bSeeded.Key, bSeeded.ActorIri),
            Fetcher = BuildSelfFetcher(BHost, bSeeded.Key, bSeeded.ActorIri, () => bServerHolder.Server!),
            DeliveryTransport = () => new LazyHandler(() => aServerHolder.Server!.CreateHandler()),
            Client = outboundClient,
        });

        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri($"https://{AHost}") };
        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri($"https://{BHost}") };
    }

    public void Dispose()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
        _countingHandler.Dispose();
    }

    [Fact]
    public async Task ReplyToRemoteParent_ParentDocumentFetchedAtMostOnce_CacheServesSecondResolution()
    {
        // bob (A) posts the parent note m1 on A (the parent's home). The IRI must be in A's serving
        // namespace so A's object-document endpoint can serve it when B fetches it.
        var parentIri = new Iri($"https://{AHost}/ap/v1/objects/m1-{Guid.NewGuid():N}");
        await _aPersistence.Objects.PutObjectAsync(new Note
        {
            Id = parentIri.Value,
            Content = ["a post on the remote instance"],
            AttributedTo = [new Link { Href = new Uri($"https://{AHost}/ap/v1/u/{Bob}") }],
        });

        // Reset the counter (the setup may have made some GETs).
        _countingHandler.Reset();

        // alice (B) authors a reply to m1 via a signed POST to B's outbox. The publish path calls
        // ResolveObjectAuthorForDeliveryAsync twice for the same parentIri: once in
        // RewriteOutboundAudienceAsync (to add the parent author to the `to` audience) and once for the
        // parent-author delivery. With the ActorCache (146.1), the first call fetches m1 over the wire
        // and caches it; the second call is a cache hit (no wire).
        var replyIri = new Iri($"https://{BHost}/ap/v1/objects/reply-{Guid.NewGuid():N}");
        var create = BuildReplyCreate(_aliceActorIri, parentIri, replyIri);
        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // Wait for the reply to be delivered to A (the parent's home).
        await WaitForAsync(async () =>
            await _aPersistence.Objects.TryGetObjectAsync(replyIri, out _),
            timeout: TimeSpan.FromSeconds(30));

        // The parent document (m1) was fetched at most once over the wire. The second resolution
        // (parent-author delivery) was served from the ActorCache. Without the cache (pre-146.1),
        // the count would be 2.
        var m1PathSegment = parentIri.Value.Split('/').Last();
        var m1Gets = _countingHandler.GetCountForPath(m1PathSegment);
        Assert.True(m1Gets <= 1,
            $"Expected m1 to be fetched at most once over the wire (ActorCache should serve the second resolution), but it was fetched {m1Gets} times.");
    }

    // --- Helpers --------------------------------------------------------------------------

    private static async Task WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("Timed out waiting for condition.");
    }

    private static Create BuildReplyCreate(Iri actorIri, Iri parentIri, Iri replyIri) => new()
    {
        Id = $"https://{BHost}/activities/reply-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = replyIri.Value,
                Content = ["a reply to the remote parent"],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
                InReplyTo = [new Link { Href = new Uri(parentIri.Value) }],
            },
        ],
    };

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on
    /// B's outbox. Uses the client pipeline (via a <see cref="CaptureHandler"/>) to produce a correctly
    /// signed request, then replays the signed headers onto a fresh request.
    /// </summary>
    private static HttpRequestMessage SignedOutboxRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using var client = BuildClientForSigning(actorIri, key, capture);

        var signedContent = new StringContent(json);
        signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
        var response = client
            .SendAsync(
                new HttpRequestMessage(HttpMethod.Post, $"https://{BHost}{path}")
                {
                    Content = signedContent,
                },
                CancellationToken.None)
            .GetAwaiter().GetResult();
        response.Dispose();

        var captured = capture.Captured!;
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{BHost}{path}")
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

    private static IdentityKeys BuildIdentityKeys(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, Func<TestServer> targetServer)
    {
        var client = BuildClient(host, handle, key, targetServer);
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private static IActorDocumentFetcher BuildSelfFetcher(
        string host, KeyPair key, Iri actorIri, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private static IActivityPubClient BuildClient(
        string host, string handle, KeyPair key, Func<TestServer> targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => targetServer().CreateHandler()));
    }

    private static IActivityPubClient BuildClientForSigning(Iri actorIri, KeyPair key, HttpMessageHandler handler)
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

    /// <summary>
    /// A counting <see cref="DelegatingHandler"/> that records GET requests by path suffix.
    /// </summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, int> _getCountsByPathSuffix = new();

        public CountingHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            lock (_lock)
            {
                if (request.Method == HttpMethod.Get)
                {
                    var path = request.RequestUri!.AbsolutePath;
                    var suffix = path.Split('/').LastOrDefault() ?? path;
                    _getCountsByPathSuffix.TryGetValue(suffix, out var count);
                    _getCountsByPathSuffix[suffix] = count + 1;
                }
            }

            return await base.SendAsync(request, ct);
        }

        public void Reset()
        {
            lock (_lock)
            {
                _getCountsByPathSuffix.Clear();
            }
        }

        public int GetCountForPath(string pathSuffix)
        {
            lock (_lock)
            {
                foreach (var (suffix, count) in _getCountsByPathSuffix)
                {
                    if (suffix.EndsWith(pathSuffix, StringComparison.Ordinal))
                    {
                        return count;
                    }
                }

                return 0;
            }
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public CapturedRequest? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = values.ToList();
            }

            if (request.Content is { } contentHeaders)
            {
                foreach (var (name, values) in contentHeaders.Headers)
                {
                    headers[name] = values.ToList();
                }
            }

            Captured = new CapturedRequest(headers);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(Dictionary<string, List<string>> Headers);

    private sealed class TestServerHolder
    {
        public TestServer? Server { get; set; }
    }
}
