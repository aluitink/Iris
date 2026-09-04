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
/// Cross-instance community un-follow propagation: a <em>community</em> C on instance A follows a
/// <em>community</em> D on instance B (federating A → B, where B records C in D's follows + followers
/// sets), then C publishes an <see cref="Undo"/> of that follow; the Undo federates A → B and B
/// <em>removes</em> C from D's follows + followers sets.
/// </summary>
/// <remarks>
/// This is the community→community analogue of
/// <see cref="CommunityFollowsPersonUnfollowPropagationIntegrationTests"/> (22.14, community→person): the
/// follow target is itself a community (a local <c>Group</c> on B), not a person. The community half is
/// exercised end-to-end: the <c>Follow</c> is authored to C's outbox
/// (<c>POST /ap/v1/c/iris/outbox</c>), A's <c>CommunityOutboxPublishHandler</c> records C's local follows
/// set (C → D) and <em>server-delivers</em> the signed follow to D's inbox on B; B's
/// <c>FollowActivityHandler</c> community branch (F-24) records <em>two</em> edges in D's community store —
/// D's follows set (D → C, so C's content reaches D's members via the federation path) and D's followers
/// set (C → D, so D's <c>followers</c> collection lists C). The <c>Undo</c> is authored the same way; B's
/// <c>UndoActivityHandler</c> community-target branch resolves the original follow from B's activity store
/// and removes both of D's edges (the inverse of the follow).
/// <para>
/// The subtle dependency this test locks: B can only remove the edges if it first received and stored the
/// original <c>Follow</c> (the <c>Undo</c> references the original by its server-minted IRI, and B's
/// handler resolves the parties from its own activity store — it does not re-fetch the original from A).
/// The test exercises the real cross-instance flow: the follow federates A → B (B records + stores), then
/// the <c>Undo(Follow)</c> federates A → B (B resolves the stored original and removes).
/// </para>
/// </remarks>
/// <para>
/// Topology: instance A (cc-unfollow-a.domain.local) hosts the local instance actor <c>alice</c> and the
/// community <c>iris</c> (with its own signing key; C is the following community). Instance B
/// (cc-unfollow-b.domain.local) hosts the local person <c>bob</c> (B's instance actor, so B's document
/// fetcher is routable) and the community <c>nebula</c> (the followed community, with its own signing key).
/// A's outbound delivery worker routes to B; A's fetcher routes by actor-IRI host (alice/iris → A,
/// bob/nebula → B). B's fetcher routes to A (to fetch the community's document and validate the signature
/// of the federated Follow/Undo). The client's writes are signed POSTs to C's outbox on A; the
/// cross-instance hop (A → B) is made by A's server, signed as the community C.
/// </para>
public sealed class CommunityFollowsCommunityUnfollowPropagationIntegrationTests : IDisposable
{
    private const string AHost = "cc-unfollow-a.domain.local";
    private const string BHost = "cc-unfollow-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string CommunityName = "iris";
    private const string TargetCommunityName = "nebula";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly KeyPair _communityKey;
    private readonly Iri _communityIri;
    private readonly KeyPair _bobKey;
    private readonly Iri _bobActorIri;
    private readonly KeyPair _targetCommunityKey;
    private readonly Iri _targetCommunityIri;

    public CommunityFollowsCommunityUnfollowPropagationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A: the instance actor (alice) is the host's local actor; the community (iris) is a second local
        // identity with its own key (the follow/undo are authored + signed as the community C).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;
        var aCommunity = TestSeeder.SeedCommunityWithKey(_aPersistence, AHost, CommunityName);
        _communityKey = aCommunity.Key;
        _communityIri = aCommunity.CommunityIri;

        // B: the local person (bob) is B's instance actor (keeps B's fetcher routable); the community
        // (nebula) is the followed community, a second local identity on B with its own key.
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        _bobActorIri = bSeeded.ActorIri;
        var bCommunity = TestSeeder.SeedCommunityWithKey(_bPersistence, BHost, TargetCommunityName);
        _targetCommunityKey = bCommunity.Key;
        _targetCommunityIri = bCommunity.CommunityIri;

