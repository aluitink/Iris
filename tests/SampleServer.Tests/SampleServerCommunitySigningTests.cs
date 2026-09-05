using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Samples.SampleServer;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iris.Samples.SampleServer.Tests;

/// <summary>
/// Phase 19.1.2 (F-1911-3) integration tests: the seeded <em>community's</em> signing identity. A
/// community's outbound deliveries (a community Follow published to <c>POST /ap/v1/c/{name}/outbox</c>)
/// are delivered to the target's inbox signed as the community (the community's key IRI is the primary
/// actor's key — the community's <c>publicKey</c> extension points at it). Before F-1911-3 the sample
/// registered only alice/bob/carla with the <see cref="IKeyProvider"/>, so a community follow dead-lettered
/// with "No signing identity registered for actor '.../c/iris'" (live-verified as an HTTP 401 on the
/// community outbox endpoint). These tests assert the seeded set is complete (unit) and that the full
/// signed community-follow round trip against the hosted sample succeeds (integration).
/// </summary>
public sealed class SampleServerCommunitySigningTests : IDisposable
{
    private const string Host = "localhost";
    private const int Port = 5000;
    private const string Community = "iris";

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly IPersistenceProvider _persistence;
    private readonly IKeyProvider _keyProvider;
    private readonly ISigningKey _aliceKey;

