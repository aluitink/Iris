using System.Net;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Phase 17.4 integration tests: the per-peer inbound rate limiter. A remote peer that exceeds its
/// per-minute budget of signed inbox POSTs receives <c>429 Too Many Requests</c> (fail-fast, not
/// queued). The peer is keyed by the host of the signer's <c>keyId</c>. A disabled limiter (0)
/// permits all requests.
/// </summary>
public sealed class InboundRateLimitIntegrationTests : IDisposable
{
    private const string Host = "inbound-rate-limit.domain.local";
    private const string Handle = "alice";
    private const string RemoteHost = "remote.example.org";
    private static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");
    private static readonly Iri RemoteActorIri = new($"https://{RemoteHost}/u/bob");
    private static readonly Iri RemoteKeyId = new($"https://{RemoteHost}/u/bob#key-1");
    private static readonly Iri OtherActorIri = new("https://other.example.org/u/carol");
    private static readonly Iri OtherKeyId = new("https://other.example.org/u/carol#key-1");

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _remoteKey;
    private readonly KeyPair _otherKey;
    private readonly string _base = $"https://{Host}";

    public InboundRateLimitIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        _remoteKey = Seed(_persistence);
        _otherKey = KeyPairGenerator.GenerateRsa(OtherKeyId);
        _server = StartServer(_persistence, _remoteKey, _otherKey, maxRequests: 3);
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri(_base) };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A peer within its budget is accepted (202) ----------------------------------------

    [Fact]
    public async Task Inbox_PermitsWithinBudget()
    {
        for (var i = 0; i < 3; i++)
        {
            var response = await SendSignedPostAsync();
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
    }

    // --- A peer that exceeds its budget is rejected (429) ----------------------------------

    [Fact]
    public async Task Inbox_RejectsBeyondBudget()
    {
        // 3 requests: all permitted (budget of 3).
        for (var i = 0; i < 3; i++)
        {
            var response = await SendSignedPostAsync();
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        // 4th request: budget exhausted — 429 Too Many Requests.
        var rejected = await SendSignedPostAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // The 429 carries a Retry-After header (60 seconds) so the client can back off.
        Assert.True(rejected.Headers.RetryAfter != null, "429 should carry a Retry-After header");
    }

    // --- A 429'd request is not processed ---------------------------------------------------

    [Fact]
    public async Task Inbox_RejectedRequest_NotProcessed()
    {
        // Exhaust the budget (3 requests).
        for (var i = 0; i < 3; i++)
        {
            await SendSignedPostAsync();
        }

        // 4th request is rejected (429) and NOT processed: the note is not stored.
        var noteIri = new Iri($"{RemoteActorIri}/notes/n1");
        var rejected = await SendSignedPostAsync(noteIri: noteIri);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // The note was not stored (the activity was not dispatched to the inbox processor).
        var objectResponse = await _http.GetAsync(new Uri(noteIri.Value).AbsolutePath);
        Assert.Equal(HttpStatusCode.NotFound, objectResponse.StatusCode);
    }

    // --- Per-peer isolation: a different remote host is unaffected --------------------------

    [Fact]
    public async Task Inbox_PerPeerIsolation()
    {
        // Exhaust the budget for the first remote host (remote.example.org).
        for (var i = 0; i < 3; i++)
        {
            var response = await SendSignedPostAsync();
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var rejected = await SendSignedPostAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // A different remote host (other.example.org) has its own budget: permitted.
        var otherResponse = await SendSignedPostAsync(key: _otherKey, remoteActor: OtherActorIri, keyId: OtherKeyId);
        Assert.Equal(HttpStatusCode.Accepted, otherResponse.StatusCode);
    }

    // --- A disabled limiter (maxRequests 0) permits all requests ----------------------------

    [Fact]
    public async Task Inbox_DisabledLimiter_PermitsAll()
    {
        // A fresh server with the limiter disabled (0).
        var persistence = new InMemoryPersistenceProvider();
        var remoteKey = Seed(persistence);
        var otherKey = KeyPairGenerator.GenerateRsa(OtherKeyId);
        using var server = StartServer(persistence, remoteKey, otherKey, maxRequests: 0);
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri(_base) };

        // 10 requests: all permitted (no limit).
        for (var i = 0; i < 10; i++)
        {
            var response = await SendSignedPostAsync(http, remoteKey);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
    }

    // --- Helpers ---------------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendSignedPostAsync(
        HttpClient? http = null,
        KeyPair? key = null,
        Iri? remoteActor = null,
        Iri? keyId = null,
        Iri? noteIri = null)
    {
        var client = http ?? _http;
        var signer = key ?? _remoteKey;
        var actor = remoteActor ?? RemoteActorIri;
        var id = keyId ?? RemoteKeyId;
        return await SendSignedPostAsync(client, signer, actor, id, noteIri);
    }

    private static async Task<HttpResponseMessage> SendSignedPostAsync(
        HttpClient http,
        KeyPair key,
        Iri remoteActor,
        Iri remoteKeyId,
        Iri? noteIri = null)
    {
        var actualNoteIri = noteIri ?? new Iri($"{remoteActor}/notes/{Guid.NewGuid():N}");
        var create = new Create
        {
            Id = $"{remoteActor}/creates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(remoteActor.Value) }],
            Object =
            [
                new Note
                {
                    Id = actualNoteIri.Value,
                    Content = [ $"a note from {remoteActor} " ],
                    AttributedTo = [new Link { Href = new Uri(remoteActor.Value) }],
                },
            ],
        };
        var body = ActivityJson.Serialize(create);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        var date = DateTime.UtcNow.ToString("R");
        var digest = Signatures.ComputeDigest(bodyBytes);
        var host = Host;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.HostHeaderName] = host,
            [Signatures.DateHeaderName] = date,
            [Signatures.ContentTypeHeaderName] = ActivityJson.ActivityJsonContentType,
            [Signatures.DigestHeaderName] = digest,
        };

        var metadata = new HttpRequestMetadata(
            "POST",
            $"/ap/v1/u/{Handle}/inbox",
            host,
            date,
            ActivityJson.ActivityJsonContentType,
            bodyBytes,
            headers);

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(remoteActor, remoteKeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var identity = new SystemIdentity(remoteActor, remoteKeyId);
        var signature = signer.Sign(metadata, identity, SigningProfile.ServerToServer);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/u/{Handle}/inbox")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
        request.Content.Headers.TryAddWithoutValidation(Signatures.DigestHeaderName, digest);
        request.Headers.TryAddWithoutValidation(Signatures.DateHeaderName, date);
        request.Headers.TryAddWithoutValidation(Signatures.SignatureHeaderName, signature);

        return await http.SendAsync(request);
    }

    private static KeyPair Seed(InMemoryPersistenceProvider persistence)
    {
        // Seed the local actor (the inbox owner).
        var localKeyId = new Iri($"{ActorIri.Value}#key-1");
        var localKey = KeyPairGenerator.GenerateRsa(localKeyId);
        var actor = new Person
        {
            Id = ActorIri.Value,
            PreferredUsername = Handle,
            Name = [Handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = localKeyId.Value,
            owner = ActorIri.Value,
            publicKeyPem = localKey.ExportPublicKeyPem(),
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
        persistence.Keys.PutKey(localKey);

        // Generate the remote key (the signer of inbound deliveries).
        var remoteKey = KeyPairGenerator.GenerateRsa(RemoteKeyId);
        return remoteKey;
    }

    private static TestServer StartServer(
        InMemoryPersistenceProvider persistence,
        KeyPair remoteKey,
        KeyPair otherKey,
        int maxRequests)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(remoteKey);
        keyStore.PutKey(otherKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

        var fetcher = new RemoteActorDocFetcher(
            new Dictionary<Iri, KeyPair>
            {
                [RemoteActorIri] = remoteKey,
                [OtherActorIri] = otherKey,
            });

        TestServer? self = null;
        var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = persistence,
            Fetcher = fetcher,
            ExtraServices = s =>
            {
                s.AddSingleton<IOptions<InboundRateLimitOptions>>(_ =>
                    Options.Create(new InboundRateLimitOptions
                    {
                        PerPeerMaxRequestsPerMinute = maxRequests,
                    }));
                s.AddSingleton<IInboundRateLimiter>(sp =>
                    new SlidingWindowInboundRateLimiter(
                        sp.GetRequiredService<IOptions<InboundRateLimitOptions>>().Value.PerPeerMaxRequestsPerMinute,
                        TimeSpan.FromMinutes(1)));
            },
        });
        self = server;
        return server;
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that serves a remote actor's document (with its
    /// <c>publicKey</c> in <c>ExtensionData</c>) from a pre-registered set of remote actors. This
    /// mirrors what a real remote instance would serve.
    /// </summary>
    private sealed class RemoteActorDocFetcher(Dictionary<Iri, KeyPair> actors) : IActorDocumentFetcher
    {
        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            if (!actors.TryGetValue(actorIri, out var key))
            {
                return Task.FromResult<Actor?>(null);
            }

            var actor = new Person
            {
                Id = actorIri.Value,
                PreferredUsername = "remote",
                Name = ["remote"],
            };
            actor.ExtensionData = new Dictionary<string, JsonElement>
            {
                ["publicKey"] = JsonSerializer.SerializeToElement(new
                {
                    id = key.KeyId.Value,
                    owner = actorIri.Value,
                    publicKeyPem = key.ExportPublicKeyPem(),
                }),
            };
            return Task.FromResult<Actor?>(actor);
        }
    }
}
