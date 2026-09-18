using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iris.Server.Tests;

file static class MutualPeeringIris
{
    public static readonly Iri AliceIri = new("https://a.domain.local/ap/v1/u/alice");
    public static readonly Iri ACommunityIri = new("https://a.domain.local/ap/v1/c/iris");
    public static readonly Iri BCommunityIri = new("https://b.domain.local/ap/v1/c/interop");
}

/// <summary>
/// Phase 138.6 integration test (mutual peering handshake): two peered Iris instances each make their
/// community follow the other's community, and the test verifies the peering handshake completes on
/// <em>both</em> sides — each side's <c>following</c> collection lists the other, and each side's
/// <c>followers</c> collection lists the other. This drives 138.5 (Iris → remote-community discovery:
/// the outbound follow fetches + persists the peer's <see cref="Group"/> document) and the inbound
/// half of the handshake in one shot: the peer's <see cref="FollowActivityHandler"/> records the
/// directed edge and auto-accepts (no manual approval step — the expected platform behavior the slice
/// documents), and the follower's <see cref="AcceptActivityHandler"/> finalizes its own follow edge once
/// the <c>Accept</c> returns.
/// </summary>
/// <remarks>
/// Topology: two real Iris instances, A (a.domain.local, owner <c>alice</c> + community <c>iris</c>)
/// and B (b.domain.local, owner <c>bob</c> + community <c>interop</c>), cross-wired: A's outbound
/// delivery and fetcher route to B, and B's outbound delivery and fetcher route to A (a genuine
/// peer-to-peer federation, not a one-way fixture). Neither community sets
/// <c>manuallyApprovesFollowers</c>, so an inbound follow is auto-accepted by the followed side. Each
/// side's community follows the other via the existing
/// <c>POST /local/v1/c/{name}/follow/{targetIri}</c> endpoint (Phase 89.1), signed as the instance
/// actor (the community holds no client key, so the owner's Basic auth is the only credential).
/// </remarks>
[Collection("MutualPeeringHandshake")]
public sealed class MutualPeeringHandshakeIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "a.domain.local";
    internal const string BHost = "b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";
    internal const string ACommunity = "iris";
    internal const string BCommunity = "interop";

    private readonly MutualPeeringHandshakeSharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly HttpClient _httpA;
    private readonly HttpClient _httpB;
    private readonly string _baseA = $"https://{AHost}";
    private readonly string _baseB = $"https://{BHost}";

    public MutualPeeringHandshakeIntegrationTests(MutualPeeringHandshakeSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _httpA = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
        _httpB = new HttpClient(fixture.ServerB.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        // Re-add the community entries (without re-seeding keys) so the served documents carry the
        // publicKey from the keys already in the persistence key store (which survive Reset()).
        // Re-seeding keys here would replace the KeyPair instances that the IdentityKeys key store
        // holds, disposing the RSA material the DeliveryWorker signs with.
        ReSeedCommunityEntries(_aPersistence, AHost, Alice, ACommunity, MutualPeeringIris.ACommunityIri);
        ReSeedCommunityEntries(_bPersistence, BHost, Bob, BCommunity, MutualPeeringIris.BCommunityIri);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-adds a person and community to <paramref name="persistence"/> after a <c>Reset()</c>,
    /// WITHOUT re-seeding keys (the keys already in the persistence key store survive <c>Reset()</c>
    /// and must not be replaced, which would dispose the RSA material the <c>IdentityKeys</c> key
    /// store holds). Uses <c>SeedCommunityWithExistingKey</c> to serve the publicKey from the
    /// existing key.
    /// </summary>
    private static void ReSeedCommunityEntries(
        InMemoryPersistenceProvider persistence, string host, string owner, string community, Iri communityIri)
    {
        var ownerIri = TestSeeder.SeedPerson(persistence, host, owner);
        var keyId = new Iri($"{communityIri}#key-1");
        TestSeeder.SeedCommunityWithExistingKey(persistence, host, community, keyId);

        if (persistence.Communities.TryGetCommunityAsync(communityIri, out var c).GetAwaiter().GetResult()
            && c is not null)
        {
            c.AttributedTo = [new Link { Href = ownerIri.Uri }];
            persistence.Communities.PutCommunityAsync(c).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _httpA.Dispose();
        _httpB.Dispose();
        return Task.CompletedTask;
    }

    // --- Mutual follow: each side's /following lists the other ----------------------------------

    [Fact]
    public async Task MutualFollow_BothFollowingCollections_ListTheOther()
    {
        // A's community follows B's community; B's community follows A's community.
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(_httpA, _baseA, ACommunity, MutualPeeringIris.BCommunityIri, "alice:alice-password"));
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(_httpB, _baseB, BCommunity, MutualPeeringIris.ACommunityIri, "bob:bob-password"));

        // Wait for both outbound follow edges to be recorded (the follow endpoint records its edge
        // synchronously before returning 204).
        await WaitForAsync(
            async () => (await _aPersistence.Communities.GetFollowsAsync(MutualPeeringIris.ACommunityIri)).Contains(MutualPeeringIris.BCommunityIri)
                && (await _bPersistence.Communities.GetFollowsAsync(MutualPeeringIris.BCommunityIri)).Contains(MutualPeeringIris.ACommunityIri),
            timeout: TimeSpan.FromSeconds(30));

        // Each side's /following collection (read live) lists the other community — the peering edge is
        // visible on both ends.
        var aFollowing = JsonDoc.ItemIdsOf(await ReadCollectionAsync(_httpA, $"{_baseA}/ap/v1/c/{ACommunity}/following"));
        var bFollowing = JsonDoc.ItemIdsOf(await ReadCollectionAsync(_httpB, $"{_baseB}/ap/v1/c/{BCommunity}/following"));
        Assert.Contains(MutualPeeringIris.BCommunityIri.Value, aFollowing);
        Assert.Contains(MutualPeeringIris.ACommunityIri.Value, bFollowing);
    }

    // --- Mutual follow: each side's /followers lists the other (auto-accept, no manual approval) ---

    [Fact]
    public async Task MutualFollow_BothFollowersCollections_ListTheOther()
    {
        // A's community follows B's community; B's community follows A's community.
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(_httpA, _baseA, ACommunity, MutualPeeringIris.BCommunityIri, "alice:alice-password"));
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(_httpB, _baseB, BCommunity, MutualPeeringIris.ACommunityIri, "bob:bob-password"));

        // The followed side (B, for A's follow) records the follower in its community's followers set
        // and auto-accepts (no manual approval — the expected platform behavior). The follower side's
        // AcceptActivityHandler then finalizes the edge. Deterministically pump the round trip
        // (A→B Follow → B Accept → A, and the symmetric path) instead of relying on the background
        // DeliveryWorker (whose async continuations go unscheduled under full-suite load).
        await PumpDeliveryRoundTripAsync(
            async () => (await _aPersistence.Communities.GetFollowersAsync(MutualPeeringIris.ACommunityIri)).Contains(MutualPeeringIris.BCommunityIri)
                && (await _bPersistence.Communities.GetFollowersAsync(MutualPeeringIris.BCommunityIri)).Contains(MutualPeeringIris.ACommunityIri),
            timeout: TimeSpan.FromSeconds(30));

        // Each side's /followers collection (read live) lists the other community — the inverse peering
        // edge is visible on both ends, proving the handshake completed (not just the outbound follow).
        var aFollowers = JsonDoc.ItemIdsOf(await ReadCollectionAsync(_httpA, $"{_baseA}/ap/v1/c/{ACommunity}/followers"));
        var bFollowers = JsonDoc.ItemIdsOf(await ReadCollectionAsync(_httpB, $"{_baseB}/ap/v1/c/{BCommunity}/followers"));

        Assert.Contains(MutualPeeringIris.BCommunityIri.Value, aFollowers);
        Assert.Contains(MutualPeeringIris.ACommunityIri.Value, bFollowers);
    }

    // --- The peer's Group document is persisted on each side (138.5, both directions) ----------

    [Fact]
    public async Task MutualFollow_PeerGroupPersistedOnBothSides()
    {
        // A's community follows B's community; B's community follows A's community.
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(_httpA, _baseA, ACommunity, MutualPeeringIris.BCommunityIri, "alice:alice-password"));
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(_httpB, _baseB, BCommunity, MutualPeeringIris.ACommunityIri, "bob:bob-password"));

        // The outbound follow on each side fetches the peer's Group document (to resolve its inbox) and
        // persists it to the follower's durable community store (135.1's RemoteCommunityPersister). Wait
        // for both persisted documents to land (A has B's community; B has A's community).
        await WaitForAsync(
            async () => await _aPersistence.Communities.TryGetCommunityAsync(MutualPeeringIris.BCommunityIri, out _)
                && await _bPersistence.Communities.TryGetCommunityAsync(MutualPeeringIris.ACommunityIri, out _),
            timeout: TimeSpan.FromSeconds(120));

        // 138: the cached-actor-by-IRI endpoint is LOCAL-ONLY, so each side 404s for the REMOTE peer's
        // Group (the client reads it through the proxy endpoint instead). The peer's Group is still
        // persisted in each side's community store (the WaitForAsync above already asserted both are
        // present) — the peering handshake completed.
        var aServesB = await ActorByIriAsync(_httpA, _baseA, MutualPeeringIris.BCommunityIri);
        var bServesA = await ActorByIriAsync(_httpB, _baseB, MutualPeeringIris.ACommunityIri);
        Assert.Equal(HttpStatusCode.NotFound, aServesB);
        Assert.Equal(HttpStatusCode.NotFound, bServesA);

        // The peer's Group IS persisted on each side (the handshake completed, both directions).
        Assert.True(
            await _aPersistence.Communities.TryGetCommunityAsync(MutualPeeringIris.BCommunityIri, out _));
        Assert.True(
            await _bPersistence.Communities.TryGetCommunityAsync(MutualPeeringIris.ACommunityIri, out _));
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Issues a raw Basic-authenticated community-follow POST to
    /// <c>{base}/local/v1/c/{name}/follow/{targetIri}</c>. <paramref name="auth"/> is "user:pass" or null.
    /// </summary>
    private static async Task<HttpStatusCode> FollowAsync(
        HttpClient http, string baseUri, string community, Iri targetIri, string? auth)
    {
        var url = $"{baseUri}/local/v1/c/{community}/follow/{targetIri.Value.TrimStart('/')}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (auth is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth)));
        }

        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// Deterministically pumps the mutual-follow round trip: takes all pending jobs from both hosts'
    /// test delivery queues and delivers them inline (synchronously), repeating until the convergence
    /// condition is met or the timeout expires. Replaces the racy background <c>DeliveryWorker</c> pump
    /// (whose async continuations go unscheduled under full-suite load, leaving jobs stuck in-flight).
    /// </summary>
    private async Task PumpDeliveryRoundTripAsync(Func<Task<bool>> converged, TimeSpan timeout)
    {
        var factoryA = _fixture.ServerA.Services.GetRequiredService<IActivityPubClientFactory>();
        var factoryB = _fixture.ServerB.Services.GetRequiredService<IActivityPubClientFactory>();

        // A's client routes to B (A's outbound delivery target); B's routes to A.
        var clientA = factoryA.Create(
            new ActivityPubClientOptions { ActorId = MutualPeeringIris.ACommunityIri, EnableRetry = false },
            _fixture.ServerB.CreateHandler());
        var clientB = factoryB.Create(
            new ActivityPubClientOptions { ActorId = MutualPeeringIris.BCommunityIri, EnableRetry = false },
            _fixture.ServerA.CreateHandler());

        var driverA = new DeterministicDeliveryDriver(_fixture.QueueA, clientA);
        var driverB = new DeterministicDeliveryDriver(_fixture.QueueB, clientB);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // Pump A's queue (delivers A→B jobs, which may enqueue B→A accepts).
            var jobsA = _fixture.QueueA.TakeAll();
            foreach (var job in jobsA)
            {
                await DeliverJobInlineAsync(clientA, job);
            }

            // Pump B's queue (delivers B→A jobs, which may enqueue A→B accepts).
            var jobsB = _fixture.QueueB.TakeAll();
            foreach (var job in jobsB)
            {
                await DeliverJobInlineAsync(clientB, job);
            }

            if (await converged())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("Mutual follow round trip did not converge within the timeout.");
    }

    private static async Task DeliverJobInlineAsync(IActivityPubClient client, Iris.Server.Delivery.DeliveryJob job)
    {
        var json = ActivityJson.Serialize(job.Activity);
        var body = System.Text.Encoding.UTF8.GetBytes(json);

        using var request = new HttpRequestMessage(HttpMethod.Post, job.InboxIri.Value)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);

        if (job.ActorIri is { } actorIri)
        {
            request.Headers.Add("X-Iris-Actor", actorIri.Value);
        }

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// GETs a paged collection endpoint (with <c>?refresh=true</c> to bypass the page cache) and returns
    /// its serialized body.
    /// </summary>
    private static async Task<string> ReadCollectionAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url + "?refresh=true");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// GETs the cached-actor-by-IRI endpoint for <paramref name="iri"/> and returns the status (200 when
    /// the instance has cached the actor/community, 404 when it has not).
    /// </summary>
    private static async Task<HttpStatusCode> ActorByIriAsync(HttpClient http, string baseUri, Iri iri)
    {
        using var response = await http
            .GetAsync($"{baseUri}/ap/v1/actor?iri={Uri.EscapeDataString(iri.Value)}");
        return response.StatusCode;
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
    /// Seeds both peered instances: A (owner alice + community iris, alice owns it) and B (owner bob +
    /// community interop, bob owns it). Each community's <c>AttributedTo</c> is its owner so the
    /// credential validator recognizes the owner for <c>VerifyCommunityCreatorAsync</c>. Neither community
    /// sets <c>manuallyApprovesFollowers</c>, so an inbound follow is auto-accepted.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        SeedCommunityOwnedBy(aPersistence, AHost, Alice, ACommunity, MutualPeeringIris.ACommunityIri);
        SeedCommunityOwnedBy(bPersistence, BHost, Bob, BCommunity, MutualPeeringIris.BCommunityIri);
    }

    /// <summary>
    /// Seeds a person (owner) and a community on <paramref name="persistence"/>, and sets the
    /// community's <c>AttributedTo</c> to the owner's IRI so the owner is recognized for the
    /// community-follow write gate.
    /// </summary>
    private static void SeedCommunityOwnedBy(
        InMemoryPersistenceProvider persistence, string host, string owner, string community, Iri communityIri)
    {
        var ownerIri = TestSeeder.SeedPerson(persistence, host, owner);
        // Seed the community WITH a key so its served document carries a publicKey (the inbound side
        // fetches it to verify the community-authored Follow's signature). The key's IRI follows the
        // {communityIri}#key-1 convention (matching the host's ExtraLocalActors key registration).
        TestSeeder.SeedCommunityWithKey(persistence, host, community);

        if (persistence.Communities.TryGetCommunityAsync(communityIri, out var c).GetAwaiter().GetResult()
            && c is not null)
        {
            c.AttributedTo = [new Link { Href = ownerIri.Uri }];
            persistence.Communities.PutCommunityAsync(c).GetAwaiter().GetResult();
        }
    }
}