        _a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            // A must sign outbound deliveries as BOTH alice (the instance actor) and the community C
            // (the follow/undo author). The factory's auto-registration would register the community key
            // under https://A/ap/v1/c/{Handle} (name == Handle), which is wrong here (CommunityName
            // != Alice), so register both identities explicitly at their correct IRIs.
            IdentityKeys = BuildIdentityForA(_aliceKey, _aliceActorIri, _communityKey, _communityIri),
            // A's server delivers the signed Follow/Undo to D's inbox on B.
            DeliveryTransport = () => new LazyHandler(() => _b!.CreateHandler()),
            // A's fetcher routes by host: alice + iris (A) and bob + nebula (B).
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
            // B fetches the community C's document from A to validate the signature of the federated
            // Follow/Undo (signed as the community).
            Fetcher = BuildFetcherFor(AHost, Alice, _aliceKey, new LazyHandler(() => _a!.CreateHandler())),
        });
        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _aHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- A community-follow of a remote community federates A → B, and the community is recorded in B's
    //     community store (D's follows + followers); the community's Undo(Follow) federates A → B and B
    //     removes the community from D's follows + followers. ---
    //
    // Step 1 (the outbound follow half): A publishes the community's Follow to the community's outbox;
    // A records C's local follows set (C → D) and server-delivers the signed Follow to D's inbox on B;
    // B validates the signature (fetching the community C's key from A) and records the two directed
    // edges in D's community store — D's follows set (D → C) and D's followers set (C → D) (F-24) — and,
    // crucially, B's InboxProcessor stores the Follow in B's activity store (the copy the Undo will later
    // resolve against).
    //
    // Step 2 (the under-test un-follow half): A publishes the community's Undo(Follow) to the community's
    // outbox; A removes C's local follows edge (C → D) and server-delivers the signed Undo to D's inbox on
    // B; B's UndoActivityHandler community-target branch resolves the original Follow from B's activity
    // store, reads the follower/target parties, and removes both of D's edges. The edges are gone on B
    // only if the Undo federated AND B had stored the original Follow.

    [Fact]
    public async Task CommunityFollowOfRemoteCommunity_FederatesAndRecordsInRemoteCommunityStore()
    {
        // Step 1a: publish the community's Follow (C → D) to the community's own outbox (the write surface).
        var follow = BuildFollow(_communityIri, _targetCommunityIri);
        using var followRequest = SignedRequest(_communityIri, _communityKey, follow, $"/ap/v1/c/{CommunityName}/outbox");
        using var followResponse = await _aHttp.SendAsync(followRequest);
        Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);

        // A minted the Follow's id; learn it from the 2xx body (decision 055).
        var mintedFollowId = await LearnMintedIdAsync(followResponse);
        Assert.True(
            mintedFollowId is not null,
            "A should have returned the minted Follow id in the 2xx body.");

        // A recorded C's local follows set on publish (C now follows D).
        Assert.True(
            (await _aPersistence.Communities.GetFollowsAsync(_communityIri)).Contains(_targetCommunityIri),
            "A should record the community's follows edge (C → D) on publish.");

        // Step 1b: B recorded the edges in D's community store — the Follow federated and was validated.
        // This also means B stored the Follow in its activity store, which Step 2's Undo resolution
        // depends on.
        await WaitForAsync(
            async () =>
            {
                var follows = await _bPersistence.Communities.GetFollowsAsync(_targetCommunityIri);
                var followers = await _bPersistence.Communities.GetFollowersAsync(_targetCommunityIri);
                return follows.Contains(_communityIri) && followers.Contains(_communityIri);
            },
            timeout: TimeSpan.FromSeconds(30));

        var bFollows = await _bPersistence.Communities.GetFollowsAsync(_targetCommunityIri);
        var bFollowers = await _bPersistence.Communities.GetFollowersAsync(_targetCommunityIri);
        Assert.True(
            bFollows.Contains(_communityIri),
            "B should record D's follows edge (D → C) after A delivered the signed Follow.");
        Assert.True(
            bFollowers.Contains(_communityIri),
            "B should list the community C among D's followers after A delivered the signed Follow.");

        // B stored the original Follow in its activity store (the Undo's resolution depends on this).
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedFollowId!.Value, out _),
            "B should have stored the original Follow in its activity store (the Undo resolves against it).");
    }

    // --- The community's Undo(Follow) federates A → B and B removes the community from D's follows +
    //     followers (the cross-instance community un-follow half).
    //
    // The un-follow half: the follow federates A → B (B records D → C + C → D in D's store and stores the
    // Follow), then the Undo(Follow) federates A → B and B removes both edges. The edges are gone on B
    // only if the Undo federated AND B had stored the original Follow (the cross-instance dependency the
    // single-instance tests do not exercise).

    [Fact]
    public async Task CommunityUnfollowOfRemoteCommunity_FederatesAndRemovesFromRemoteCommunityFollowers()
    {
        // Step 1: establish the follow (see the dedicated follow test for the full outbound half).
        var follow = BuildFollow(_communityIri, _targetCommunityIri);
        using var followRequest = SignedRequest(_communityIri, _communityKey, follow, $"/ap/v1/c/{CommunityName}/outbox");
        using var followResponse = await _aHttp.SendAsync(followRequest);
        Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);

        var mintedFollowId = await LearnMintedIdAsync(followResponse);
        Assert.True(
            mintedFollowId is not null,
            "A should have returned the minted Follow id in the 2xx body.");

        // B recorded the edges in D's community store (and stored the Follow in its activity store).
        await WaitForAsync(
            async () =>
            {
                var follows = await _bPersistence.Communities.GetFollowsAsync(_targetCommunityIri);
                var followers = await _bPersistence.Communities.GetFollowersAsync(_targetCommunityIri);
                return follows.Contains(_communityIri) && followers.Contains(_communityIri);
            },
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            (await _bPersistence.Communities.GetFollowersAsync(_targetCommunityIri)).Contains(_communityIri),
            "B should list the community C among D's followers before the un-follow.");

        // Step 2a: publish the community's Undo(Follow) to the community's own outbox.
        var undo = BuildUndo(_communityIri, mintedFollowId!.Value);
        using var undoRequest = SignedRequest(_communityIri, _communityKey, undo, $"/ap/v1/c/{CommunityName}/outbox");
        using var undoResponse = await _aHttp.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // Step 2b: A removed C's local follows edge (the community-outbox-publish handler's Undo branch).
        await WaitForAsync(
            async () => !(await _aPersistence.Communities.GetFollowsAsync(_communityIri)).Contains(_targetCommunityIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            (await _aPersistence.Communities.GetFollowsAsync(_communityIri)).Contains(_targetCommunityIri),
            "A should remove the community's follows edge (C → D) when it publishes the Undo.");

        // Step 2c: B removed its edges — the cross-instance community un-follow half. B's
        // UndoActivityHandler community-target branch resolved the original Follow from B's activity store
        // and removed both of D's edges (RemoveFollowerAsync + RemoveFollowAsync).
        await WaitForAsync(
            async () =>
            {
                var follows = await _bPersistence.Communities.GetFollowsAsync(_targetCommunityIri);
                var followers = await _bPersistence.Communities.GetFollowersAsync(_targetCommunityIri);
                return !follows.Contains(_communityIri) && !followers.Contains(_communityIri);
            },
            timeout: TimeSpan.FromSeconds(30));

        var bFollowsAfter = await _bPersistence.Communities.GetFollowsAsync(_targetCommunityIri);
        var bFollowersAfter = await _bPersistence.Communities.GetFollowersAsync(_targetCommunityIri);
        Assert.False(
            bFollowersAfter.Contains(_communityIri),
            "B should no longer list the community C among D's followers after A's server delivered the signed Undo.");
        Assert.False(
            bFollowsAfter.Contains(_communityIri),
            "B should remove D's follows edge (D → C) after A's server delivered the signed Undo.");
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Builds A's signing identity: a key store carrying <em>both</em> the instance actor's key and the
    /// community's key, a provider registering both at their correct IRIs, and a signer. The community
    /// key is registered at the community's IRI (not the instance actor's), so the outbound
    /// <c>DeliveryWorker</c> can sign the federated Follow/Undo as the community.
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

    /// <summary>
    /// Builds an id-less <see cref="Follow"/> from <paramref name="followerIri"/> (community C) to
    /// <paramref name="targetIri"/> (community D) (id-less: the server mints the activity's id on publish —
    /// decision 055).
    /// </summary>
    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// Builds an id-less <see cref="Undo"/> by <paramref name="actorIri"/> (community C) of the original
    /// follow <paramref name="originalFollowId"/> (the server-minted Follow IRI). The receiving instance
    /// resolves the original follow from its own activity store to determine the parties whose edges to
    /// remove (id-less: the server mints the Undo's own id on publish — decision 055).
    /// </summary>
    private static Undo BuildUndo(Iri actorIri, Iri originalFollowId) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(originalFollowId.Value) }],
    };

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
    /// on the actor IRI's host (A's fetcher needs to reach A and B).
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
