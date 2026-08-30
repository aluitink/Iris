using System.Net;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 13.3 — Mastodon <c>Question</c>/poll inbound handling. A Mastodon poll arrives as a
/// <see cref="Create"/> activity whose embedded object carries the poll data (the <c>poll</c> /
/// <c>options</c> / <c>votes</c> / <c>endsAt</c> / <c>closed</c> / <c>oneOfMany</c> properties). These
/// are not in the ActivityStreams 2.0 vocabulary the library models, so they land in
/// <c>ExtensionData</c> and are forwarded opaquely. This test proves the guarantee end-to-end over the
/// real signed inbox pipeline (not just store-then-serve): a signed <see cref="Create"/> carrying a
/// poll-bearing object is accepted, the object is stored, and it is served back with the full poll
/// shape preserved verbatim.
/// </summary>
public sealed class MastodonPollInboundIntegrationTests : IDisposable
{
    private const string Host = "poll.domain.local";
    private const string Handle = "alice";
    private static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");
    private static readonly Iri NoteIri = new($"{ActorIri}/notes/poll1");

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _key;
    private readonly string _base = $"https://{Host}";

    public MastodonPollInboundIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        _key = Seed(_persistence);
        _server = StartServer(_persistence, _key);
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri(_base) };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A signed Create carrying a poll-bearing Note is accepted and the poll round-trips ----

    [Fact]
    public async Task Inbox_AcceptsPollBearingCreate_AndServesPollVerbatim()
    {
        // A Mastodon poll: a Create whose object is a Note with `poll` extension data (options, votes,
        // endsAt, closed, oneOfMany). The poll properties are not in AS2.0, so they ride in ExtensionData.
        var create = BuildPollCreate(NoteIri);
        var body = ActivityJson.Serialize(create);
        var response = await SendSignedPostAsync($"/ap/v1/u/{Handle}/inbox", body);

        // 202 Accepted: the server processed the Create.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The embedded poll-bearing Note is stored and served by its IRI, with the full poll shape
        // preserved verbatim.
        var objectResponse = await _http.GetAsync(new Uri(NoteIri.Value).AbsolutePath);
        objectResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await objectResponse.Content.ReadAsStringAsync());

        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(NoteIri.Value, doc.RootElement.GetProperty("id").GetString());

        // The `poll` extension object is preserved verbatim (an opaque Mastodon extension).
        var poll = doc.RootElement.GetProperty("poll");
        Assert.Equal("poll-1", poll.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Array, poll.GetProperty("options").ValueKind);
        Assert.Equal(3, poll.GetProperty("options").GetArrayLength());
        Assert.Equal("Alice", poll.GetProperty("options")[0].GetProperty("title").GetString());
        Assert.Equal(5, poll.GetProperty("options")[2].GetProperty("votesCount").GetInt32());
        Assert.Equal("2026-09-01T00:00:00Z", poll.GetProperty("endsAt").GetString());
        Assert.False(poll.GetProperty("expired").GetBoolean());
        Assert.False(poll.GetProperty("multiple").GetBoolean());
        Assert.Equal(12, poll.GetProperty("totalVotes").GetInt32());
    }

    // --- A poll with `oneOfMany` (a vote link) round-trips -----------------------------------

    [Fact]
    public async Task Inbox_AcceptsPollWithOneOfMany_AndServesVerbatim()
    {
        // Mastodon marks the voter's own selection with `oneOfMany` (a link to the vote object). This
        // is part of the poll extension and must round-trip.
        var create = BuildPollCreateWithOneOfMany(NoteIri);
        var body = ActivityJson.Serialize(create);
        var response = await SendSignedPostAsync($"/ap/v1/u/{Handle}/inbox", body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var objectResponse = await _http.GetAsync(new Uri(NoteIri.Value).AbsolutePath);
        objectResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await objectResponse.Content.ReadAsStringAsync());

        var poll = doc.RootElement.GetProperty("poll");
        // `oneOfMany` is a link (an object with an href), preserved verbatim.
        var oneOfMany = poll.GetProperty("oneOfMany");
        Assert.Equal("https://remote.example.org/votes/42", oneOfMany.GetProperty("href").GetString());
        Assert.Equal("vote", oneOfMany.GetProperty("type").GetString());
    }

    // --- Helpers ------------------------------------------------------------------------

    private Create BuildPollCreate(Iri objectIri) => new()
    {
        Id = $"{ActorIri.Value}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(ActorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = objectIri.Value,
                Content = ["Who wins?"],
                AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["poll"] = JsonSerializer.SerializeToElement(new
                    {
                        id = "poll-1",
                        options = new[]
                        {
                            new { title = "Alice", votesCount = 3 },
                            new { title = "Bob", votesCount = 4 },
                            new { title = "Charlie", votesCount = 5 },
                        },
                        endsAt = "2026-09-01T00:00:00Z",
                        expired = false,
                        multiple = false,
                        totalVotes = 12,
                    }),
                },
            },
        ],
    };

    private Create BuildPollCreateWithOneOfMany(Iri objectIri) => new()
    {
        Id = $"{ActorIri.Value}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(ActorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = objectIri.Value,
                Content = ["Who wins? (with a vote)"],
                AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["poll"] = JsonSerializer.SerializeToElement(new
                    {
                        id = "poll-2",
                        options = new[]
                        {
                            new { title = "Alice", votesCount = 7 },
                            new { title = "Bob", votesCount = 4 },
                        },
                        endsAt = "2026-09-01T00:00:00Z",
                        expired = false,
                        multiple = false,
                        totalVotes = 11,
                        oneOfMany = new { type = "vote", href = "https://remote.example.org/votes/42" },
                    }),
                },
            },
        ],
    };

    /// <summary>
    /// Sends a signed POST to the given path with the given body, using the ServerToServer signature
    /// profile (which covers <c>content-type</c>).
    /// </summary>
    private async Task<HttpResponseMessage> SendSignedPostAsync(string path, string body)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, _key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var date = DateTime.UtcNow.ToString("R");
        var digest = Signatures.ComputeDigest(bodyBytes);
        var contentType = ActivityJson.ActivityJsonContentType;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.HostHeaderName] = Host,
            [Signatures.DateHeaderName] = date,
            [Signatures.ContentTypeHeaderName] = contentType,
            [Signatures.DigestHeaderName] = digest,
        };

        var metadata = new HttpRequestMetadata("POST", path, Host, date, contentType, bodyBytes, headers);
        var identity = new SystemIdentity(ActorIri, _key.KeyId);
        var signature = signer.Sign(metadata, identity, SigningProfile.ServerToServer);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base + path))
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Content.Headers.TryAddWithoutValidation(Signatures.DigestHeaderName, digest);
        request.Headers.TryAddWithoutValidation(Signatures.DateHeaderName, date);
        request.Headers.TryAddWithoutValidation(Signatures.SignatureHeaderName, signature);

        return await _http.SendAsync(request);
    }

    private static KeyPair Seed(InMemoryPersistenceProvider persistence)
    {
        var keyId = new Iri($"{ActorIri.Value}#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyId);
        var actor = new Person
        {
            Id = ActorIri.Value,
            PreferredUsername = Handle,
            Name = [Handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = ActorIri.Value,
            publicKeyPem = key.ExportPublicKeyPem(),
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
        persistence.Keys.PutKey(key);
        return key;
    }

    private static TestServer StartServer(InMemoryPersistenceProvider persistence, KeyPair key)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

        TestServer? self = null;
        var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = persistence,
            Fetcher = BuildSelfFetcher(factory, () => self!),
        });
        self = server;
        return server;
    }

    private static IActorDocumentFetcher BuildSelfFetcher(ActivityPubClientFactory factory, Func<TestServer> selfServer)
    {
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = ActorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}
