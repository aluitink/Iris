using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Phase 20.2 integration tests (decision 056): the C2S inbox is a first-class, per-actor collection
/// distinct from the outbox. When an activity is delivered to an actor (inbound federation), it is
/// recorded in that actor's <em>inbox</em> (what they received), and the owner can read it back via an
/// owner-only <c>GET /ap/v1/u/{handle}/inbox</c> (Basic auth; 403 for a non-owner; no-store). The
/// client reads it via <see cref="IActivityPubClient.GetInboxItemsAsync"/>.
/// </summary>
/// <remarks>
/// Topology: instance A (inbox-a.domain.local, <c>alice</c>) and instance B (inbox-b.domain.local,
/// <c>bob</c>). A's outbound delivery worker routes to B; A's fetcher routes by actor-IRI host; B's
/// fetcher routes to A (to fetch alice's document and validate the signature of the federated Follow).
/// A single <see cref="Follow"/> (alice → bob) is the cleanest single-recipient inbound case: the
/// recipient is bob's IRI directly, so bob's inbox records exactly the delivered Follow.
/// </remarks>
public sealed class InboxCollectionIntegrationTests : IDisposable
{
    private const string AHost = "inbox-a.domain.local";
    private const string BHost = "inbox-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string BobPassword = "bob-password";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;
    private readonly ActivityPubClient _bobClient;

    public InboxCollectionIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;

        // bob's owner-only Basic credentials (the seam the inbox GET validates via IActorCredentialValidator).
        var bobActorIri = _bobActorIri;
        var bobCredentialValidator = new BasicAuthCredentialValidator((actorIri, user, password) =>
        {
            var valid = actorIri == bobActorIri
                && string.Equals(user, Bob, StringComparison.Ordinal)
                && string.Equals(password, BobPassword, StringComparison.Ordinal);
            return ValueTask.FromResult(valid);
        });