/// <summary>
/// Shared two-host fixture for <see cref="MutualPeeringHandshakeIntegrationTests"/>: two real Iris
/// instances, A (a.domain.local, owner alice + community iris) and B (b.domain.local, owner bob +
/// community interop), cross-wired so A's outbound delivery and fetcher route to B and B's route to A
/// (genuine peer-to-peer federation). Each host's fetcher carries a
/// <see cref="RemoteCommunityPersister"/> over its own community store (the production 135.1 wiring) so
/// the peer's fetched <see cref="Group"/> is persisted. Each host's community key is registered so the
/// outbound <c>DeliveryWorker</c> can sign the community-authored Follow/Accept as the community.
/// </summary>
public sealed class MutualPeeringHandshakeSharedHost : SharedTwoHostFixture
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";

    private readonly TestDeliveryQueue _queueA;
    private readonly TestDeliveryQueue _queueB;

    /// <summary>Host A's test delivery queue (the background worker idles; the test drives delivery).</summary>
    public TestDeliveryQueue QueueA => _queueA;

    /// <summary>Host B's test delivery queue (the background worker idles; the test drives delivery).</summary>
    public TestDeliveryQueue QueueB => _queueB;

    public MutualPeeringHandshakeSharedHost()
        : base(BuildOptions())
    {
        _queueA = ServerA.Services.GetRequiredService<TestDeliveryQueue>();
        _queueB = ServerB.Services.GetRequiredService<TestDeliveryQueue>();
    }

    /// <summary>
    /// Clears both hosts' persistence and collection-page caches (base behavior) AND both hosts'
    /// remote-actor and remote-key caches. The remote caches are per-server singletons that survive
    /// <see cref="IPersistenceProvider"/> resets, so a stale cached community document (e.g. one fetched
    /// without a <c>publicKey</c> by an earlier test) would otherwise persist across tests and mislead
    /// the inbound signature validation (the receiving instance resolves the sender's key from the
    /// cached document). Clearing them after each reset guarantees every test starts from a clean
    /// federation slate.
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        ClearRemoteCaches(ServerA);
        ClearRemoteCaches(ServerB);
    }

    private static void ClearRemoteCaches(Microsoft.AspNetCore.TestHost.TestServer server)
    {
        if (server.Services.GetService<RemoteActorCache>() is { } actorCache)
        {
            actorCache.Clear();
        }

        if (server.Services.GetService<RemoteKeyCache>() is { } keyCache)
        {
            keyCache.Clear();
        }
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();

        var aKeyStore = new InMemoryKeyStore();
        var bKeyStore = new InMemoryKeyStore();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, "alice");
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, BHost, "bob");
        aKeyStore.PutKey(aSeeded.Key);
        bKeyStore.PutKey(bSeeded.Key);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        var bKeyProvider = new InMemoryKeyProvider(bKeyStore);
        aKeyProvider.RegisterKey(aSeeded.ActorIri, aSeeded.Key.KeyId);
        bKeyProvider.RegisterKey(bSeeded.ActorIri, bSeeded.Key.KeyId);
        var aSigner = new HttpSignatureSigner(aKeyStore);
        var bSigner = new HttpSignatureSigner(bKeyStore);

        // Seed BOTH communities WITH keys so the community keys exist in the persistence key store at
        // {communityIri}#key-1. This must happen here (in BuildOptions), before the key provider is
        // finalized, because we need to load the community keys into the host's custom key store AND
        // register them with the host's IKeyProvider (the factory's own key registration is skipped when
        // we supply a custom IdentityKeys). The advertised publicKey in the served community document is
        // the SAME key, so the receiving instance resolves and verifies the community-authored
        // Follow/Accept signature from the fetched document.
        var aCommunityIri = new Iri($"https://{AHost}/ap/v1/c/{MutualPeeringHandshakeIntegrationTests.ACommunity}");
        var bCommunityIri = new Iri($"https://{BHost}/ap/v1/c/{MutualPeeringHandshakeIntegrationTests.BCommunity}");
        TestSeeder.SeedCommunityWithKey(aPersistence, AHost, MutualPeeringHandshakeIntegrationTests.ACommunity);
        TestSeeder.SeedCommunityWithKey(bPersistence, BHost, MutualPeeringHandshakeIntegrationTests.BCommunity);
        var aCommunityKeyId = new Iri($"{aCommunityIri}#key-1");
        var bCommunityKeyId = new Iri($"{bCommunityIri}#key-1");
        if (aPersistence.Keys.TryGetKey(aCommunityKeyId, out var aCommunityKey) && aCommunityKey is not null)
        {
            aKeyStore.PutKey(aCommunityKey);
            aKeyProvider.RegisterKey(aCommunityIri, aCommunityKeyId);
        }
        if (bPersistence.Keys.TryGetKey(bCommunityKeyId, out var bCommunityKey) && bCommunityKey is not null)
        {
            bKeyStore.PutKey(bCommunityKey);
            bKeyProvider.RegisterKey(bCommunityIri, bCommunityKeyId);
        }

        var aBaseUri = new Iri($"https://{AHost}/ap/v1");
        var bBaseUri = new Iri($"https://{BHost}/ap/v1");

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        // Test-only delivery queues: the background DeliveryWorker idles (TryDequeueAsync always
        // returns null), so the test drives delivery deterministically via TakeAll + inline POST.
        var queueA = new TestDeliveryQueue();
        var queueB = new TestDeliveryQueue();

        void PreServicesA(IServiceCollection s)
        {
            s.AddSingleton(queueA);
            s.AddSingleton<Iris.Server.Delivery.IDeliveryQueue>(queueA);
        }

        void PreServicesB(IServiceCollection s)
        {
            s.AddSingleton(queueB);
            s.AddSingleton<Iris.Server.Delivery.IDeliveryQueue>(queueB);
        }

        var optionsA = new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = "alice",
            Persistence = aPersistence,
            IdentityKeys = new IdentityKeys(aKeyStore, aKeyProvider, aSigner),
            // The community key is registered with A's provider so the DeliveryWorker can sign the
            // community-authored Follow/Accept as the community (the Accept the community sends back when
            // it follows B, and the Follow it sends when it follows B).
            ExtraLocalActors = [aCommunityIri],
            CredentialValidator = new BasicAuthCredentialValidator(
                (iri, username, password) => ValueTask.FromResult(
                    iri == MutualPeeringIris.AliceIri
                    && username == "alice"
                    && password == "alice-password")),
            // A's outbound delivery routes to B (the community-follow Follow is delivered to B's inbox).
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            // A's fetcher reaches B (resolves the peer community's inbox) AND persists the fetched peer
            // community's Group to A's community store — the 135.1 RemoteCommunityPersister path.
            Fetcher = BuildRemoteFetcher(AHost, "alice", aSeeded.Key, serverBRef, aPersistence.Communities, aBaseUri),
            PreServices = PreServicesA,
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = "bob",
            Persistence = bPersistence,
            IdentityKeys = new IdentityKeys(bKeyStore, bKeyProvider, bSigner),
            // The community key is registered with B's provider (signs the community-authored
            // Follow/Accept as the community).
            ExtraLocalActors = [bCommunityIri],
            CredentialValidator = new BasicAuthCredentialValidator(
                (iri, username, password) => ValueTask.FromResult(
                    iri == new Iri($"https://{BHost}/ap/v1/u/{MutualPeeringHandshakeIntegrationTests.Bob}")
                    && username == "bob"
                    && password == "bob-password")),
            // B's outbound delivery routes to A (the community-follow Follow is delivered to A's inbox).
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
            // B's fetcher reaches A (resolves the peer community's inbox) AND persists the fetched peer
            // community's Group to B's community store — the 135.1 RemoteCommunityPersister path.
            Fetcher = BuildRemoteFetcher(BHost, "bob", bSeeded.Key, serverARef, bPersistence.Communities, bBaseUri),
            PreServices = PreServicesB,
        };

        return (optionsA, optionsB);
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> (signed as the instance actor) whose client routes to
    /// the (deferred) <paramref name="targetServer"/>, carrying a <see cref="RemoteCommunityPersister"/>
    /// over <paramref name="communityStore"/> — the production fetcher's 135.1 wiring, mirrored for the
    /// test.
    /// </summary>
    private static IActorDocumentFetcher BuildRemoteFetcher(
        string host, string handle, KeyPair key, Func<TestServer> targetServer,
        Iris.Server.Stores.ICommunityStore communityStore, Iri instanceBase)
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
            new LazyHandler(() => targetServer().CreateHandler()));

        var communityPersister = new RemoteCommunityPersister(communityStore, instanceBase);
        return new IrisActorDocumentFetcher(client, new RemoteActorCache(), null, communityPersister);
    }
}

/// <summary>
/// xunit collection definition for the mutual-peering-handshake shared two-host fixture.
/// </summary>
[CollectionDefinition("MutualPeeringHandshake")]
public sealed class MutualPeeringHandshakeCollection
    : ICollectionFixture<MutualPeeringHandshakeSharedHost>
{
}
