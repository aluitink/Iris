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
using Xunit;

namespace Iris.Server.Tests;

file static class RemoteGroupDiscoveryIris
{
    public static readonly Iri AliceIri = new("https://a.domain.local/ap/v1/u/alice");
    public static readonly Iri CommunityIri = new("https://a.domain.local/ap/v1/c/iris");
    public static readonly Iri LemmyCommunityIri = new("https://lemmy.example/ap/v1/c/interop");
}

/// <summary>
/// Phase 138.5 integration test (Iris → remote-community discovery): a local community's operator makes
/// the community follow a <em>remote</em> community's <see cref="Group"/> actor (a non-Iris instance —
/// the live Lemmy community in the 138 fixtures) via the existing <c>POST /local/v1/c/{name}/follow/{targetIri}</c>
/// endpoint (Phase 89.1). Delivering that Follow requires the delivery service to fetch the target's
/// actor document (to resolve its <c>endpoints.sharedInbox</c>), and that fetch goes through the
/// instance's <see cref="IActorDocumentFetcher"/> — which, for a <see cref="Group"/>, persists the remote
/// community to the durable community store (135.1's <see cref="RemoteCommunityPersister"/> path). The
/// test proves this path against a real non-Iris <see cref="Group"/> (not a same-instance <c>TestServer</c>
/// fixture): after the follow, the remote community's IRI is readable on Iris via the cached-actor-by-IRI
/// endpoint (<c>GET /ap/v1/actor?iri=…</c>), the same endpoint the directory consults for known content.
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, owner <c>alice</c> + community <c>iris</c>, which alice owns)
/// and instance B (lemmy.example, a remote Lemmy-shaped host with community <c>interop</c>). A's fetcher
/// and delivery transport both route to B (so the community-follow delivery reaches B, and the inbox
/// resolution fetches B's <c>interop</c> <see cref="Group"/>). B's inbox is a no-op sink (a Lemmy
/// instance records the follow edge server-side; for this test the edge on B is irrelevant — the 138.5
/// acceptance is about A persisting B's community document). The community holds no client key, so the
/// follow is authored by the server (signed as the instance actor, as in production) — the owner's
/// Basic auth is the only credential.
/// </remarks>
[Collection("CommunityFollowRemoteGroupDiscovery")]
public sealed class CommunityFollowRemoteGroupDiscoveryIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "a.domain.local";
    internal const string BHost = "lemmy.example";
    internal const string Alice = "alice";
    internal const string Community = "iris";
    internal const string RemoteCommunity = "interop";

    private readonly CommunityFollowRemoteGroupDiscoverySharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly HttpClient _httpA;
    private readonly string _baseA = $"https://{AHost}";

    public CommunityFollowRemoteGroupDiscoveryIntegrationTests(
        CommunityFollowRemoteGroupDiscoverySharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _httpA = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, (InMemoryPersistenceProvider)_fixture.PersistenceB);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _httpA.Dispose();
        return Task.CompletedTask;
    }

    // --- Following a remote community persists its Group document, served by the actor-by-IRI endpoint ---

    [Fact]
    public async Task Follow_RemoteCommunity_PersistsGroup_ServedByActorByIri()
    {
        // Before the follow, Iris has never seen the remote community — the actor-by-IRI endpoint 404s.
        Assert.Equal(HttpStatusCode.NotFound, await ActorByIriAsync(RemoteGroupDiscoveryIris.LemmyCommunityIri));

        // The community's owner (alice) makes the community follow the remote Lemmy community.
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(RemoteGroupDiscoveryIris.LemmyCommunityIri, auth: "alice:alice-password"));

        // The follow edge is recorded synchronously on A (the community now follows the remote community).
        Assert.Contains(
            RemoteGroupDiscoveryIris.LemmyCommunityIri,
            await _aPersistence.Communities.GetFollowsAsync(RemoteGroupDiscoveryIris.CommunityIri));

        // The delivery of the Follow runs on the background worker (the endpoint returns 204 once the
        // Follow is enqueued, before the worker delivers it). The delivery resolves the remote
        // community's inbox (fetching its Group document), which the fetcher persists to A's durable
        // community store (135.1's RemoteCommunityPersister). Wait for that async effect to land.
        await WaitForAsync(
            async () => await _aPersistence.Communities.TryGetCommunityAsync(
                RemoteGroupDiscoveryIris.LemmyCommunityIri, out _),
            timeout: TimeSpan.FromSeconds(15));

        Assert.True(
            await _aPersistence.Communities.TryGetCommunityAsync(RemoteGroupDiscoveryIris.LemmyCommunityIri, out var persisted),
            "following the remote community should persist its Group document to A's community store");
        Assert.NotNull(persisted);
        Assert.Equal(RemoteGroupDiscoveryIris.LemmyCommunityIri.Value, persisted!.Id);

        // 138: the cached-actor-by-IRI endpoint is LOCAL-ONLY, so it 404s for the REMOTE community
        // (the client reads it through the proxy endpoint instead). The community is still persisted
        // in A's community store — assert the round-trip against the STORE (serialized): the
        // persisted document is B's own Group (its inbox/outbox point at B, not A).
        var actorRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{_baseA}/ap/v1/actor?iri={Uri.EscapeDataString(RemoteGroupDiscoveryIris.LemmyCommunityIri.Value)}");
        actorRequest.Headers.Accept.ParseAdd("application/activity+json");
        var response = await _httpA.SendAsync(actorRequest);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = JsonDocument.Parse(ActivityJson.Serialize(persisted));
        Assert.Equal(RemoteGroupDiscoveryIris.LemmyCommunityIri.Value, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Group", doc.RootElement.GetProperty("type").GetString());
        // The persisted document is B's own Group (its inbox/outbox point at B, not A).
        var inbox = doc.RootElement.GetProperty("inbox").GetString();
        Assert.StartsWith($"https://{BHost}/", inbox);
    }

    // --- The community's /following collection lists the remote community after a follow ----------

    [Fact]
    public async Task Follow_RemoteCommunity_AppearInFollowingCollection()
    {
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(RemoteGroupDiscoveryIris.LemmyCommunityIri, auth: "alice:alice-password"));

        // The community's /following collection (read live, bypassing the page cache) lists the remote
        // community's IRI — the peering edge is visible to the community's members.
        var response = await _httpA.GetAsync($"{_baseA}/ap/v1/c/{Community}/following?refresh=true");
        response.EnsureSuccessStatusCode();
        var items = JsonDoc.ItemIdsOf(await response.Content.ReadAsStringAsync());
        Assert.Contains(RemoteGroupDiscoveryIris.LemmyCommunityIri.Value, items);
    }

    // --- A non-owner cannot drive the community follow (403) --------------------------------------
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

    [Fact]
    public async Task Follow_RemoteCommunity_Unauthenticated_IsRejected()
    {
        var status = await FollowAsync(RemoteGroupDiscoveryIris.LemmyCommunityIri, auth: null);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        // No edge, no persisted community (the write was rejected before delivery).
        Assert.Empty(await _aPersistence.Communities.GetFollowsAsync(RemoteGroupDiscoveryIris.CommunityIri));
        Assert.False(await _aPersistence.Communities.TryGetCommunityAsync(RemoteGroupDiscoveryIris.LemmyCommunityIri, out _));
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Issues a raw Basic-authenticated community-follow POST to <c>/local/v1/c/{name}/follow/{targetIri}</c>
    /// on instance A. <paramref name="auth"/> is "user:pass" or null (no auth).
    /// </summary>
    private async Task<HttpStatusCode> FollowAsync(Iri targetIri, string? auth)
    {
        var url = $"{_baseA}/local/v1/c/{Community}/follow/{targetIri.Value.TrimStart('/')}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (auth is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth)));
        }

        using var response = await _httpA.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// GETs the cached-actor-by-IRI endpoint for <paramref name="iri"/> on instance A and returns the
    /// status (200 when the instance has cached the actor/community, 404 when it has not).
    /// </summary>
    private async Task<HttpStatusCode> ActorByIriAsync(Iri iri)
    {
        using var response = await _httpA
            .GetAsync($"{_baseA}/ap/v1/actor?iri={Uri.EscapeDataString(iri.Value)}");
        return response.StatusCode;
    }

    /// <summary>
    /// Seeds the community <c>iris</c> on A whose <c>AttributedTo</c> is <c>alice</c> (so the credential
    /// validator recognizes alice as the community's owner for <c>VerifyCommunityCreatorAsync</c>), and
    /// re-seeds the remote community on B (with a key) after <see cref="SharedTwoHostFixture.Reset"/>
    /// wiped it. B's community key is re-registered in B's key store so B can sign/serve it.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        var aliceIri = TestSeeder.SeedPerson(aPersistence, AHost, Alice);
        TestSeeder.SeedCommunity(aPersistence, AHost, Community);

        // Set iris's AttributedTo to alice (the owner) so VerifyCommunityCreatorAsync passes for alice.
        if (aPersistence.Communities.TryGetCommunityAsync(RemoteGroupDiscoveryIris.CommunityIri, out var iris).GetAwaiter().GetResult()
            && iris is not null)
        {
            iris.AttributedTo = [new Link { Href = aliceIri.Uri }];
            aPersistence.Communities.PutCommunityAsync(iris).GetAwaiter().GetResult();
        }

        // Re-seed the remote community on B (Reset() wiped it). The key store survives Reset, but the
        // community's key is re-generated here, so re-register it in B's key store for serving.
        TestSeeder.SeedCommunityWithKey(bPersistence, BHost, RemoteGroupDiscoveryIris.LemmyCommunityIri.Value.Split('/').Last());
    }
}

