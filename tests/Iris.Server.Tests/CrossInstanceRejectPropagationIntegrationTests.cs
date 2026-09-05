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
/// Cross-instance <see cref="Reject"/> (declined follow) propagation: a <em>follower</em> on instance A
/// (a person or a community) follows a local actor on instance B (federating A → B, where B records the
/// remote follow edge and stores the original <see cref="Follow"/>); the followed side (B) then
/// <em>Rejects</em> the follow (publishing a <see cref="Reject"/> to its own outbox), and B server-delivers
/// that <c>Reject</c> back to the follower's inbox on A; A's <see cref="RejectActivityHandler"/> removes
/// A's local follow edge.
/// </summary>
/// <remarks>
/// This is the inverse of the existing cross-instance <c>Accept</c> path. The authoring half of a
/// <c>Reject</c> (a local actor publishing a <c>Reject</c> to its outbox, the local edge removal, and the
/// server→server delivery of the <c>Reject</c> to the remote follower's inbox) already works and is
/// locked single-instance by <c>OutboxFollowDecisionIntegrationTests.OutboxReject_RemoteFollow_…</c>;
/// this slice locks the <em>cross-instance</em> half — that the <c>Reject</c> actually reaches the
/// follower's inbox on A and A's <see cref="RejectActivityHandler"/> removes A's follow edge.
/// <para>
/// The community-follower test additionally locks a real code fix: <see cref="RejectActivityHandler"/>
/// previously lacked the G-3 community-follower override that <see cref="AcceptActivityHandler"/> has
/// (a community is a <c>Group</c>, not a person in the actor store, so the base local-actor guard does not
/// see it as local). Without the fix, a community-initiated follow is finalized by an inbound
/// <c>Accept</c> but a <em>declined</em> follow is never undone on the community side (asymmetric with the
/// person path). This handler now mirrors the <see cref="AcceptActivityHandler"/> community arm: the local
/// check is widened to cover a local community, and a community's rejected follow is undone by removing the
/// edge from the community's follows set.
/// </para>
/// </remarks>
/// <para>
/// Topology: instance A (reject-a.domain.local) hosts the local instance actor <c>alice</c> (the person
/// follower) and the community <c>iris</c> (the community follower; C, with its own signing key). Instance B
/// (reject-b.domain.local) hosts the local person <c>bob</c> (B's instance actor, the follow target, so B's
/// document fetcher is routable). A's outbound delivery worker routes to B; A's fetcher routes by actor-IRI
/// host (alice/iris → A, bob → B). B's outbound delivery worker routes to A (it delivers the signed
/// <c>Reject</c> to the follower's inbox on A); B's fetcher routes to A (to fetch the follower's document and
/// validate the signature of the federated Follow). The client's writes are signed POSTs to the outbox on the
/// authoring instance; each cross-instance hop (A → B for the Follow, B → A for the Reject) is made by that
/// instance's server, signed as the acting local actor.
/// </para>
[Collection("CrossInstanceRejectPropagation")]
public sealed class CrossInstanceRejectPropagationIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "reject-a.domain.local";
    internal const string BHost = "reject-b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";
    internal const string CommunityName = "iris";

    private readonly CrossInstanceRejectPropagationSharedHost _fixture;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private KeyPair _communityKey;
    private readonly Iri _communityIri;
    private KeyPair _bobKey;
    private readonly Iri _bobActorIri;

    public CrossInstanceRejectPropagationIntegrationTests(CrossInstanceRejectPropagationSharedHost fixture)
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
        _bHttp = new HttpClient(fixture.ServerB.CreateHandler(), disposeHandler: false);
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
        _bHttp.Dispose();
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

    // --- A person on A follows a person on B (federates A → B); B Rejects the follow (publishes a
    //     Reject to its outbox), B server-delivers the Reject to A, and A's RejectActivityHandler removes
    //     A's local follow edge (alice → bob). ---
    //
    // Step 1 (the outbound follow half): alice publishes Follow(bob) to her outbox on A; A records alice's
    // local follow edge (alice → bob) in A's person follow store and server-delivers the signed Follow to
    // bob's inbox on B; B's FollowActivityHandler records the remote edge (bob's followers set lists alice)
    // and stores the Follow in B's activity store (the copy the Reject will later resolve against).
    //
    // Step 2 (the under-test cross-instance Reject half): bob publishes Reject(follow) to his outbox on B;
    // B's RecordFollowDecisionLocalAsync removes B's edge (bob's followers) and returns alice's IRI (the
    // remote follower), so B's outbox-publish handler server-delivers the signed Reject to alice's inbox on
    // A; A's RejectActivityHandler (inbound) resolves the original Follow from A's activity store and
    // removes A's local follow edge (alice → bob). The edge is gone on A only if the Reject federated B → A
    // AND A had stored the original Follow (the cross-instance dependency the single-instance test does not
    // exercise).

    [Fact]
    public async Task PersonFollowOfRemoteActor_ThenReject_FederatesAndRemovesFollowerEdgeOnOrigin()
    {
        // Step 1a: alice publishes Follow(bob) to her outbox on A.
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        using var followRequest = SignedRequest(_fixture.ServerA, _aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox");
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
        // activity store (the Reject's resolution depends on this).
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
            "B should have stored the original Follow in its activity store (the Reject resolves against it).");

        // Step 2a: bob publishes Reject(follow) to his outbox on B (the followed side declines the follow).
        var reject = BuildReject(_bobActorIri, mintedFollowId!.Value);
        using var rejectRequest = SignedRequest(_fixture.ServerB, _bobActorIri, _bobKey, reject, $"/ap/v1/u/{Bob}/outbox");
        using var rejectResponse = await _bHttp.SendAsync(rejectRequest);
        Assert.Equal(HttpStatusCode.Accepted, rejectResponse.StatusCode);

        // Step 2b: B removed its edge (bob's followers no longer list alice) on the local Reject publish.
        await WaitForAsync(
            async () => !(await _bPersistence.Follows.GetFollowersAsync(_bobActorIri)).Contains(_aliceActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            (await _bPersistence.Follows.GetFollowersAsync(_bobActorIri)).Contains(_aliceActorIri),
            "B should remove its edge (bob's followers) when it publishes the Reject.");

        // Step 2c: A removed its local follow edge — the cross-instance Reject half. B server-delivered the
        // signed Reject to alice's inbox on A; A's RejectActivityHandler resolved the original Follow from
        // A's activity store and removed the alice → bob edge.
        await WaitForAsync(
            async () => !await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "A should remove alice's follow edge (alice → bob) after B server-delivered the signed Reject.");
    }

    // --- A community C on A follows a person on B (federates A → B); B Rejects the follow, and A's
    //     RejectActivityHandler (the G-3 community-follower override) removes the community's follows edge.
    //     This locks the real code fix: without the override, a community is not a local person (the base
    //     local-actor guard no-ops), so the community's rejected follow is never undone on A.

    [Fact]
    public async Task CommunityFollowOfRemoteActor_ThenReject_FederatesAndRemovesCommunityFollowsEdge()
    {
        // Step 1a: the community C publishes Follow(bob) to its outbox on A.
        var follow = BuildFollow(_communityIri, _bobActorIri);
        using var followRequest = SignedRequest(_fixture.ServerA, _communityIri, _communityKey, follow, $"/ap/v1/c/{CommunityName}/outbox");
        using var followResponse = await _aHttp.SendAsync(followRequest);
        Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);

        var mintedFollowId = await LearnMintedIdAsync(followResponse);
        Assert.True(mintedFollowId is not null, "A should have returned the minted Follow id in the 2xx body.");

        // A recorded C's local follows set on publish (C → bob).
        Assert.True(
            (await _aPersistence.Communities.GetFollowsAsync(_communityIri)).Contains(_bobActorIri),
            "A should record the community's follows edge (C → bob) on publish.");

        // Step 1b: B recorded the remote edge (bob's followers list C) and stored the Follow in its activity
        // store (the Reject's resolution depends on this).
        await WaitForAsync(
            async () =>
            {
                var followers = await _bPersistence.Follows.GetFollowersAsync(_bobActorIri);
                return followers.Contains(_communityIri)
                    && await _bPersistence.Activities.TryGetActivityAsync(mintedFollowId!.Value, out _);
            },
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            (await _bPersistence.Follows.GetFollowersAsync(_bobActorIri)).Contains(_communityIri),
            "B should list the community C among bob's followers after A delivered the signed Follow.");

        // Step 2a: bob publishes Reject(follow) to his outbox on B.
        var reject = BuildReject(_bobActorIri, mintedFollowId!.Value);
        using var rejectRequest = SignedRequest(_fixture.ServerB, _bobActorIri, _bobKey, reject, $"/ap/v1/u/{Bob}/outbox");
        using var rejectResponse = await _bHttp.SendAsync(rejectRequest);
        Assert.Equal(HttpStatusCode.Accepted, rejectResponse.StatusCode);

        // Step 2b: B removed its edge (bob's followers no longer list C).
        await WaitForAsync(
            async () => !(await _bPersistence.Follows.GetFollowersAsync(_bobActorIri)).Contains(_communityIri),
            timeout: TimeSpan.FromSeconds(30));

        // Step 2c: A removed C's local follows edge — the cross-instance Reject half AND the G-3 community
        // fix. B server-delivered the signed Reject to the community C's inbox on A; A's RejectActivityHandler
        // (with the community-follower override) resolved the original Follow from A's activity store and
        // removed the C → bob edge from the community's follows set. Without the override, the base
        // local-actor guard would not see C as local and the edge would survive.
        await WaitForAsync(
            async () => !(await _aPersistence.Communities.GetFollowsAsync(_communityIri)).Contains(_bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            (await _aPersistence.Communities.GetFollowsAsync(_communityIri)).Contains(_bobActorIri),
            "A should remove the community's follows edge (C → bob) after B server-delivered the signed Reject " +
            "(the G-3 community-follower override in RejectActivityHandler).");
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Builds A's signing identity: a key store carrying <em>both</em> the instance actor's key and the
    /// community's key, a provider registering both at their correct IRIs, and a signer. The community key
    /// is registered at the community's IRI (not the instance actor's), so the outbound
    /// <c>DeliveryWorker</c> can sign the federated Follow as the community.
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
    /// author's outbox, addressed to <paramref name="server"/>. Uses the client pipeline (via a capture
    /// handler) to produce a correctly signed request, then replays the signed headers onto a fresh request
    /// for delivery to that instance's TestServer.
    /// </summary>
    private HttpRequestMessage SignedRequest(
        TestServer server, Iri actorIri, KeyPair key, Activity activity, string path)
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
    /// Builds an id-less <see cref="Reject"/> by <paramref name="actorIri"/> (the followed side, bob) of the
    /// original follow <paramref name="originalFollowId"/> (the server-minted Follow IRI). The receiving
    /// instance resolves the original follow from its own activity store to determine the parties whose edge
    /// to remove (id-less: the server mints the Reject's own id on publish — decision 055).
    /// </summary>
    private static Reject BuildReject(Iri actorIri, Iri originalFollowId) => new()
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
/// Shared two-host fixture for <see cref="CrossInstanceRejectPropagationIntegrationTests"/> (A:
/// reject-a.domain.local alice + iris community, B: reject-b.domain.local bob). Seeds all three
/// identities with keys ONCE; wires cross-wired delivery + routing fetchers + multi-identity signing
/// via <see cref="SharedHostFixture.ServerRefFor"/>.
/// </summary>
public sealed class CrossInstanceRejectPropagationSharedHost : SharedTwoHostFixture
{
    public CrossInstanceRejectPropagationSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, CrossInstanceRejectPropagationIntegrationTests.AHost, CrossInstanceRejectPropagationIntegrationTests.Alice);
        var aCommunity = TestSeeder.SeedCommunityWithKey(aPersistence, CrossInstanceRejectPropagationIntegrationTests.AHost, CrossInstanceRejectPropagationIntegrationTests.CommunityName);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, CrossInstanceRejectPropagationIntegrationTests.BHost, CrossInstanceRejectPropagationIntegrationTests.Bob);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);
        var aliceIri = new Iri($"https://{CrossInstanceRejectPropagationIntegrationTests.AHost}/ap/v1/u/{CrossInstanceRejectPropagationIntegrationTests.Alice}");
        var communityIri = new Iri($"https://{CrossInstanceRejectPropagationIntegrationTests.AHost}/ap/v1/c/{CrossInstanceRejectPropagationIntegrationTests.CommunityName}");
        var bobIri = new Iri($"https://{CrossInstanceRejectPropagationIntegrationTests.BHost}/ap/v1/u/{CrossInstanceRejectPropagationIntegrationTests.Bob}");

        var optionsA = new ActivityPubHostOptions
        {
            Host = CrossInstanceRejectPropagationIntegrationTests.AHost,
            Handle = CrossInstanceRejectPropagationIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = CrossInstanceRejectPropagationIntegrationTests.BuildIdentityForA(aSeeded.Key, aliceIri, aCommunity.Key, communityIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = new CrossInstanceRejectPropagationIntegrationTests.RoutingFetcher(
                CrossInstanceRejectPropagationIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                CrossInstanceRejectPropagationIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                aSeeded.Key, aliceIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = CrossInstanceRejectPropagationIntegrationTests.BHost,
            Handle = CrossInstanceRejectPropagationIntegrationTests.Bob,
            Persistence = bPersistence,
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
            Fetcher = new CrossInstanceRejectPropagationIntegrationTests.RoutingFetcher(
                CrossInstanceRejectPropagationIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                CrossInstanceRejectPropagationIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                bSeeded.Key, bobIri),
        };

        return (optionsA, optionsB);
    }
}

/// <summary>
/// xunit collection definition for the cross-instance-reject-propagation shared two-host fixture.
/// </summary>
[CollectionDefinition("CrossInstanceRejectPropagation")]
public sealed class CrossInstanceRejectPropagationCollection : ICollectionFixture<CrossInstanceRejectPropagationSharedHost>
{
}