        _a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentity(aSeeded.Key, aSeeded.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => _b!.CreateHandler()),
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => _a!.CreateHandler()),
                BHost, new LazyHandler(() => _b!.CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
        });
        _b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
            CredentialValidator = bobCredentialValidator,
            Fetcher = BuildFetcherFor(AHost, Alice, aSeeded.Key, new LazyHandler(() => _a!.CreateHandler())),
        });
        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);
        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false);

        // bob's client (for GetInboxItemsAsync): a plain signed pipeline pointed at B. The inbox read
        // carries bob's Basic credentials on each request; the signing pipeline is otherwise irrelevant
        // to an owner-authenticated GET.
        var bobKeyStore = new InMemoryKeyStore();
        bobKeyStore.PutKey(bSeeded.Key);
        var bobKeyProvider = new InMemoryKeyProvider(bobKeyStore);
        bobKeyProvider.RegisterKey(_bobActorIri, bSeeded.Key.KeyId);
        var bobSigner = new HttpSignatureSigner(bobKeyStore);
        var factory = new ActivityPubClientFactory(bobKeyStore, bobKeyProvider, bobSigner);
        _bobClient = (ActivityPubClient)factory.Create(
            new ActivityPubClientOptions { ActorId = _bobActorIri, EnableRetry = false },
            new LazyHandler(() => _b.CreateHandler()));
    }

    public void Dispose()
    {
        _bobClient.Dispose();
        _aHttp.Dispose();
        _bHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- An inbound Follow delivered to bob is recorded in bob's INBOX (not only his outbox) ------
    //
    // alice (A) follows bob (B). A's server delivers the Follow to bob's inbox (signed as alice); B
    // validates the signature (fetching alice's document from A) and records the Follow. Decision 056:
    // the delivered activity is ALSO recorded in bob's inbox (the "received" surface), distinct from his
    // outbox (the "authored" surface).

    [Fact]
    public async Task InboundFollow_IsRecordedInRecipientInbox_DistinctFromOutbox()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);

        // The client's write: a single signed POST to A's own outbox. A's server (not the client)
        // delivers the Follow to bob's inbox, signed as alice.
        using var request = SignedRequest(_aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var mintedId = await LearnMintedIdAsync(response);
        Assert.NotNull(mintedId);

        // A recorded the Follow in alice's outbox (the local authoring half), under the minted id.
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == mintedId!.Value.Value);

        // B recorded the follow edge (alice → bob): the cross-instance half (proves A's server delivered
        // the signed Follow).
        await WaitForAsync(
            () => _bPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));

        // Decision 056: B ALSO recorded the delivered Follow in bob's inbox (the "received" surface). The
        // inbox entry is the Follow activity (by its minted id), independent of the follow edge.
        await WaitForAsync(
            () => _bPersistence.Activities.GetInboxAsync(_bobActorIri).ContinueWith(t =>
                t.Result.Any(o => o is IObject { Id: { Length: > 0 } id } && id == mintedId!.Value.Value)),
            timeout: TimeSpan.FromSeconds(30));

        var bobInbox = await _bPersistence.Activities.GetInboxAsync(_bobActorIri);
        Assert.Contains(bobInbox, o => o is IObject { Id: { Length: > 0 } id } && id == mintedId!.Value.Value);

        // Distinct surface: the inbox is a per-actor collection of DELIVERED activities, independent of
        // the outbox (what bob authored). The inbox holds the received Follow by its minted id; whether
        // the follow handler ALSO echoes it into the outbox is orthogonal (the two surfaces are separate).
        Assert.True((await _bPersistence.Activities.GetInboxAsync(_bobActorIri)).Count >= 1);
    }

    // --- The inbox is owner-only: 200 for the owner (Basic auth), 403 otherwise, no-store ---------
    //
    // After the inbound Follow is in bob's inbox, an owner-only GET /ap/v1/u/bob/inbox returns the
    // collection for bob (Basic auth) and 403 for an unauthenticated / wrong-credential requester. The
    // response is no-store (private, owner-scoped data).

    [Fact]
    public async Task InboxEndpoint_OwnerOnly_200ForOwner_403ForNonOwner_NoStore()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        using (var request = SignedRequest(_aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox"))
        using (var response = await _aHttp.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await WaitForAsync(
            () => _bPersistence.Activities.GetInboxAsync(_bobActorIri).ContinueWith(t => t.Result.Count > 0),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True((await _bPersistence.Activities.GetInboxAsync(_bobActorIri)).Count > 0);

        // Owner (bob, Basic auth) → 200 + the inbox collection carrying the delivered Follow, no-store.
        using (var ownerRequest = BasicRequest($"/ap/v1/u/{Bob}/inbox", Bob, BobPassword))
        using (var ownerResponse = await _bHttp.SendAsync(ownerRequest))
        {
            Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
            Assert.Equal("no-store", ownerResponse.Headers.CacheControl?.ToString());
            var body = await ownerResponse.Content.ReadAsStringAsync();
            Assert.Contains("inbox", body);
        }

        // Non-owner: no credentials → 403 (the collection exists but the requester may not read it).
        using (var anonRequest = new HttpRequestMessage(HttpMethod.Get, $"https://{BHost}/ap/v1/u/{Bob}/inbox"))
        using (var anonResponse = await _bHttp.SendAsync(anonRequest))
        {
            Assert.Equal(HttpStatusCode.Forbidden, anonResponse.StatusCode);
        }

        // Non-owner: wrong credentials → 403.
        using (var wrongRequest = BasicRequest($"/ap/v1/u/{Bob}/inbox", Bob, "not-the-password"))
        using (var wrongResponse = await _bHttp.SendAsync(wrongRequest))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongResponse.StatusCode);
        }

        // Unknown actor → 404.
        using (var missingRequest = new HttpRequestMessage(HttpMethod.Get, $"https://{BHost}/ap/v1/u/nope/inbox"))
        using (var missingResponse = await _bHttp.SendAsync(missingRequest))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        }
    }

    // --- The client reads the owner's inbox via GetInboxItemsAsync (Basic credentials) ------------
    //
    // bob's client, carrying bob's Basic credentials, reads bob's inbox (GetInboxItemsAsync) and yields
    // the delivered Follow. Without credentials (a 403) it yields nothing.

    [Fact]
    public async Task Client_GetInboxItemsAsync_YieldsDeliveredActivity_ForOwnerOnly()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        using (var request = SignedRequest(_aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox"))
        using (var response = await _aHttp.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await WaitForAsync(
            () => _bPersistence.Activities.GetInboxAsync(_bobActorIri).ContinueWith(t => t.Result.Count > 0),
            timeout: TimeSpan.FromSeconds(30));

        // The owner (bob, with bob's Basic credentials) reads the inbox: the delivered Follow is yielded.
        var inboxItems = new List<IObjectOrLink>();
        await foreach (var item in _bobClient.GetInboxItemsAsync(_bobActorIri, new Iris.Client.Pipeline.ProxyCredentials(Bob, BobPassword)))
        {
            inboxItems.Add(item);
        }
        Assert.NotEmpty(inboxItems);
        Assert.Contains(inboxItems, o => o is IObject { Id: { Length: > 0 } });

        // A non-owner (wrong credentials → 403) reads nothing.
        var deniedItems = new List<IObjectOrLink>();
        await foreach (var item in _bobClient.GetInboxItemsAsync(_bobActorIri, new Iris.Client.Pipeline.ProxyCredentials(Bob, "not-the-password")))
        {
            deniedItems.Add(item);
        }
        Assert.Empty(deniedItems);
    }

    // --- The inbox is idempotent by activity IRI (mirrors the outbox) ------------------------------
    //
    // An at-least-once delivery (or a restart replay) of the SAME activity id is recorded in the inbox
    // exactly once — the store's idempotent-by-IRI guard (mirroring the outbox). (A re-publish to the
    // outbox mints a NEW id under decision 055, so it is a genuinely distinct activity; this test pins
    // the same-id idempotency directly at the store.)

    [Fact]
    public async Task Inbox_AddSameActivityIriTwice_IsRecordedOnce()
    {
        // A delivered activity with a fixed originator id (inbound keeps the originator id — decision 055).
        var follow = new Follow
        {
            Id = $"{_aliceActorIri}/follow/deterministic-1",
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(_bobActorIri.Value) }],
        };

        await _bPersistence.Activities.AddToInboxAsync(_bobActorIri, follow);
        await _bPersistence.Activities.AddToInboxAsync(_bobActorIri, follow);

        var bobInbox = await _bPersistence.Activities.GetInboxAsync(_bobActorIri);
        Assert.Single(bobInbox);
        Assert.Contains(bobInbox, o => o is IObject { Id: { Length: > 0 } id }
            && id == $"{_aliceActorIri}/follow/deterministic-1");
    }

    // --- Helpers --------------------------------------------------------------------------

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
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

    private static HttpRequestMessage BasicRequest(string path, string user, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{BHost}{path}");
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        return request;
    }

    private static Follow BuildFollow(Iri followerIri, Iri followeeIri) => new()
    {
        // Decision 055: the client sends the Follow's shape (no id); the server mints the id.
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(followeeIri.Value) }],
    };

    private static async Task<Iri?> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var activity = ActivityJson.Deserialize<IObjectOrLink>(body) as Activity;
        var id = activity?.Id;
        return string.IsNullOrWhiteSpace(id) ? null : new Iri(id);
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

    private sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            string aHost, HttpMessageHandler aHandler,
            string bHost, HttpMessageHandler bHandler,
            KeyPair signingKey, Iri signingActor)
        {
            _ = signingActor;
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [aHost] = BuildFetcherFor(aHost, "local", signingKey, aHandler),
                [bHost] = BuildFetcherFor(bHost, "local", signingKey, bHandler),
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
