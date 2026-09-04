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
/// Slice 26.1: cross-instance <see cref="Accept"/> (follow-acceptance) propagation, locked end-to-end
/// across two instances (a 2-instance <see cref="TestServer"/>). This is the inverse of 25.2's
/// <see cref="CrossInstanceRejectPropagationIntegrationTests"/> <see cref="Reject"/> lock and the more
/// common follow flow (auto-accept, the default when the followed actor does not set
/// <c>manuallyApprovesFollowers</c>).
/// </summary>
/// <remarks>
/// For <strong>each</strong> direction the test proves both halves:
/// <list type="number">
/// <item>The <strong>follow</strong> (follower → followed) is recorded on both instances: the follower's
/// home instance records the provisional <c>follower → target</c> edge at publish, and the followed
/// side's <c>FollowActivityHandler</c> records its copy when the signed <c>Follow</c> arrives in its
/// inbox.</item>
/// <item>The <strong>acceptance</strong> propagates back: the followed side's <c>FollowActivityHandler</c>
/// auto-constructs the <c>Accept</c> (actor = the local actor/community being followed, object = the
/// original follow by its original IRI) and server-delivers it to the follower's inbox on the follower's
/// home instance; the follower's home instance stores the inbound <c>Accept</c> (its
/// <c>AcceptActivityHandler</c> finalizes the edge — idempotent, since the follower already recorded it at
/// publish). The non-vacuous signal is the <c>Accept</c> itself: the follower's activity store contains an
/// <c>Accept</c> whose <c>actor</c> is the followed actor and whose <c>object</c> references the follower's
/// minted follow id — a fact that can only be true if the followed side built the <c>Accept</c> AND
/// delivered it to the follower's inbox AND the follower stored it. (The follower's follow edge is recorded
/// at publish, so it is NOT a usable cross-instance signal here; the stored <c>Accept</c> is the
/// cross-instance artifact.)</item>
/// </list>
/// The person test (alice on A follows bob on B) and the community test (community iris on A follows
/// remote community lumen on B) mirror the two cases in <see cref="CrossInstanceRejectPropagationIntegrationTests"/>
/// and additionally lock the <c>AcceptActivityHandler</c>'s community-follower branch (G-3 — a local
/// community is local even though it is not in the person store).
/// </remarks>
/// <para>
/// Topology: instance A (accept-a.domain.local) hosts the local person <c>alice</c> (the person follower)
/// and the community <c>iris</c> (the community follower; with its own signing key). Instance B
/// (accept-b.domain.local) hosts the local person <c>bob</c> (the person follow target) and the community
/// <c>lumen</c> (the community follow target). A's outbound delivery worker routes to B (the Follow hop);
/// A's fetcher routes by actor-IRI host (alice/iris → A, bob/lumen → B). B's outbound delivery worker
/// routes to A (the Accept hop — B's <c>FollowActivityHandler</c> emits the auto-Accept and B's delivery
/// worker delivers it to the follower's inbox on A); B's fetcher routes by actor-IRI host (bob/lumen → B,
/// alice/iris → A) so B can resolve the follower's inbox when delivering the Accept.
/// </para>
public sealed class CrossInstanceAcceptPropagationIntegrationTests : IDisposable
{
    private const string AHost = "accept-a.domain.local";
    private const string BHost = "accept-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Iris = "iris";
    private const string Lumen = "lumen";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly KeyPair _irisKey;
    private readonly Iri _irisCommunityIri;
    private readonly KeyPair _bobKey;
    private readonly Iri _bobActorIri;
    private readonly KeyPair _lumenKey;
    private readonly Iri _lumenCommunityIri;

    public CrossInstanceAcceptPropagationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A: the instance actor (alice) is the person follower; the community (iris) is a second local
        // identity with its own key (the community-follower test's follow author).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;
        var aCommunity = TestSeeder.SeedCommunityWithKey(_aPersistence, AHost, Iris);
        _irisKey = aCommunity.Key;
        _irisCommunityIri = aCommunity.CommunityIri;

        // B: the local person (bob) is the person follow target; the community (lumen) is the community
        // follow target.
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        _bobActorIri = bSeeded.ActorIri;
        var bCommunity = TestSeeder.SeedCommunityWithKey(_bPersistence, BHost, Lumen);
        _lumenKey = bCommunity.Key;
        _lumenCommunityIri = bCommunity.CommunityIri;

