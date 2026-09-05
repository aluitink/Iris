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
/// Cross-instance community un-follow propagation: a <em>community</em> on instance A follows a
/// <em>person</em> on instance B (federating A → B and recorded in B's follow store as bob's follower),
/// then the community publishes an <see cref="Undo"/> of that follow; the Undo federates A → B and B
/// <em>removes</em> the community from bob's followers set.
/// </summary>
/// <remarks>
/// This is the follow-flavored, community-follower counterpart of
/// <see cref="ModerationUndoPropagationIntegrationTests"/> (which pins a person's Block/Flag undo) and
/// the person-follower counterpart of
/// <see cref="CommunityOutboxPublishIntegrationTests"/> (which pins a community following a remote
/// community). The community half is exercised end-to-end: the <c>Follow</c> is authored to the
/// community's outbox (<c>POST /ap/v1/c/{name}/outbox</c>), A's <c>CommunityOutboxPublishHandler</c>
/// records C's local follows set and <em>server-delivers</em> the signed follow to bob's inbox on B; B's
/// <c>FollowActivityHandler</c> records the directed edge (C → bob) in B's follow store. The <c>Undo</c>
/// is authored the same way; B's <c>UndoActivityHandler</c> resolves the original follow from B's
/// activity store and removes the edge.
/// </remarks>
/// <para>
/// Topology: instance A (cf-unfollow-a.domain.local) hosts the local instance actor <c>alice</c> and the
/// community <c>iris</c> (with its own signing key). Instance B (cf-unfollow-b.domain.local) hosts the
/// local person <c>bob</c> (with its own signing key, so B can serve bob's document for signature
/// validation and bob exists as a real local actor). A's outbound delivery worker routes to B; A's
/// fetcher routes by actor-IRI host (alice/iris → A, bob → B). B's fetcher routes to A (to fetch the
/// community's document and validate the signature of the federated Follow/Undo). The client's writes
/// are signed POSTs to the community's outbox on A; the cross-instance hop (A → B) is made by A's
/// server, signed as the community.
/// </para>
[Collection("CommunityFollowsPersonUnfollowPropagation")]
public sealed class CommunityFollowsPersonUnfollowPropagationIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "cf-unfollow-a.domain.local";
    internal const string BHost = "cf-unfollow-b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";
    internal const string CommunityName = "iris";

    private readonly CommunityFollowsPersonUnfollowPropagationSharedHost _fixture;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private KeyPair _communityKey;
    private readonly Iri _communityIri;
    private KeyPair _bobKey;
    private readonly Iri _bobActorIri;

    public CommunityFollowsPersonUnfollowPropagationIntegrationTests(CommunityFollowsPersonUnfollowPropagationSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _communityIri = new Iri($"https://{AHost}/ap/v1/c/{CommunityName}");
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _aliceKey = null!;
        _communityKey = null!;
        _bobKey = null!;
        _aHttp = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence);

        _aPersistence.Keys.TryGetKey(new Iri($"{_aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        _aPersistence.Keys.TryGetKey(new Iri($"{_communityIri.Value}#key-1"), out var communityKey);
        _communityKey = (KeyPair)communityKey!;
        _bPersistence.Keys.TryGetKey(new Iri($"{_bobActorIri.Value}#key-1"), out var bobKey);
        _bobKey = (KeyPair)bobKey!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores alice + the community iris (on A) and bob (on B) with their existing keys.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var communityIri = new Iri($"https://{AHost}/ap/v1/c/{CommunityName}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedCommunityWithExistingKey(aPersistence, AHost, CommunityName, new Iri($"{communityIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"{bobIri.Value}#key-1"));
    }

    // --- A community-follow of a remote person federates A → B, and the community is recorded in B's
    //     follow store as bob's follower; the community's Undo(Follow) federates A → B and B removes the
    //     community from bob's followers. ---
    //
    // Step 1 (the outbound follow half): A publishes the community's Follow to the community's outbox;
    // A records C's local follows set (C now follows bob) and server-delivers the signed Follow to bob's
    // inbox on B; B validates the signature (fetching the community's key from A) and records the
    // directed edge C → bob in B's follow store — and, crucially, B's InboxProcessor stores the Follow in
    // B's activity store (the copy the Undo will later resolve against).
    //
    // Step 2 (the under-test un-follow half): A publishes the community's Undo(Follow) to the community's
    // outbox; A removes C's local follows edge and server-delivers the signed Undo to bob's inbox on B;
    // B's UndoActivityHandler resolves the original Follow from B's activity store, reads the
    // follower/target parties, and removes the C → bob edge. The edge is gone on B only if the Undo
    // federated AND B had stored the original Follow.

    [Fact]
    public async Task CommunityFollowOfRemotePerson_FederatesAndRecordsInRemoteFollowStore()
    {
        // Step 1a: publish the community's Follow to the community's own outbox (the write surface).
        var follow = BuildFollow(_communityIri, _bobActorIri);
        using var followRequest = SignedRequest(_communityIri, _communityKey, follow, $"/ap/v1/c/{CommunityName}/outbox");
        using var followResponse = await _aHttp.SendAsync(followRequest);
        Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);

        // A minted the Follow's id; learn it from the 2xx body (decision 055).
        var mintedFollowId = await LearnMintedIdAsync(followResponse);
        Assert.True(
            mintedFollowId is not null,
            "A should have returned the minted Follow id in the 2xx body.");

        // A recorded C's local follows set on publish (C now follows bob).
        Assert.True(
            (await _aPersistence.Communities.GetFollowsAsync(_communityIri)).Contains(_bobActorIri),
            "A should record the community's follows edge (C → bob) on publish.");

        // Step 1b: B recorded the edge — the Follow federated and was validated. This also means B stored
        // the Follow in its activity store, which Step 2's Undo resolution depends on.
        await WaitForAsync(
            () => _bPersistence.Follows.IsFollowingAsync(_communityIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(_communityIri, _bobActorIri),
            "B should have recorded the C → bob follow edge after A delivered the signed Follow.");

        // The community is listed among bob's followers (the inverse direction).
        Assert.True(
            (await _bPersistence.Follows.GetFollowersAsync(_bobActorIri)).Contains(_communityIri),
            "B should list the community among bob's followers after A delivered the signed Follow.");

        // B stored the original Follow in its activity store (the Undo's resolution depends on this).
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedFollowId!.Value, out _),
            "B should have stored the original Follow in its activity store (the Undo resolves against it).");
    }

    // --- The community's Undo(Follow) federates A → B and B removes the community from bob's followers.
    //
    // The un-follow half: the follow federates A → B (B records C → bob and stores the Follow), then the
    // Undo(Follow) federates A → B and B removes the edge. The edge is gone on B only if the Undo
    // federated AND B had stored the original Follow (the cross-instance dependency the single-instance
    // tests do not exercise).

    [Fact]
    public async Task CommunityUnfollowOfRemotePerson_FederatesAndRemovesFromRemoteFollowers()
    {
        // Step 1: establish the follow (see the dedicated follow test for the full outbound half).
        var follow = BuildFollow(_communityIri, _bobActorIri);
        using var followRequest = SignedRequest(_communityIri, _communityKey, follow, $"/ap/v1/c/{CommunityName}/outbox");
        using var followResponse = await _aHttp.SendAsync(followRequest);
        Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);

        var mintedFollowId = await LearnMintedIdAsync(followResponse);
        Assert.True(
            mintedFollowId is not null,
            "A should have returned the minted Follow id in the 2xx body.");

        // B recorded the edge (and stored the Follow in its activity store).
        await WaitForAsync(
            () => _bPersistence.Follows.IsFollowingAsync(_communityIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(_communityIri, _bobActorIri),
            "B should have recorded the C → bob follow edge before the un-follow.");

        // Step 2a: publish the community's Undo(Follow) to the community's own outbox.
        var undo = BuildUndo(_communityIri, mintedFollowId!.Value);
        using var undoRequest = SignedRequest(_communityIri, _communityKey, undo, $"/ap/v1/c/{CommunityName}/outbox");
        using var undoResponse = await _aHttp.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // Step 2b: A removed C's local follows edge (the outbox-publish handler's Undo branch).
        await WaitForAsync(
            async () => !(await _aPersistence.Communities.GetFollowsAsync(_communityIri)).Contains(_bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            (await _aPersistence.Communities.GetFollowsAsync(_communityIri)).Contains(_bobActorIri),
            "A should remove the community's follows edge (C → bob) when it publishes the Undo.");

        // Step 2c: B removed its edge — the cross-instance un-follow half. B's UndoActivityHandler
        // resolved the original Follow from B's activity store and called
        // RemoveFollowAsync(C, bob), removing the community from bob's followers set.
        await WaitForAsync(
            async () => !await _bPersistence.Follows.IsFollowingAsync(_communityIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _bPersistence.Follows.IsFollowingAsync(_communityIri, _bobActorIri),
            "B should remove the C → bob follow edge after A's server delivered the signed Undo.");

        // The community is no longer listed among bob's followers (the inverse direction).
        Assert.False(
            (await _bPersistence.Follows.GetFollowersAsync(_bobActorIri)).Contains(_communityIri),
            "B should no longer list the community among bob's followers after the un-follow.");
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Builds A's signing identity: a key store carrying <em>both</em> the instance actor's key and the
    /// community's key, a provider registering both at their correct IRIs, and a signer. The community
    /// key is registered at the community's IRI (not the instance actor's), so the outbound
    /// <c>DeliveryWorker</c> can sign the federated Follow/Undo as the community.
    /// </summary>
    internal static IdentityKeys BuildIdentityForA(
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
    /// Builds an id-less <see cref="Follow"/> from <paramref name="followerIri"/> (the community) to
    /// <paramref name="targetIri"/> (bob) (id-less: the server mints the activity's id on publish —
    /// decision 055).
    /// </summary>
    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// Builds an id-less <see cref="Undo"/> by <paramref name="actorIri"/> (the community) of the
    /// original follow <paramref name="originalFollowId"/> (the server-minted Follow IRI). The receiving
    /// instance resolves the original follow from its own activity store to determine the parties whose
    /// edge to remove (id-less: the server mints the Undo's own id on publish — decision 055).
    /// </summary>
    private static Undo BuildUndo(Iri actorIri, Iri originalFollowId) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(originalFollowId.Value) }],
    };

    /// <summary>
    /// Learns the server-minted id from a 2xx response body (decision 055: the server returns the
    /// created object in the 2xx body). Returns null when the body is empty or carries no id.
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

/// <summary>
/// Shared two-host fixture for <see cref="CommunityFollowsPersonUnfollowPropagationIntegrationTests"/>
/// (A: cf-unfollow-a.domain.local alice + iris community, B: cf-unfollow-b.domain.local bob). Seeds all
/// three identities with keys ONCE; wires cross-wired delivery + routing fetchers + multi-identity
/// signing via <see cref="SharedHostFixture.ServerRefFor"/>.
/// </summary>
public sealed class CommunityFollowsPersonUnfollowPropagationSharedHost : SharedTwoHostFixture
{
    public CommunityFollowsPersonUnfollowPropagationSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, CommunityFollowsPersonUnfollowPropagationIntegrationTests.AHost, CommunityFollowsPersonUnfollowPropagationIntegrationTests.Alice);
        var aCommunity = TestSeeder.SeedCommunityWithKey(aPersistence, CommunityFollowsPersonUnfollowPropagationIntegrationTests.AHost, CommunityFollowsPersonUnfollowPropagationIntegrationTests.CommunityName);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, CommunityFollowsPersonUnfollowPropagationIntegrationTests.BHost, CommunityFollowsPersonUnfollowPropagationIntegrationTests.Bob);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);
        var aliceIri = new Iri($"https://{CommunityFollowsPersonUnfollowPropagationIntegrationTests.AHost}/ap/v1/u/{CommunityFollowsPersonUnfollowPropagationIntegrationTests.Alice}");
        var communityIri = new Iri($"https://{CommunityFollowsPersonUnfollowPropagationIntegrationTests.AHost}/ap/v1/c/{CommunityFollowsPersonUnfollowPropagationIntegrationTests.CommunityName}");
        var bobIri = new Iri($"https://{CommunityFollowsPersonUnfollowPropagationIntegrationTests.BHost}/ap/v1/u/{CommunityFollowsPersonUnfollowPropagationIntegrationTests.Bob}");

        var optionsA = new ActivityPubHostOptions
        {
            Host = CommunityFollowsPersonUnfollowPropagationIntegrationTests.AHost,
            Handle = CommunityFollowsPersonUnfollowPropagationIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = CommunityFollowsPersonUnfollowPropagationIntegrationTests.BuildIdentityForA(aSeeded.Key, aliceIri, aCommunity.Key, communityIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = new CommunityFollowsPersonUnfollowPropagationIntegrationTests.RoutingFetcher(
                CommunityFollowsPersonUnfollowPropagationIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                CommunityFollowsPersonUnfollowPropagationIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                aSeeded.Key, aliceIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = CommunityFollowsPersonUnfollowPropagationIntegrationTests.BHost,
            Handle = CommunityFollowsPersonUnfollowPropagationIntegrationTests.Bob,
            Persistence = bPersistence,
            Fetcher = CommunityFollowsPersonUnfollowPropagationIntegrationTests.BuildFetcherFor(
                CommunityFollowsPersonUnfollowPropagationIntegrationTests.AHost,
                CommunityFollowsPersonUnfollowPropagationIntegrationTests.Alice,
                aSeeded.Key,
                new LazyHandler(() => serverARef().CreateHandler())),
        };

        return (optionsA, optionsB);
    }
}

/// <summary>
/// xunit collection definition for the community-unfollow-propagation shared two-host fixture.
/// </summary>
[CollectionDefinition("CommunityFollowsPersonUnfollowPropagation")]
public sealed class CommunityFollowsPersonUnfollowPropagationCollection : ICollectionFixture<CommunityFollowsPersonUnfollowPropagationSharedHost>
{
}
