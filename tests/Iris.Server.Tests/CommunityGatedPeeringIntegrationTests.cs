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
/// Phase 136.4 (community peering handshake): the <strong>gated</strong> (manually-approving) community
/// follow — the cross-instance direction the auto-accept
/// <see cref="CrossInstanceAcceptPropagationIntegrationTests"/> does not cover. A community that sets
/// <c>manuallyApprovesFollowers</c> does <em>not</em> auto-accept an inbound follow: its
/// <c>FollowActivityHandler</c> records the follows/followers edges + surfaces the request in the
/// community's outbox, but emits no <c>Accept</c>. The operator then authors an <c>Accept</c> and
/// publishes it to the community's own outbox (the AP-native follow-decision write surface), which
/// applies the local edge effect (idempotent — the provisional edges are confirmed) and
/// server-delivers the <c>Accept</c> to the remote follower's inbox; the remote instance's
/// <c>AcceptActivityHandler</c> (the G-3 community-follower override) finalizes the follower's edge.
/// </summary>
/// <remarks>
/// Topology: instance A (peer-gate-a.domain.local) hosts <c>alice</c> (person instance actor) and the
/// community <c>iris</c> — a <em>manually-approving</em> community (the follow target, with its own
/// signing key so the operator's Accept is signed as iris). Instance B (peer-gate-b.domain.local) hosts
/// <c>bob</c> (person instance actor) and the community <c>lumen</c> (the community follower, with its
/// own signing key). A's outbound delivery worker routes to B; B's routes to A. A's fetcher routes by
/// actor-IRI host (alice/iris → A, bob/lumen → B) so A can resolve lumen's inbox when delivering the
/// operator's Accept; B's fetcher routes (bob/lumen → B, alice/iris → A) so B can resolve iris's inbox
/// when delivering the follow.
/// </remarks>
/// <para>
/// The two non-vacuous cross-instance signals:
/// <list type="number">
/// <item>After B delivers the signed <c>Follow</c> to A's <c>iris</c>, <strong>no</strong> <c>Accept</c>
/// has been delivered back to B (the gate suppresses the auto-Accept) — yet A has already recorded the
/// follows/followers edges (the provisional relationship exists locally; it is merely unconfirmed by the
/// followed side).</item>
/// <item>After the operator publishes the <c>Accept</c> to A's <c>iris</c> outbox, A server-delivers it
/// to B's <c>lumen</c> inbox and B's <c>AcceptActivityHandler</c> finalizes lumen's edge — the
/// non-vacuous artifact is B's activity store holding the inbound <c>Accept</c> (actor = iris, object =
/// the follow), which can only exist if A built AND delivered it and B stored it.</item>
/// </list>
/// </para>
[Collection("CommunityGatedPeering")]
public sealed class CommunityGatedPeeringIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "peer-gate-a.domain.local";
    internal const string BHost = "peer-gate-b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";
    internal const string Iris = "iris";
    internal const string Lumen = "lumen";

    private readonly CommunityGatedPeeringSharedHost _fixture;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly Iri _irisCommunityIri;
    private readonly Iri _lumenCommunityIri;
    private KeyPair _irisKey;
    private KeyPair _lumenKey;

    public CommunityGatedPeeringIntegrationTests(CommunityGatedPeeringSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _irisCommunityIri = new Iri($"https://{AHost}/ap/v1/c/{Iris}");
        _lumenCommunityIri = new Iri($"https://{BHost}/ap/v1/c/{Lumen}");
        _aHttp = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
        _bHttp = new HttpClient(fixture.ServerB.CreateHandler(), disposeHandler: false);
        _irisKey = null!;
        _lumenKey = null!;
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence);

        _aPersistence.Keys.TryGetKey(new Iri($"{_irisCommunityIri.Value}#key-1"), out var irisKey);
        _irisKey = (KeyPair)irisKey!;
        _bPersistence.Keys.TryGetKey(new Iri($"{_lumenCommunityIri.Value}#key-1"), out var lumenKey);
        _lumenKey = (KeyPair)lumenKey!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores <c>iris</c> (a <em>manually-approving</em> community) on A and <c>lumen</c> (an
    /// auto-accept community) on B with their existing keys, plus the instance-actor persons.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"https://{AHost}/ap/v1/u/{Alice}#key-1"));
        TestSeeder.SeedManuallyApprovingCommunityWithExistingKey(aPersistence, AHost, Iris, new Iri($"https://{AHost}/ap/v1/c/{Iris}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"https://{BHost}/ap/v1/u/{Bob}#key-1"));
        TestSeeder.SeedCommunityWithExistingKey(bPersistence, BHost, Lumen, new Iri($"https://{BHost}/ap/v1/c/{Lumen}#key-1"));
    }

    // --- B's community lumen follows A's manually-approving community iris; A holds the follow (no
    //     auto-Accept); A's operator Accepts via the community outbox; A server-delivers the Accept to
    //     B's lumen; B's AcceptActivityHandler (G-3) finalizes lumen's edge. ---

    [Fact]
    public async Task GatedCommunityFollow_NoAutoAccept_ThenOperatorAcceptDeliveredBackToFollower()
    {
        // Step 1: lumen (on B) publishes Follow(iris) to its outbox on B.
        var follow = BuildFollow(_lumenCommunityIri, _irisCommunityIri);
        using var followRequest = SignedRequest(_fixture.ServerB, _lumenCommunityIri, _lumenKey, follow, $"/ap/v1/c/{Lumen}/outbox");
        using var followResponse = await _bHttp.SendAsync(followRequest);
        Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);

        var mintedFollowId = await LearnMintedIdAsync(followResponse);
        Assert.True(mintedFollowId is not null, "B should have returned the minted Follow id in the 2xx body.");

        // B recorded lumen's local follows edge on publish (lumen → iris).
        Assert.True(
            (await _bPersistence.Communities.GetFollowsAsync(_lumenCommunityIri)).Contains(_irisCommunityIri),
            "B should record lumen's follows edge (lumen → iris) on publish.");

        // Step 2: A's FollowActivityHandler community branch recorded the follows + followers edges
        // (iris → lumen / lumen → iris) and surfaced the request in iris's outbox, but — because iris is
        // manually-approving — emitted NO Accept. Assert both halves: the edges are present locally on A,
        // AND no Accept has reached B's activity store (the gate suppressed the auto-Accept).
        await WaitForAsync(
            async () =>
            {
                var irisFollows = await _aPersistence.Communities.GetFollowsAsync(_irisCommunityIri);
                var irisFollowers = await _aPersistence.Communities.GetFollowersAsync(_irisCommunityIri);
                return irisFollows.Contains(_lumenCommunityIri)
                    && irisFollowers.Contains(_lumenCommunityIri)
                    && await _aPersistence.Activities.TryGetActivityAsync(mintedFollowId!.Value, out _);
            },
            timeout: TimeSpan.FromSeconds(30));

        // The gate is observable: A holds the provisional edges (the relationship exists locally) ...
        Assert.True(
            (await _aPersistence.Communities.GetFollowsAsync(_irisCommunityIri)).Contains(_lumenCommunityIri),
            "A should record the community's follows edge (iris → lumen) after B delivered the Follow, even when gated.");
        Assert.Contains(_lumenCommunityIri, await _aPersistence.Communities.GetFollowersAsync(_irisCommunityIri));

        // ... but NO auto-Accept was delivered back to B (the gate suppressed it). Give the (non-existent)
        // delivery a short window and assert it never arrives — the non-vacuous "held" signal.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(
            await FindInboundAcceptAsync(_bPersistence, _irisCommunityIri, mintedFollowId!.Value),
            "A manually-approving community must NOT auto-accept: no Accept should have been delivered back to B.");

        // Step 3: the operator on A authors an Accept (actor = iris, object = the follow) and publishes it
        // to iris's own outbox on A (the AP-native follow-decision write surface), signed as iris.
        var accept = BuildAccept(_irisCommunityIri, mintedFollowId.Value);
        using var acceptRequest = SignedRequest(_fixture.ServerA, _irisCommunityIri, _irisKey, accept, $"/ap/v1/c/{Iris}/outbox");
        using var acceptResponse = await _aHttp.SendAsync(acceptRequest);
        Assert.Equal(HttpStatusCode.Accepted, acceptResponse.StatusCode);

        // Step 4: A server-delivered the operator's Accept to B's lumen inbox; B's AcceptActivityHandler's
        // community branch (G-3) finalized lumen's edge. The non-vacuous cross-instance artifact is B's
        // activity store holding the inbound Accept (actor = iris, object = the follow).
        await WaitForAsync(
            async () => await FindInboundAcceptAsync(_bPersistence, _irisCommunityIri, mintedFollowId!.Value),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await FindInboundAcceptAsync(_bPersistence, _irisCommunityIri, mintedFollowId!.Value),
            "B's activity store should hold the inbound Accept (actor = iris, object = the follow) after A's " +
            "operator Accept was published to the community outbox and server-delivered to B — the gated " +
            "community accept hop.");

        // And the edges on A are intact (idempotent confirm — the provisional edges recorded in step 2
        // are re-confirmed, not duplicated).
        Assert.True(
            (await _aPersistence.Communities.GetFollowsAsync(_irisCommunityIri)).Contains(_lumenCommunityIri),
            "A's follows edge (iris → lumen) should remain recorded after the operator's Accept (idempotent).");
        Assert.Contains(_lumenCommunityIri, await _aPersistence.Communities.GetFollowersAsync(_irisCommunityIri));
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Builds instance A's signing identity: the instance actor's key (alice) + the community's key
    /// (iris), a provider registering both at their correct IRIs, and a signer — so A's delivery worker
    /// can sign the operator's Accept as iris (the community), not as the instance actor.
    /// </summary>
    internal static IdentityKeys BuildIdentityForA(KeyPair instanceKey, Iri instanceActorIri, KeyPair communityKey, Iri communityIri)
        => BuildIdentity(instanceKey, instanceActorIri, communityKey, communityIri);

    /// <summary>
    /// Builds instance B's signing identity: the instance actor's key (bob) + the community's key
    /// (lumen), a provider registering both at their correct IRIs, and a signer — so B's delivery worker
    /// can sign the follow as lumen (the community).
    /// </summary>
    internal static IdentityKeys BuildIdentityForB(KeyPair instanceKey, Iri instanceActorIri, KeyPair communityKey, Iri communityIri)
        => BuildIdentity(instanceKey, instanceActorIri, communityKey, communityIri);

    private static IdentityKeys BuildIdentity(
        KeyPair instanceKey, Iri instanceActorIri, KeyPair communityKey, Iri communityIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(instanceKey);
        keyStore.PutKey(communityKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(instanceActorIri, instanceKey.KeyId);
        keyProvider.RegisterKey(communityIri, communityKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on the
    /// author's outbox, addressed to <paramref name="server"/>. Uses the client pipeline (via a capture
    /// handler) to produce a correctly signed request, then replays the signed headers onto a fresh
    /// request for delivery to that instance's TestServer.
    /// </summary>
    private HttpRequestMessage SignedRequest(TestServer server, Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var host = server == _fixture.ServerA ? AHost : BHost;
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClient(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}")
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}")
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

    private static IActorDocumentFetcher BuildFetcherFor(string host, string handle, KeyPair key, HttpMessageHandler handler)
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
    /// Builds an id-less <see cref="Follow"/> from <paramref name="followerIri"/> to
    /// <paramref name="targetIri"/> (id-less: the server mints the activity's id on publish — decision 055).
    /// </summary>
    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// Builds an id-less <see cref="Accept"/> (actor = <paramref name="acceptorIri"/>, object = the
    /// original follow by <paramref name="followId"/>) — the operator's community follow decision
    /// (decision 055: the Accept's own id is minted by the server on the outbox write path).
    /// </summary>
    private static Accept BuildAccept(Iri acceptorIri, Iri followId) => new()
    {
        Actor = [new Link { Href = new Uri(acceptorIri.Value) }],
        Object = [new Link { Href = new Uri(followId.Value) }],
    };

    /// <summary>
    /// Scans the given activity store for an inbound <see cref="Accept"/> whose <c>actor</c> is
    /// <paramref name="acceptorIri"/> (the followed community) and whose <c>object</c> references
    /// <paramref name="followId"/> (the follower's minted follow id). Returns <see langword="true"/> when
    /// such an <c>Accept</c> is stored — the cross-instance accept hop has completed.
    /// </summary>
    private static async Task<bool> FindInboundAcceptAsync(InMemoryPersistenceProvider persistence, Iri acceptorIri, Iri followId)
    {
        foreach (var activity in await persistence.Activities.GetAllActivitiesAsync())
        {
            if (activity is not Accept accept)
            {
                continue;
            }

            var acceptor = accept.Actor?.FirstOrDefault().ResolveObjectIri();
            if (acceptor != acceptorIri)
            {
                continue;
            }

            var objectRef = accept.Object?.FirstOrDefault().ResolveObjectIri();
            if (objectRef == followId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Learns the server-minted id from a 2xx response body (decision 055). Returns null when the body is
    /// empty or carries no id.
    /// </summary>
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

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents based
    /// on the actor IRI's host (A's fetcher needs to reach A and B; B's fetcher needs to reach B and A).
    /// </summary>
    internal sealed class RoutingFetcher : IActorDocumentFetcher
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

    /// <summary>
    /// Captures a signed request (its body + headers) instead of forwarding it, so the signed body can be
    /// replayed through a plain <see cref="HttpClient"/>.
    /// </summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public CapturedRequest? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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

/// <summary>
/// Shared two-host fixture for <see cref="CommunityGatedPeeringIntegrationTests"/> (A:
/// peer-gate-a.domain.local alice + the manually-approving community iris, B: peer-gate-b.domain.local
/// bob + the community lumen). Seeds all four identities with keys ONCE; wires cross-wired delivery +
/// routing fetchers + multi-identity signing via <see cref="SharedHostFixture.ServerRefFor"/>.
/// </summary>
public sealed class CommunityGatedPeeringSharedHost : SharedTwoHostFixture
{
    public CommunityGatedPeeringSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();

        // A: alice (person instance actor) + iris (manually-approving community, the follow target).
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, CommunityGatedPeeringIntegrationTests.AHost, CommunityGatedPeeringIntegrationTests.Alice);
        var aCommunity = TestSeeder.SeedManuallyApprovingCommunityWithKey(aPersistence, CommunityGatedPeeringIntegrationTests.AHost, CommunityGatedPeeringIntegrationTests.Iris);

        // B: bob (person instance actor) + lumen (auto-accept community, the follower).
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, CommunityGatedPeeringIntegrationTests.BHost, CommunityGatedPeeringIntegrationTests.Bob);
        var bCommunity = TestSeeder.SeedCommunityWithKey(bPersistence, CommunityGatedPeeringIntegrationTests.BHost, CommunityGatedPeeringIntegrationTests.Lumen);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);
        var aliceIri = new Iri($"https://{CommunityGatedPeeringIntegrationTests.AHost}/ap/v1/u/{CommunityGatedPeeringIntegrationTests.Alice}");
        var irisIri = new Iri($"https://{CommunityGatedPeeringIntegrationTests.AHost}/ap/v1/c/{CommunityGatedPeeringIntegrationTests.Iris}");
        var bobIri = new Iri($"https://{CommunityGatedPeeringIntegrationTests.BHost}/ap/v1/u/{CommunityGatedPeeringIntegrationTests.Bob}");
        var lumenIri = new Iri($"https://{CommunityGatedPeeringIntegrationTests.BHost}/ap/v1/c/{CommunityGatedPeeringIntegrationTests.Lumen}");

        var optionsA = new ActivityPubHostOptions
        {
            Host = CommunityGatedPeeringIntegrationTests.AHost,
            Handle = CommunityGatedPeeringIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = CommunityGatedPeeringIntegrationTests.BuildIdentityForA(aSeeded.Key, aliceIri, aCommunity.Key, irisIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = new CommunityGatedPeeringIntegrationTests.RoutingFetcher(
                CommunityGatedPeeringIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                CommunityGatedPeeringIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                aSeeded.Key, aliceIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = CommunityGatedPeeringIntegrationTests.BHost,
            Handle = CommunityGatedPeeringIntegrationTests.Bob,
            Persistence = bPersistence,
            IdentityKeys = CommunityGatedPeeringIntegrationTests.BuildIdentityForB(bSeeded.Key, bobIri, bCommunity.Key, lumenIri),
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
            Fetcher = new CommunityGatedPeeringIntegrationTests.RoutingFetcher(
                CommunityGatedPeeringIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                CommunityGatedPeeringIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                bSeeded.Key, bobIri),
        };

        return (optionsA, optionsB);
    }
}

/// <summary>
/// xunit collection definition for the gated-community-peering shared two-host fixture.
/// </summary>
[CollectionDefinition("CommunityGatedPeering")]
public sealed class CommunityGatedPeeringCollection : ICollectionFixture<CommunityGatedPeeringSharedHost>
{
}