        _a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            // A must sign outbound deliveries as BOTH alice (the person follow) and the community iris (the
            // community follow). Register both identities explicitly at their correct IRIs.
            IdentityKeys = BuildIdentityForA(_aliceKey, _aliceActorIri, _irisKey, _irisCommunityIri),
            // A's server delivers the signed Follow to bob's/lumen's inbox on B (the outbound follow hop).
            DeliveryTransport = () => new LazyHandler(() => _b!.CreateHandler()),
            // A's fetcher routes by host: alice + iris (A) and bob + lumen (B).
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => _a!.CreateHandler()),
                BHost, new LazyHandler(() => _b!.CreateHandler()),
                _aliceKey, _aliceActorIri),
        });
        _b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
            // B must sign outbound deliveries as bob (the person accept author) and lumen (the community
            // accept author). Register both identities explicitly at their correct IRIs.
            IdentityKeys = BuildIdentityForB(_bobKey, _bobActorIri, _lumenKey, _lumenCommunityIri),
            // B's server delivers the signed Accept to the follower's inbox on A (the accept hop) — emitted
            // by B's FollowActivityHandler and sent by B's delivery worker.
            DeliveryTransport = () => new LazyHandler(() => _a!.CreateHandler()),
            // B's fetcher routes by host: bob + lumen (B) and alice + iris (A) — so B can resolve the
            // follower's inbox when delivering the Accept back to A.
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => _a!.CreateHandler()),
                BHost, new LazyHandler(() => _b!.CreateHandler()),
                _bobKey, _bobActorIri),
        });
        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);
        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- A person on A follows a person on B (federates A → B); B auto-accepts (its FollowActivityHandler
    //     builds the Accept), B server-delivers the Accept to A, and A's AcceptActivityHandler finalizes
    //     A's local follow edge. The non-vacuous signal: A's activity store holds the inbound Accept. ---

    [Fact]
    public async Task PersonFollowOfRemoteActor_AutoAcceptPropagatesBackToFollowerHomeInstance()
    {
        // Step 1a: alice publishes Follow(bob) to her outbox on A.
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        using var followRequest = SignedRequest(_a, _aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox");
        using var followResponse = await _aHttp.SendAsync(followRequest);
        Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);

        // A minted the Follow's id; learn it from the 2xx body (decision 055).
        var mintedFollowId = await LearnMintedIdAsync(followResponse);
        Assert.True(mintedFollowId is not null, "A should have returned the minted Follow id in the 2xx body.");

        // A recorded alice's local follow edge on publish (alice → bob).
        Assert.True(
            await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "A should record alice's follow edge (alice → bob) on publish.");

        // Step 1b: B recorded the remote edge (bob's followers list alice) and stored the Follow in its
        // activity store (B's FollowActivityHandler's auto-Accept resolves the follow against it).
        await WaitForAsync(
            async () =>
            {
                var followers = await _bPersistence.Follows.GetFollowersAsync(_bobActorIri);
                return followers.Contains(_aliceActorIri)
                    && await _bPersistence.Activities.TryGetActivityAsync(mintedFollowId!.Value, out _);
            },
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            (await _bPersistence.Follows.GetFollowersAsync(_bobActorIri)).Contains(_aliceActorIri),
            "B should list alice among bob's followers after A delivered the signed Follow.");
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedFollowId!.Value, out _),
            "B should have stored the original Follow in its activity store (the Accept resolves against it).");

        // Step 2 (the under-test cross-instance Accept half): B's FollowActivityHandler built the Accept
        // (actor = bob, object = the minted follow id) and B's delivery worker delivered it to alice's inbox
        // on A; A stored the inbound Accept and A's AcceptActivityHandler finalized alice's edge
        // (idempotent — the edge was already recorded at publish). The non-vacuous signal is the stored
        // Accept on A: it can only exist if B built AND delivered it and A stored it.
        await WaitForAsync(
            async () => await FindInboundAcceptAsync(_aPersistence, _bobActorIri, mintedFollowId!.Value),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await FindInboundAcceptAsync(_aPersistence, _bobActorIri, mintedFollowId!.Value),
            "A's activity store should hold the inbound Accept (actor = bob, object = the minted follow id) " +
            "after B built and server-delivered the auto-Accept — the cross-instance accept hop.");
    }

    // --- A community (iris) on A follows a remote community (lumen) on B (federates A → B); B auto-accepts
    //     (its FollowActivityHandler's community branch builds the Accept), B server-delivers the Accept to
    //     A, and A's AcceptActivityHandler (the G-3 community-follower override) finalizes iris's follows
    //     edge. The non-vacuous signal: A's activity store holds the inbound Accept (actor = lumen). ---

    [Fact]
    public async Task CommunityFollowOfRemoteCommunity_AutoAcceptPropagatesBackToFollowerHomeInstance()
    {
        // Step 1a: the community iris publishes Follow(lumen) to its outbox on A.
        var follow = BuildFollow(_irisCommunityIri, _lumenCommunityIri);
        using var followRequest = SignedRequest(_a, _irisCommunityIri, _irisKey, follow, $"/ap/v1/c/{Iris}/outbox");
        using var followResponse = await _aHttp.SendAsync(followRequest);
        Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);

        var mintedFollowId = await LearnMintedIdAsync(followResponse);
        Assert.True(mintedFollowId is not null, "A should have returned the minted Follow id in the 2xx body.");

        // A recorded iris's local follows edge on publish (iris → lumen).
        Assert.True(
            (await _aPersistence.Communities.GetFollowsAsync(_irisCommunityIri)).Contains(_lumenCommunityIri),
            "A should record the community's follows edge (iris → lumen) on publish.");

        // Step 1b: B recorded the remote edge (lumen's follows set includes iris; lumen's followers set
        // includes iris) and stored the Follow in its activity store.
        await WaitForAsync(
            async () =>
            {
                var lumenFollows = await _bPersistence.Communities.GetFollowsAsync(_lumenCommunityIri);
                var lumenFollowers = await _bPersistence.Communities.GetFollowersAsync(_lumenCommunityIri);
                return lumenFollows.Contains(_irisCommunityIri)
                    && lumenFollowers.Contains(_irisCommunityIri)
                    && await _bPersistence.Activities.TryGetActivityAsync(mintedFollowId!.Value, out _);
            },
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            (await _bPersistence.Communities.GetFollowsAsync(_lumenCommunityIri)).Contains(_irisCommunityIri),
            "B should record the community follow (lumen follows iris) after A delivered the signed Follow.");

        // Step 2 (the under-test cross-instance Accept half): B's FollowActivityHandler's community branch
        // built the Accept (actor = lumen, object = the minted follow id) and B's delivery worker delivered
        // it to iris's inbox on A; A stored the inbound Accept and A's AcceptActivityHandler's community
        // branch (G-3) finalized iris's follows edge. The non-vacuous signal is the stored Accept on A.
        await WaitForAsync(
            async () => await FindInboundAcceptAsync(_aPersistence, _lumenCommunityIri, mintedFollowId!.Value),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await FindInboundAcceptAsync(_aPersistence, _lumenCommunityIri, mintedFollowId!.Value),
            "A's activity store should hold the inbound Accept (actor = lumen, object = the minted follow id) " +
            "after B built and server-delivered the auto-Accept — the cross-instance community accept hop.");
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Builds A's signing identity: a key store carrying <em>both</em> the instance actor's key and the
    /// community's key, a provider registering both at their correct IRIs, and a signer. The community key
    /// is registered at the community's IRI (not the instance actor's), so the outbound
    /// <c>DeliveryWorker</c> can sign the federated Follow as the community.
    /// </summary>
    private static IdentityKeys BuildIdentityForA(
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
    /// Builds B's signing identity: a key store carrying <em>both</em> the instance actor's key (bob) and
    /// the community's key (lumen), a provider registering both at their correct IRIs, and a signer — so
    /// B's delivery worker can sign the auto-Accept as bob (person) or lumen (community).
    /// </summary>
    private static IdentityKeys BuildIdentityForB(
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
    /// handler) to produce a correctly signed request, then replays the signed headers onto a fresh request
    /// for delivery to that instance's TestServer.
    /// </summary>
    private HttpRequestMessage SignedRequest(
        TestServer server, Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var host = server == _a ? AHost : BHost;
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
    /// Scans the given activity store for an inbound <see cref="Accept"/> whose <c>actor</c> is
    /// <paramref name="acceptorIri"/> (the followed actor) and whose <c>object</c> references
    /// <paramref name="followId"/> (the follower's minted follow id). Returns <see langword="true"/> when
    /// such an <c>Accept</c> is stored — the cross-instance accept hop has completed.
    /// </summary>
    private static async Task<bool> FindInboundAcceptAsync(
        InMemoryPersistenceProvider persistence, Iri acceptorIri, Iri followId)
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
    /// Learns the server-minted id from a 2xx response body (decision 055: the server returns the created
    /// object in the 2xx body). Returns null when the body is empty or carries no id.
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
}