    public SampleServerCommunitySigningTests()
    {
        var builder = SampleServer.CreateWebHostBuilder();
        // Opt in to the carla remote stand-in: the community-follow round trip below targets carla
        // (a remote-host actor), which the sample only seeds when Iris:Seed:RemoteStandIn is set.
        builder.UseConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:Seed:RemoteStandIn"] = "true",
            })
            .Build());
        _server = new TestServer(builder);
        _client = _server.CreateClient();
        _persistence = _server.Services.GetRequiredService<IPersistenceProvider>();
        _keyProvider = _server.Services.GetRequiredService<IKeyProvider>();
        _aliceKey = _persistence.Keys.TryGetKey(new Iri($"{BaseUri}/ap/v1/u/alice#key-1"), out var key)
            ? key!
            : throw new InvalidOperationException("the sample must have seeded alice's key");
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private static string BaseUri => $"http://{Host}:{Port}";

    private static Iri ActorIri(string handle) => new($"{BaseUri}/ap/v1/u/{handle}");

    // --- The seeded key set is complete (unit) ----------------------------------

    [Fact]
    public void GetSeededKeyIris_IncludesCommunity_WithPrimaryActorKey()
    {
        var aliceIri = ActorIri("alice");
        var pairs = SampleServer.GetSeededKeyIris(aliceIri, remoteStandIn: true);

        // With the remote stand-in enabled, the seeded set is exactly: alice, bob, carla, and the
        // community — the community's key IRI is the primary actor's key (the community's publicKey
        // extension points at it).
        var communityEntry = pairs.FirstOrDefault(p => p.Handle == SampleServer.SampleCommunityName);
        Assert.Equal(SampleServer.SampleCommunityName, communityEntry.Handle);
        Assert.Equal(new Iri($"{aliceIri}#key-1"), communityEntry.KeyIri);
        Assert.Contains(pairs, p => p.Handle == SampleServer.CarlaHandle);
    }

    [Fact]
    public void GetSeededKeyIris_Default_ExcludesRemoteStandIn()
    {
        // The default sample (no remote stand-in) seeds only the local actors and the community — carla
        // is absent, so the honest default data carries no fake remote-host identity.
        var aliceIri = ActorIri("alice");
        var pairs = SampleServer.GetSeededKeyIris(aliceIri);

        Assert.DoesNotContain(pairs, p => p.Handle == SampleServer.CarlaHandle);
        Assert.Contains(pairs, p => p.Handle == "alice");
        Assert.Contains(pairs, p => p.Handle == SampleServer.BobHandle);
        Assert.Contains(pairs, p => p.Handle == SampleServer.SampleCommunityName);
    }

    [Fact]
    public void ActorIriFor_CommunityHandle_YieldsCommunityIri()
    {
        var aliceIri = ActorIri("alice");
        var communityIri = SampleServer.ActorIriFor(aliceIri, SampleServer.SampleCommunityName);
        Assert.Equal($"{BaseUri}/ap/v1/c/{Community}", communityIri.Value);
    }

    // --- The hosted sample registers the community's identity (the F-1911-3 fix) --

    [Fact]
    public void HostedSample_RegistersCommunityKey_WithKeyProvider()
    {
        var communityIri = new Iri($"{BaseUri}/ap/v1/c/{Community}");

        // The F-1911-3 regression: the key provider must resolve the community's identity, whose key IRI
        // is the primary actor's key (the community's publicKey extension points at alice's key).
        Assert.True(
            _keyProvider.TryGetIdentity(communityIri, out var identity),
            "the hosted sample must register the seeded community's key with the IKeyProvider "
            + "(F-1911-3: without it the community's outbound deliveries dead-letter with 'No signing "
            + "identity registered for actor '.../c/iris')");
        Assert.NotNull(identity);
        Assert.Equal(communityIri, identity!.ActorId);
        Assert.Equal($"{BaseUri}/ap/v1/u/alice#key-1", identity.KeyId.Value);
    }

    // --- The full signed community-follow round trip ------------------------------

    [Fact]
    public async Task CommunityOutbox_SignedFollow_SucceedsAndRecordsEdge()
    {
        var communityIri = new Iri($"{BaseUri}/ap/v1/c/{Community}");
        // Follow the remote-host actor (carla): a remote target, so the server must deliver the Follow
        // to carla's inbox signed as the community — the path that dead-lettered before F-1911-3.
        // Decision 055: the client sends the Follow's *shape* without an id; the server mints the id and
        // returns the created Follow in the 202 body, so the caller learns the id from that body (the
        // pre-055 client-set deterministic id is overwritten by the minter).
        var carlaIri = new Iri($"http://{SampleServer.RemoteHostName}/ap/v1/u/{SampleServer.CarlaHandle}");
        var follow = new Follow
        {
            Actor = [new Link { Href = communityIri.Uri }],
            Object = [new Link { Href = carlaIri.Uri }],
        };

        using var request = BuildSignedRequestAsCommunity(communityIri, follow, $"/ap/v1/c/{Community}/outbox");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Learn the id the server minted for the Follow (decision 055) from the 202 body.
        var body = await response.Content.ReadAsStringAsync();
        var created = ActivityJson.Deserialize<IObjectOrLink>(body);
        Assert.NotNull(created?.Id);
        var mintedFollowId = created!.Id;

        // The community's follows-set edge is recorded (the community `following` collection lists the
        // target).
        Assert.Contains(
            carlaIri,
            await _persistence.Communities.GetFollowsAsync(communityIri));
        var outbox = await _persistence.Activities.GetOutboxAsync(communityIri);
        Assert.Contains(outbox, a => a.Id == mintedFollowId);
    }

    // --- Helpers ------------------------------------------------------------------

    /// <summary>
    /// Builds a signed <see cref="HttpRequestMessage"/> for the given activity: the request is signed as
    /// the community (the community's key is the primary actor's key — resolved from the sample's key
    /// store) by running it through the client's <see cref="SigningHandler"/> over a capture handler,
    /// and the signed request (body + signature headers) is returned for replay through the plain
    /// <see cref="HttpClient"/>.
    /// </summary>
    private HttpRequestMessage BuildSignedRequestAsCommunity(Iri communityIri, Follow follow, string path)
    {
        var factory = new ActivityPubClientFactory(
            _server.Services.GetRequiredService<IKeyStore>(),
            _keyProvider,
            _server.Services.GetRequiredService<ISignatureSigner>());

        var json = ActivityJson.Serialize(follow);
        var capture = new CaptureHandler();
        using (var client = factory.Create(
            new ActivityPubClientOptions { ActorId = communityIri, EnableRetry = false },
            capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"{BaseUri}{path}")
                    {
                        Content = signedContent,
                    },
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            response.Dispose();
        }

        Assert.NotNull(capture.Captured);
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUri}{path}")
        {
            Content = content,
        };
        foreach (var (name, values) in capture.Captured!.Headers)
        {
            // Content-Type is a content header (already set); Date is restricted on content headers, so
            // it goes on the request headers (the server merges request + content headers when
            // reconstructing the signature base).
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

        if (capture.Captured.Headers.TryGetValue("date", out var dateValues))
        {
            foreach (var value in dateValues)
            {
                request.Headers.TryAddWithoutValidation("date", value);
            }
        }

        return request;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public CapturedRequest? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            // Capture BOTH request headers and content headers: the SigningHandler puts Date/Digest/
            // Content-Type as content headers (not in request.Headers), so capturing only
            // request.Headers would drop them and the replayed signature would fail to verify.
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = values.ToList();
            }

            if (request.Content is { } contentHeaders)
            {
                foreach (var (name, values) in contentHeaders.Headers)
                {
                    headers[name] = values.ToList();
                }
            }

            Captured = new CapturedRequest(body, headers);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            });
        }
    }

    private sealed record CapturedRequest(byte[] Body, Dictionary<string, List<string>> Headers);
}
