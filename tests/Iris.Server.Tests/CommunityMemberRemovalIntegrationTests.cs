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

/// <summary>
/// Phase 40.2 integration tests: the community creator removes a member via the local,
/// Basic-authenticated endpoint <c>POST /local/v1/c/{name}/members/remove/{memberId}</c>.
/// The server verifies the authenticated person is the community's creator (via the Group's
/// <c>attributedTo</c>) before removing the membership edge.
/// </summary>
public sealed class CommunityMemberRemovalIntegrationTests : IDisposable
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

    public CommunityMemberRemovalIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        // Seed alice (the community creator) and bob (a member).
        var aliceSeeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        _aliceIri = aliceSeeded.ActorIri;
        _bobIri = TestSeeder.SeedPerson(_persistence, AHost, Bob);

        // A Basic-auth credential validator that recognizes alice's credentials.
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

    // --- The creator can remove a member ------------------------------------------------------

    [Fact]
    public async Task RemoveMember_Creator_RemovesMember()
    {
        var communityIri = new Iri($"https://{AHost}/ap/v1/c/devs");

        // Create the community (alice is the creator, so AttributedTo = [alice]).
        var createResult = await _client.CreateCommunityAsync(_aliceIri, "devs", "Devs Community");
        Assert.True(createResult.IsSuccess, $"CreateCommunityAsync must succeed (got {createResult.StatusCode})");

        // Add bob as a member.
        var addResult = await _client.AddMemberAsync(communityIri, _bobIri);
        Assert.True(addResult.IsSuccess, $"AddMemberAsync must succeed (got {addResult.StatusCode})");

        // Verify bob is a member.
        Assert.True(
            await _persistence.Communities.IsMemberAsync(communityIri, _bobIri),
            "bob should be a member before removal");

        // Alice (the creator) removes bob via the local endpoint.
        var status = await RemoveMemberAsync("devs", _bobIri, auth: "alice:alice-password");
        Assert.Equal(HttpStatusCode.NoContent, status);

        // Bob is no longer a member.
        Assert.False(
            await _persistence.Communities.IsMemberAsync(communityIri, _bobIri),
            "bob should not be a member after removal");
    }

    // --- A non-creator cannot remove a member -------------------------------------------------

    [Fact]
    public async Task RemoveMember_NonCreator_ReturnsForbidden()
    {
        var communityIri = new Iri($"https://{AHost}/ap/v1/c/devs");

        // Create the community (alice is the creator).
        var createResult = await _client.CreateCommunityAsync(_aliceIri, "devs", "Devs Community");
        Assert.True(createResult.IsSuccess, $"CreateCommunityAsync must succeed (got {createResult.StatusCode})");

        // Add bob as a member.
        var addResult = await _client.AddMemberAsync(communityIri, _bobIri);
        Assert.True(addResult.IsSuccess, $"AddMemberAsync must succeed (got {addResult.StatusCode})");

        // Bob (not the creator) tries to remove himself — the server should return 403.
        // The credential validator only recognizes alice's credentials, so bob's credentials fail.
        var status = await RemoveMemberAsync("devs", _bobIri, auth: "bob:bob-password");
        Assert.Equal(HttpStatusCode.Forbidden, status);

        // Bob is still a member.
        Assert.True(
            await _persistence.Communities.IsMemberAsync(communityIri, _bobIri),
            "bob should still be a member after the failed removal");
    }

    // --- Removing a non-member returns 404 ----------------------------------------------------

    [Fact]
    public async Task RemoveMember_NonMember_ReturnsNotFound()
    {
        var communityIri = new Iri($"https://{AHost}/ap/v1/c/devs");

        // Create the community.
        var createResult = await _client.CreateCommunityAsync(_aliceIri, "devs", "Devs Community");
        Assert.True(createResult.IsSuccess, $"CreateCommunityAsync must succeed (got {createResult.StatusCode})");

        // Alice tries to remove bob, who is not a member.
        var status = await RemoveMemberAsync("devs", _bobIri, auth: "alice:alice-password");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // --- Removing from a non-existent community returns 404 ----------------------------------

    [Fact]
    public async Task RemoveMember_NonExistentCommunity_ReturnsNotFound()
    {
        // Alice tries to remove a member from a community that doesn't exist.
        var status = await RemoveMemberAsync("nonexistent", _bobIri, auth: "alice:alice-password");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // --- No auth returns 403 ------------------------------------------------------------------

    [Fact]
    public async Task RemoveMember_NoAuth_ReturnsForbidden()
    {
        var communityIri = new Iri($"https://{AHost}/ap/v1/c/devs");

        // Create the community and add a member.
        var createResult = await _client.CreateCommunityAsync(_aliceIri, "devs", "Devs Community");
        Assert.True(createResult.IsSuccess);
        var addResult = await _client.AddMemberAsync(communityIri, _bobIri);
        Assert.True(addResult.IsSuccess);

        // No auth header → 403.
        var status = await RemoveMemberAsync("devs", _bobIri, auth: null);
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    // --- Helpers ------------------------------------------------------------------------------

    /// <summary>
    /// Issues a raw Basic-authenticated POST to the community member removal endpoint
    /// (<c>/local/v1/c/{name}/members/remove/{memberId}</c>). <paramref name="auth"/> is
    /// "user:pass" or null (no auth).
    /// </summary>
    private async Task<HttpStatusCode> RemoveMemberAsync(string name, Iri memberIri, string? auth)
    {
        var url = $"https://{AHost}/local/v1/c/{name}/members/remove/{memberIri.Value.TrimStart('/')}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (auth is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth)));
        }

        using var response = await _http.SendAsync(request);
        return response.StatusCode;
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