/// <summary>
/// Shared two-host fixture for <see cref="CommunityFollowRemoteGroupDiscoveryIntegrationTests"/> (A:
/// a.domain.local, owner alice + community iris; B: lemmy.example, the remote Lemmy-shaped host with
/// community interop). A's fetcher and delivery transport both route to B, and A's fetcher carries a
/// <see cref="RemoteCommunityPersister"/> over A's community store (mirroring the production fetcher's
/// 135.1 wiring) so a fetched remote <see cref="Group"/> is persisted. B's remote community is seeded
/// ONCE with a key (its <c>publicKey</c> is served for the fetch).
/// </summary>
public sealed class CommunityFollowRemoteGroupDiscoverySharedHost : SharedTwoHostFixture
{
    private const string AHost = "a.domain.local";
    private const string BHost = "lemmy.example";

    public CommunityFollowRemoteGroupDiscoverySharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();

        // B hosts the remote Lemmy community (interop) with a key, so its Group document serves a
        // publicKey for the fetch. The remote community is the "Lemmy-shaped" actor (a Group, not an
        // Iris person).
        TestSeeder.SeedCommunityWithKey(bPersistence, BHost, RemoteGroupDiscoveryIris.LemmyCommunityIri.Value.Split('/').Last());

        var aKeyStore = new InMemoryKeyStore();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, "alice");
        aKeyStore.PutKey(aSeeded.Key);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        aKeyProvider.RegisterKey(aSeeded.ActorIri, aSeeded.Key.KeyId);
        var aSigner = new HttpSignatureSigner(aKeyStore);
        // The persister's local-prefix base: A's local communities are at https://a.domain.local/ap/v1/c/…,
        // so the base must be the /ap/v1 prefix (not the bare host) for the prefix check to match.
        var aBaseUri = new Iri($"https://{AHost}/ap/v1");

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var optionsA = new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = "alice",
            Persistence = aPersistence,
            IdentityKeys = new IdentityKeys(aKeyStore, aKeyProvider, aSigner),
            CredentialValidator = new BasicAuthCredentialValidator(
                (iri, username, password) => ValueTask.FromResult(
                    iri == RemoteGroupDiscoveryIris.AliceIri
                    && username == "alice"
                    && password == "alice-password")),
            // A's outbound delivery routes to B (the community-follow Follow is delivered to B's inbox).
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            // A's fetcher reaches B (resolves the remote community's inbox) AND persists fetched remote
            // communities (the interop Group) to A's community store — the 135.1 RemoteCommunityPersister
            // path this slice proves live against a non-Iris Group.
            Fetcher = BuildRemoteFetcher(AHost, "alice", aSeeded.Key, serverBRef, aPersistence.Communities, aBaseUri),
        };

        // B only needs to SERVE the remote community's Group document (for A's fetch); it does no
        // outbound delivery in this test, so no local actor key registration is required.
        var optionsB = new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = "lemmyadmin",
            Persistence = bPersistence,
            RegisterLocalKey = false,
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
/// xunit collection definition for the community-follow-remote-group shared two-host fixture.
/// </summary>
[CollectionDefinition("CommunityFollowRemoteGroupDiscovery")]
public sealed class CommunityFollowRemoteGroupDiscoveryCollection
    : ICollectionFixture<CommunityFollowRemoteGroupDiscoverySharedHost>
{
}
