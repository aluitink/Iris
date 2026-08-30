using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Samples.SampleServer;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iris.Samples.SampleServer.Tests;

/// <summary>
/// Phase 8 (Slice S1) integration tests for the federation-ready sample server: inbound signature
/// validation on the inbox, a real signed cross-instance delivery (alice on the sample instance
/// follows bob, verified end to end), the Ed25519 remote-host actor, and the rich seed (per-actor
/// credentials, follows, replies, and likes).
/// </summary>
/// <remarks>
/// These tests host the <see cref="SampleServer"/> in an in-process <see cref="TestServer"/> (no real
/// port is bound). The signed-delivery test builds a real client signed as alice (the sample's
/// instance actor) routed to the sample's in-process handler, so the full inbound path runs: the
/// sample's signature middleware verifies alice's RSA key (resolved from alice's own public document)
/// and the follow handler records the edge.
/// </remarks>
public sealed class SampleServerFederationTests : IDisposable
{
    private const string Host = "localhost";
    private const int Port = 5000;
    private const string Community = "iris";

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly IPersistenceProvider _persistence;

    public SampleServerFederationTests()
    {
        var builder = SampleServer.CreateWebHostBuilder();
        _server = new TestServer(builder);
        _client = _server.CreateClient();
        _persistence = _server.Services.GetRequiredService<IPersistenceProvider>();
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private static string BaseUri => $"http://{Host}:{Port}";

    private static Iri ActorIri(string handle) => new($"{BaseUri}/ap/v1/u/{handle}");

    private static string BasicAuth(string user, string pass)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    // --- Inbound signature validation ------------------------------------------

    [Fact]
    public async Task InboxPost_UnterminatedSignature_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/u/{SampleServer.BobHandle}/inbox")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/activity+json"),
        };

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InboxPost_SignedFollow_IsAcceptedAndRecorded()
    {
        var keyStore = _server.Services.GetRequiredService<IKeyStore>();
        var keyProvider = _server.Services.GetRequiredService<IKeyProvider>();
        var signer = _server.Services.GetRequiredService<ISignatureSigner>();
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var aliceIri = ActorIri("alice");
        using var client = factory.Create(
            new ActivityPubClientOptions { ActorId = aliceIri, EnableRetry = false },
            _server.CreateHandler());

        var result = await client.FollowAsync(aliceIri, ActorIri(SampleServer.BobHandle));

        // 202 Accepted: the full inbound pipeline ran (signature verified, handler recorded the edge).
        Assert.Equal(202, result.StatusCode);
        Assert.True(
            await _persistence.Follows.IsFollowingAsync(aliceIri, ActorIri(SampleServer.BobHandle)));
    }

    // --- Rich seed -------------------------------------------------------------

    [Fact]
    public async Task ActorDoc_SecondActor_AuthenticatesWithOwnHandle()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/ap/v1/u/{SampleServer.BobHandle}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{SampleServer.BobHandle}:{SampleServer.Password}"))) },
        };

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<Actor>(json);
        Assert.NotNull(doc);
        Assert.True(
            doc!.ExtensionData is { } ext && ext.ContainsKey("privateKey"),
            "bob must authenticate with his own handle and unlock the privateKey extension");
    }

    [Fact]
    public async Task RemoteHostActor_IsNotResolvable_LikeARealRemote()
    {
        // Carla is a remote-host actor (her IRI is on remote.example, not this instance). The sample's
        // inbound key resolver — like a real instance's — cannot resolve a true remote actor's key, so
        // a signed delivery FROM carla would be rejected. This is the honest federation boundary: the
        // sample verifies local-host senders and treats other-host senders as unknown.
        var carlaKeyIri = new Iri($"http://{SampleServer.RemoteHostName}/ap/v1/u/{SampleServer.CarlaHandle}#key-1");
        var resolver = _server.Services.GetRequiredService<IInboundKeyResolver>();
        var key = await resolver.ResolveAsync(carlaKeyIri);
        Assert.Null(key);
    }

    [Fact]
    public async Task InboxPost_SignedFollow_FromEd25519LocalActor_IsAccepted()
    {
        // A second local actor (bob) signs an Ed25519 follow and delivers it to alice's inbox. The
        // sample's signature middleware resolves bob's key from bob's own local document and verifies
        // the Ed25519 signature end to end, exercising the non-RSA verification path.
        var keyStore = _server.Services.GetRequiredService<IKeyStore>();
        var keyProvider = _server.Services.GetRequiredService<IKeyProvider>();
        var signer = _server.Services.GetRequiredService<ISignatureSigner>();

        var bobIri = ActorIri(SampleServer.BobHandle);
        var bobKeyIri = new Iri($"{bobIri}#key-1");
        var bobKey = Ed25519Key.Generate(bobKeyIri);
        keyStore.PutKey(bobKey);
        keyProvider.RegisterKey(bobIri, bobKeyIri);
        // Replace bob's stored publicKeyPem with the new Ed25519 key so the inbound resolver (which
        // reads bob's local document) resolves the Ed25519 key rather than the seeded RSA key.
        var fetcher = _server.Services.GetRequiredService<IActorDocumentFetcher>();
        var bobDoc = await fetcher.GetActorAsync(bobIri);
        bobDoc!.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
        bobDoc.ExtensionData["publicKey"] = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id = bobKeyIri.Value,
            owner = bobIri.Value,
            publicKeyPem = bobKey.ExportPublicKeyPem(),
        });
        await _persistence.Actors.PutActorAsync(bobDoc);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        using var client = factory.Create(
            new ActivityPubClientOptions { ActorId = bobIri, EnableRetry = false },
            _server.CreateHandler());

        var result = await client.FollowAsync(bobIri, ActorIri("alice"));
        Assert.Equal(202, result.StatusCode);
        Assert.True(await _persistence.Follows.IsFollowingAsync(bobIri, ActorIri("alice")));
    }

    [Fact]
    public async Task Seed_Follows_AreRecorded()
    {
        var alice = ActorIri("alice");
        var bob = ActorIri(SampleServer.BobHandle);
        var carla = new Iri($"http://{SampleServer.RemoteHostName}/ap/v1/u/{SampleServer.CarlaHandle}");

        Assert.True(await _persistence.Follows.IsFollowingAsync(alice, bob));
        Assert.True(await _persistence.Follows.IsFollowingAsync(bob, alice));
        Assert.True(await _persistence.Follows.IsFollowingAsync(alice, carla));
        Assert.True(await _persistence.Follows.IsFollowingAsync(carla, alice));
    }

    [Fact]
    public async Task Community_FollowsRemoteActor_AndHasMembers()
    {
        var carla = new Iri($"http://{SampleServer.RemoteHostName}/ap/v1/u/{SampleServer.CarlaHandle}");
        var communityIri = new Iri($"{BaseUri}/ap/v1/c/{Community}");

        var follows = await _persistence.Communities.GetFollowsAsync(communityIri);
        Assert.Contains(carla, follows);

        var members = await _persistence.Communities.GetMembersAsync(communityIri);
        Assert.Contains(ActorIri("alice"), members);
        Assert.Contains(ActorIri(SampleServer.BobHandle), members);
    }

    [Fact]
    public async Task Seed_Reply_And_Like_AreStored()
    {
        var aliceIri = ActorIri("alice");
        var bobIri = ActorIri(SampleServer.BobHandle);
        var carlaIri = new Iri($"http://{SampleServer.RemoteHostName}/ap/v1/u/{SampleServer.CarlaHandle}");

        // The reply: bob's note 2 is a reply to alice's note 1 (recorded in the reply store and
        // present in bob's outbox).
        var bobOutbox = await _persistence.Activities.GetOutboxAsync(bobIri);
        var reply = bobOutbox.FirstOrDefault(a => a.Id == $"{bobIri.Value}/notes/2");
        Assert.True(reply is not null, $"bob outbox = [{string.Join(", ", bobOutbox.Select(a => a.Id))}]");
        Assert.True(await _persistence.Replies.HasReplyAsync(
            new Iri($"{aliceIri.Value}/notes/1"),
            new Iri($"{bobIri.Value}/notes/2")), "reply edge missing");

        // The like: carla liked alice's note 1 (present in carla's outbox and the like store).
        var carlaOutbox = await _persistence.Activities.GetOutboxAsync(carlaIri);
        var like = carlaOutbox.FirstOrDefault(a => a.Id == $"{carlaIri.Value}/likes/1");
        Assert.True(like is not null, $"carla outbox = [{string.Join(", ", carlaOutbox.Select(a => a.Id))}]");
        Assert.IsType<Like>(like);
        Assert.True(await _persistence.Likes.HasLikedAsync(carlaIri, new Iri($"{aliceIri.Value}/notes/1")), "like edge missing");
    }
}
