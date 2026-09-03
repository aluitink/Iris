using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.6.2 — <strong>Outbox page-cache invalidation on a local outbox write</strong>: the outbox
/// collection page is served through the local collection-page response cache (a 60s TTL). Before this
/// fix, a local actor publishing an activity to their own outbox left the cached page-1 stale, so the
/// actor's outbox card (a plain, non-<c>?refresh</c> read) did not surface the activity it just
/// published until the TTL lapsed or a manual refresh bypass was issued.
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local, alice) hosting the real outbox publish endpoint and the
/// public outbox collection endpoint. The test primes the page-1 cache with a plain read (the baseline
/// the UI takes on first load), then publishes a signed <see cref="Create"/> to the outbox endpoint (the
/// real user path — the handler that records the activity and now invalidates the cached page), then does
/// a PLAIN (non-<c>?refresh</c>) read and asserts the new activity is visible immediately.
/// </remarks>
/// <remarks>
/// This is the HTTP-publish half of the boundary that <c>OutboxCacheBypassIntegrationTests</c> pins from
/// the store-write side: a write that bypasses the handler (a direct store <c>AddToOutboxAsync</c>) leaves
/// the cached page stale and relies on the <c>?refresh=true</c> escape hatch, whereas a write that goes
/// through the outbox publish handler invalidates the page so the primary (plain) read is fresh. The two
/// are complementary, not contradictory.
/// </remarks>
public sealed class OutboxPublishCacheInvalidationIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly string _base = $"https://{AHost}";
    private string _outboxUrl => $"{_base}/ap/v1/u/{Alice}/outbox";

    public OutboxPublishCacheInvalidationIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var seeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        _aliceKey = seeded.Key;
        _aliceActorIri = seeded.ActorIri;

        // The inbound key resolver fetches the signing actor's document to recover the public key.
        // That fetch must resolve IN-PROCESS (routed to this TestServer), not over the real network —
        // so the host's IActorDocumentFetcher is an IrisActorDocumentFetcher backed by a LazyHandler
        // that defers to the TestServer once it is constructed (the chicken-and-egg the LazyHandler
        // exists for). A TestServer[] holds the server so the fetcher closure can see it after StartServer.
        var self = new TestServer[1];
        _server = StartServer(_persistence, _aliceKey, _aliceActorIri, () => self[0]!.CreateHandler());
        self[0] = _server;
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A published activity is immediately visible on a plain (non-refresh) outbox read --------

    [Fact]
    public async Task PublishedActivity_IsVisibleOnPlainOutboxRead_WithoutRefreshBypass()
    {
        // 1) Prime the page-1 cache with a plain read (a miss → renders and caches a page holding only
        //    the seeded outbox item, if any). This is the baseline the UI takes on first load.
        using (var prime = await _http.GetAsync($"{_outboxUrl}?limit=10"))
        {
            prime.EnsureSuccessStatusCode();
        }

        // 2) Publish a NEW activity to the outbox through the signed outbox-publish endpoint (the real
        //    user path). The handler records it AND invalidates the cached page-1.
        var create = BuildIdlessCreate(_aliceActorIri);
        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        // 3) A PLAIN (non-?refresh) read of the outbox now sees the new activity immediately — the
        //    handler's invalidation dropped the stale cached page, so the next read re-renders from the
        //    live store. Without the invalidation this read would serve the stale cached page (the new
        //    activity absent) until the 60s TTL lapsed.
        using var read = await _http.GetAsync($"{_outboxUrl}?limit=10");
        read.EnsureSuccessStatusCode();
        var ids = JsonDoc.ItemIdsOf(await read.Content.ReadAsStringAsync());
        Assert.Contains(mintedId.Value, ids);
    }

    // --- The same holds for the community outbox (a Group's publish invalidates its cached page) --

    [Fact]
    public async Task CommunityPublishedActivity_IsVisibleOnPlainOutboxRead_WithoutRefreshBypass()
    {
        // A community (Group) authored by alice. The community publishes a Follow to its own outbox
        // through the signed community outbox-publish endpoint (the handler that records + invalidates).
        var communityIri = TestSeeder.SeedCommunity(_persistence, AHost, "devs");

        // Prime the community outbox page-1 cache with a plain read (a miss → renders and caches).
        using (var prime = await _http.GetAsync($"{_base}/ap/v1/c/devs/outbox?limit=10"))
        {
            prime.EnsureSuccessStatusCode();
        }

        // Publish a community-authored Follow (a Follow has a recipient; bob is a local actor so no
        // cross-instance delivery hop is needed). The handler records it + invalidates the page-1 cache.
        var follow = new Follow
        {
            Actor = [new Link { Href = new Uri(communityIri.Value) }],
            Object = [new Link { Href = new Uri($"{_base}/ap/v1/u/{Alice}") }],
        };
        using var request = SignedRequest(communityIri, _aliceKey, follow, $"/ap/v1/c/devs/outbox");
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        // A plain (non-?refresh) read of the community outbox now sees the new Follow immediately.
        using var read = await _http.GetAsync($"{_base}/ap/v1/c/devs/outbox?limit=10");
        read.EnsureSuccessStatusCode();
        var ids = JsonDoc.ItemIdsOf(await read.Content.ReadAsStringAsync());
        Assert.Contains(mintedId.Value, ids);
    }

    // --- Helpers ---------------------------------------------------------------------------

    private static Iri ActorIriString() => new Iri($"https://{AHost}/ap/v1/u/{Alice}");

    /// <summary>
    /// Builds an id-less public <see cref="Create"/> addressed to the author (no remote recipients → no
    /// cross-instance delivery hop → no fetcher needed for the single-instance test).
    /// </summary>
    private static Create BuildIdlessCreate(Iri actorIri) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Content = ["a freshly published post"],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            },
        ],
    };

    private TestServer StartServer(
        InMemoryPersistenceProvider persistence, KeyPair aliceKey, Iri aliceActorIri,
        Func<HttpMessageHandler> selfHandlerFactory)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(aliceActorIri, aliceKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        // The inbound fetcher resolves the local actor's document in-process (so the signature
        // middleware can recover alice's public key without a real network hop).
        var fetchClient = new ActivityPubClientFactory(keyStore, keyProvider, signer).Create(
            new ActivityPubClientOptions { ActorId = aliceActorIri, EnableRetry = false },
            new LazyHandler(selfHandlerFactory));
        var fetcher = new IrisActorDocumentFetcher(fetchClient, new RemoteActorCache());

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            Fetcher = fetcher,
        });
    }

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
                    new HttpRequestMessage(HttpMethod.Post, $"{_base}{path}")
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}{path}")
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

    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var created = ActivityJson.Deserialize<Activity>(body);
        Assert.NotNull(created);
        Assert.NotNull(created!.Id);
        return new Iri(created.Id);
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
}
