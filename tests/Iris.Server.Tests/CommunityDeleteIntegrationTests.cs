using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;
using KeyPair = Iris.Core.Identity.KeyPair;

namespace Iris.Server.Tests;

public sealed class CommunityDeleteIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly IActivityPubClient _client;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _aliceIri;
    private readonly Iri _bobIri;

    public CommunityDeleteIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        var aliceSeeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        _aliceIri = aliceSeeded.ActorIri;
        _bobIri = TestSeeder.SeedPerson(_persistence, AHost, Bob);

        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(
                iri == _aliceIri && username == Alice && password == "alice-password"));

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _persistence,
            CredentialValidator = credentialValidator,
            Fetcher = BuildSelfFetcher(_persistence, _aliceIri, () => _server!.CreateHandler()),
            ExtraLocalActors = [_bobIri],
        });

        _http = _server.CreateClient();
        _client = BuildClient(_aliceIri, aliceSeeded.Key, _server.CreateHandler());
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task DeleteCommunity_Creator_Succeeds()
    {
        var communityIri = await CreateCommunityAsync();

        var response = await _http.SendAsync(DeleteRequest(communityIri));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var docResponse = await _http.GetAsync($"{communityIri}");
        Assert.Equal(HttpStatusCode.NotFound, docResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteCommunity_NonCreator_Forbidden()
    {
        var communityIri = await CreateCommunityAsync();

        var request = new HttpRequestMessage(HttpMethod.Delete, $"https://{AHost}/local/v1/c/devs");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:bob-password")));

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var docResponse = await _http.GetAsync($"{communityIri}");
        Assert.Equal(HttpStatusCode.OK, docResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteCommunity_Unknown_NotFound()
    {
        var response = await _http.SendAsync(DeleteRequest(new Iri($"https://{AHost}/ap/v1/c/nonexistent")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCommunity_RemovesFollowers()
    {
        var communityIri = await CreateCommunityAsync();

        await _persistence.Communities.AddFollowerAsync(communityIri, _bobIri);

        var followers = await _persistence.Communities.GetFollowersAsync(communityIri);
        Assert.Contains(_bobIri, followers);

        var response = await _http.SendAsync(DeleteRequest(communityIri));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.False(await _persistence.Communities.TryGetCommunityAsync(communityIri, out _));
    }

    [Fact]
    public async Task DeleteCommunity_RemovesCreatorAutoFollowEdge_GoneFromFollowing()
    {
        // S40: the community-creation path (S21) records an auto-follow edge creator -> community
        // (EdgeKind.Follow, kind 0) via the Follows store. Deleting the community must remove that
        // inbound edge so the deleted community does NOT linger in the creator's /following collection
        // and Communities "Following" tab (it would otherwise 404 on re-fetch). Before the fix only the
        // community-scoped edges (incl. the kind-10 CommunityFollower edge) were removed, orphaning this
        // kind-0 edge.
        var communityIri = await CreateCommunityAsync();

        // S21 precondition: the creator is auto-followed on creation (a Follow edge alice -> community).
        Assert.True(await _persistence.Follows.IsFollowingAsync(_aliceIri, communityIri),
            "precondition: the creator auto-follows the newly created community (S21)");

        // Warm the creator's /following page in its post-creation state (the community IS listed).
        using (var warm = await _http.GetAsync($"https://{AHost}/ap/v1/u/{Alice}/following"))
        {
            warm.EnsureSuccessStatusCode();
            var warmBody = await warm.Content.ReadAsStringAsync();
            Assert.Contains(communityIri.Value, warmBody, StringComparison.Ordinal);
        }

        // Delete the community.
        var response = await _http.SendAsync(DeleteRequest(communityIri));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The community is gone from the community store...
        Assert.False(await _persistence.Communities.TryGetCommunityAsync(communityIri, out _));

        // S40: the inbound auto-follow (kind 0) edge is removed — the creator no longer "follows" the
        // deleted community.
        Assert.False(await _persistence.Follows.IsFollowingAsync(_aliceIri, communityIri),
            "S40: deleting the community must remove the creator's auto-follow (Follow kind 0) edge");
        Assert.Empty(await _persistence.Follows.GetFollowersAsync(communityIri)); // S40: no inbound Follow (kind 0) edges

        // ...and a fresh (non-cached) read of the creator's /following no longer lists the community.
        using var read = await _http.GetAsync(
            $"https://{AHost}/ap/v1/u/{Alice}/following?refresh=true");
        read.EnsureSuccessStatusCode();
        var readBody = await read.Content.ReadAsStringAsync();
        Assert.DoesNotContain(communityIri.Value, readBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteCommunity_RemovesModerationEdges()
    {
        var communityIri = await CreateCommunityAsync();

        await _persistence.Communities.AddBlockAsync(communityIri, _bobIri);
        await _persistence.Communities.AddMuteAsync(communityIri, _bobIri);
        await _persistence.Communities.AddFollowAsync(communityIri, _bobIri);

        var response = await _http.SendAsync(DeleteRequest(communityIri));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.False(await _persistence.Communities.TryGetCommunityAsync(communityIri, out _));
    }

    private async Task<Iri> CreateCommunityAsync()
    {
        var result = await _client.CreateCommunityAsync(_aliceIri, "devs", "Devs Community");
        Assert.True(result.IsSuccess, $"Community creation failed: HTTP {result.StatusCode}");
        return new Iri($"https://{AHost}/ap/v1/c/devs");
    }

    private static HttpRequestMessage DeleteRequest(Iri communityIri)
    {
        var name = communityIri.Value[(communityIri.Value.IndexOf("/c/") + 3)..];
        return new HttpRequestMessage(HttpMethod.Delete, $"https://{AHost}/local/v1/c/{name}")
        {
            Headers =
            {
                Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:alice-password"))),
            },
        };
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

    private static IActorDocumentFetcher BuildSelfFetcher(
        InMemoryPersistenceProvider persistence,
        Iri aliceIri,
        Func<HttpMessageHandler> transportFactory)
    {
        var aliceKey = KeyPairGenerator.GenerateRsa(new Iri($"{aliceIri.Value}#key-1"));
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(aliceIri, aliceKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = aliceIri, EnableRetry = false },
            new LazyHandler(transportFactory));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}
