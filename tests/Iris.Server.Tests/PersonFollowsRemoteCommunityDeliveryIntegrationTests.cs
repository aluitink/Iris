using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Regression test for the <em>person-follows-a-remote-community delivery</em> bug: when a local person
/// follows a <em>remote</em> community (the peered-Lemmy case), the remote community is cached in the
/// follower's own actor store (so <c>TryGetCommunityAsync(recipient)</c> is TRUE for it). The old
/// store-membership local/remote test in <c>OutboxPublishHandler</c> misclassified such a cached remote
/// as local and SKIPPED the cross-instance delivery — the Follow was recorded in the follower's outbox +
/// the local follows edge, but never sent to the remote community's inbox, so the peer never recorded the
/// follower. The fix makes the local/remote test host-based (a cached-but-remote community is delivered;
/// a genuinely-local community short-circuits to the local inbox write).
/// </summary>
/// <remarks>
/// Topology: instance A (pfr-a.domain.local) hosts the local person <c>alice</c>. Instance B
/// (pfr-b.domain.local) hosts the remote community <c>lumen</c> (a Group with a real key) and a local
/// member <c>bob</c>. A's outbound delivery routes to B; A's fetcher routes by actor-IRI host (alice → A,
/// lumen → B). The client's write is a signed POST of a <see cref="Follow"/> to alice's outbox on A; the
/// cross-instance hop (A → B) is made by A's server. The test asserts the Follow FEDERATES (B's community
/// follows set records alice), which only happens when the delivery is actually made — the store-membership
/// bug would leave B's follows set empty (the Follow never reaches B).
/// </remarks>
[Collection("PersonFollowsRemoteCommunityDelivery")]
public sealed class PersonFollowsRemoteCommunityDeliveryIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "pfr-a.domain.local";
    internal const string BHost = "pfr-b.domain.local";
    internal const string Alice = "alice";
    internal const string RemoteCommunity = "lumen";

    private readonly PersonFollowsRemoteCommunityDeliverySharedHost _fixture;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _remoteCommunityIri;

    public PersonFollowsRemoteCommunityDeliveryIntegrationTests(PersonFollowsRemoteCommunityDeliverySharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _remoteCommunityIri = new Iri($"https://{BHost}/ap/v1/c/{RemoteCommunity}");
        _aliceKey = null!;
        _aHttp = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence);
        _aPersistence.Keys.TryGetKey(new Iri($"{_aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores alice (on A) and the remote community lumen (on B) with their existing keys, and —
    /// critically for this regression — CACHES a <see cref="Group"/> for the remote community lumen in
    /// A's own community store (as it would be after alice browsed/followed the remote community and its
    /// document was cached). With lumen cached in A, the old store-membership local/remote test
    /// misclassified lumen as local and skipped the cross-instance delivery.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var lumenIri = new Iri($"https://{BHost}/ap/v1/c/{RemoteCommunity}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedCommunityWithExistingKey(bPersistence, BHost, RemoteCommunity, new Iri($"{lumenIri.Value}#key-1"));

        // Cache the remote community's Group in A (the peered-community case): A has seen lumen's document
        // (e.g. alice followed it and A cached the remote Group), so A's community store now holds it. This
        // is the precondition that the store-membership local/remote test misread as "local".
        var cached = new Group { Id = lumenIri.Value, PreferredUsername = RemoteCommunity };
        aPersistence.Communities.PutCommunityAsync(cached).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Person_FollowOfRemoteCommunity_FederatesToRemoteCommunityInbox()
    {
        // alice (A) follows B's community lumen: a signed Follow published to alice's outbox on A.
        var follow = BuildFollow(_aliceActorIri, _remoteCommunityIri);
        using var request = SignedRequest(_aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted || response.StatusCode == HttpStatusCode.Created,
            $"A should accept the Follow publish (got {(int)response.StatusCode}).");

        // A recorded alice's local follows edge (alice now follows lumen).
        await WaitForAsync(
            async () => await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _remoteCommunityIri),
            timeout: TimeSpan.FromSeconds(5));

        // The follow FEDERATED: B's community follows set records alice. This is the acceptance — with the
        // store-membership bug the Follow is never delivered, so B's follows set stays empty and this fails.
        await WaitForAsync(
            async () => (await _bPersistence.Communities.GetFollowsAsync(_remoteCommunityIri)).Contains(_aliceActorIri),
            timeout: TimeSpan.FromSeconds(10));

        // B validated the signature and recorded the follow (a follow of a community IS a membership grant).
        Assert.Contains(_aliceActorIri, await _bPersistence.Communities.GetFollowsAsync(_remoteCommunityIri));
        Assert.Contains(_aliceActorIri, await _bPersistence.Communities.GetFollowersAsync(_remoteCommunityIri));
    }

    // --- Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Builds an id-less <see cref="Follow"/> from <paramref name="followerIri"/> (alice) to
    /// <paramref name="targetIri"/> (the remote community) (id-less: the server mints the activity's id on
    /// publish — decision 055).
    /// </summary>
    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// Signs an activity as <paramref name="actorIri"/> (key <paramref name="key"/>) and replays the signed
    /// request (body + signature headers) through a plain <see cref="HttpClient"/> so it can be sent to the
    /// TestServer. Mirrors the capture-and-replay pattern in the other outbox-publish integration tests.
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

    internal static IActorDocumentFetcher BuildFetcherFor(
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

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents based
    /// on the actor IRI's host (A's fetcher needs to reach A and B).
    /// </summary>
    internal sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            string aHost, HttpMessageHandler aHandler,
            string bHost, HttpMessageHandler bHandler,
            KeyPair signingKey)
        {
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [aHost] = BuildFetcherFor(aHost, Alice, signingKey, aHandler),
                [bHost] = BuildFetcherFor(bHost, Alice, signingKey, bHandler),
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
    /// Captures a signed request (its body + headers) instead of forwarding it, so the signed body can be
    /// replayed through a plain <see cref="HttpClient"/>.
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

/// <summary>
/// Shared two-host fixture for <see cref="PersonFollowsRemoteCommunityDeliveryIntegrationTests"/>
/// (A: pfr-a.domain.local alice, B: pfr-b.domain.local lumen community + bob). Seeds all identities with
/// keys ONCE; wires cross-wired delivery + routing fetchers via <see cref="SharedHostFixture.ServerRefFor"/>.
/// </summary>
public sealed class PersonFollowsRemoteCommunityDeliverySharedHost : SharedTwoHostFixture
{
    public PersonFollowsRemoteCommunityDeliverySharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, PersonFollowsRemoteCommunityDeliveryIntegrationTests.AHost, PersonFollowsRemoteCommunityDeliveryIntegrationTests.Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, PersonFollowsRemoteCommunityDeliveryIntegrationTests.BHost, "bob");
        var bCommunity = TestSeeder.SeedCommunityWithKey(bPersistence, PersonFollowsRemoteCommunityDeliveryIntegrationTests.BHost, PersonFollowsRemoteCommunityDeliveryIntegrationTests.RemoteCommunity, new Iri($"https://{PersonFollowsRemoteCommunityDeliveryIntegrationTests.BHost}/ap/v1/u/bob"));

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);
        var aliceIri = new Iri($"https://{PersonFollowsRemoteCommunityDeliveryIntegrationTests.AHost}/ap/v1/u/{PersonFollowsRemoteCommunityDeliveryIntegrationTests.Alice}");
        var aKeyStore = new InMemoryKeyStore();
        aKeyStore.PutKey(aSeeded.Key);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        aKeyProvider.RegisterKey(aliceIri, aSeeded.Key.KeyId);

        var optionsA = new ActivityPubHostOptions
        {
            Host = PersonFollowsRemoteCommunityDeliveryIntegrationTests.AHost,
            Handle = PersonFollowsRemoteCommunityDeliveryIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = new IdentityKeys(aKeyStore, aKeyProvider, new HttpSignatureSigner(aKeyStore)),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = new PersonFollowsRemoteCommunityDeliveryIntegrationTests.RoutingFetcher(
                PersonFollowsRemoteCommunityDeliveryIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                PersonFollowsRemoteCommunityDeliveryIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                aSeeded.Key),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = PersonFollowsRemoteCommunityDeliveryIntegrationTests.BHost,
            Handle = "bob",
            Persistence = bPersistence,
            Fetcher = PersonFollowsRemoteCommunityDeliveryIntegrationTests.BuildFetcherFor(
                PersonFollowsRemoteCommunityDeliveryIntegrationTests.AHost,
                PersonFollowsRemoteCommunityDeliveryIntegrationTests.Alice,
                aSeeded.Key,
                new LazyHandler(() => serverARef().CreateHandler())),
        };

        return (optionsA, optionsB);
    }
}

/// <summary>
/// xunit collection definition for the person-follows-remote-community delivery shared two-host fixture.
/// </summary>
[CollectionDefinition("PersonFollowsRemoteCommunityDelivery")]
public sealed class PersonFollowsRemoteCommunityDeliveryCollection : ICollectionFixture<PersonFollowsRemoteCommunityDeliverySharedHost>
{
}
