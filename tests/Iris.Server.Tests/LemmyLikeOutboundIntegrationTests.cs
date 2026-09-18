using System.Net;
using System.Net.Http.Headers;
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

/// <summary>
/// Integration tests for 138.16 (Iris → Lemmy likes): when an Iris user likes a Lemmy-sourced post
/// (a <c>Page</c> stored on another instance), the <c>Like</c> activity is delivered to the object's
/// home (the Lemmy stand-in's author), so the Lemmy instance can record the upvote and reflect it in
/// the post's score. This exercises the outbound Like federation path (Phase 24.1
/// <c>ResolveObjectOwnerForDeliveryAsync</c> + server→server delivery).
/// </summary>
public sealed class LemmyLikeOutboundIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Bob = "bob";
    private const string Alice = "alice";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;

    public LemmyLikeOutboundIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts bob (the Lemmy stand-in — the parent Page's author, the object's home).
        // B hosts alice (the Iris user who likes the Lemmy post).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Bob);
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Alice);
        _aliceKey = bSeeded.Key;
        _aliceActorIri = bSeeded.ActorIri;

        var aServerHolder = new TestServerHolder();
        var bServerHolder = new TestServerHolder();

        // A: its inbound fetcher reaches B (validates the Like signed by alice).
        _a = aServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Bob,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentityKeys(aSeeded.Key, aSeeded.ActorIri),
            Fetcher = BuildFetcherFor(AHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
            Client = BuildClient(BHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
        });

        // B: its object fetcher reaches A (resolves the remote Page's author), its delivery transport
        // reaches A (delivers the Like to bob via B's hosted DeliveryWorker), its inbound fetcher is a
        // self-fetcher (validates the Like signed by alice, B's local actor).
        _b = bServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Alice,
            Persistence = _bPersistence,
            IdentityKeys = BuildIdentityKeys(bSeeded.Key, bSeeded.ActorIri),
            Fetcher = BuildSelfFetcher(BHost, bSeeded.Key, bSeeded.ActorIri, () => bServerHolder.Server!),
            DeliveryTransport = () => new LazyHandler(() => aServerHolder.Server!.CreateHandler()),
            Client = BuildClient(BHost, Alice, bSeeded.Key, () => aServerHolder.Server!),
        });

        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri($"https://{AHost}") };
        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri($"https://{BHost}") };
    }

    public void Dispose()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    /// <summary>
    /// An Iris user's like of a Lemmy-sourced post (a <c>Page</c>) is delivered to the object's home
    /// (A, the Lemmy stand-in), where the like edge is recorded. The Lemmy instance can then reflect
    /// the upvote in the post's score.
    /// </summary>
    [Fact]
    public async Task IrisLikeOfLemmyPage_DeliveredToObjectHome_AndRecordedThere()
    {
        // bob (A) has a Page (the Lemmy stand-in's post). The Page's IRI is in A's serving namespace
        // so B can fetch it over the wire to resolve the object's owner (bob).
        var pageIri = new Iri($"https://{AHost}/ap/v1/objects/page-{Guid.NewGuid():N}");
        await _aPersistence.Objects.PutObjectAsync(new Page
        {
            Id = pageIri.Value,
            Name = ["A Lemmy post"],
            Content = ["<p>Post content</p>"],
            AttributedTo = [new Link { Href = new Uri($"https://{AHost}/ap/v1/u/{Bob}") }],
        }, CancellationToken.None);

        // alice (B) likes the Page: a Like activity published to alice's outbox.
        var likeIri = new Iri($"https://{BHost}/activities/like-{Guid.NewGuid():N}");
        var like = new Like
        {
            Id = likeIri.Value,
            Actor = [new Link { Href = _aliceActorIri.Uri }],
            Object = [new Link { Href = pageIri.Uri }],
        };

        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, like, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // Wait for the effect: the like edge is recorded on A (the object's home — the Lemmy stand-in).
        var aliceIri = _aliceActorIri;
        await WaitForAsync(
            async () => await _aPersistence.Likes.HasLikedAsync(aliceIri, pageIri, CancellationToken.None),
            timeout: TimeSpan.FromSeconds(30));

        // (a) A recorded the liker → liked-object edge (alice → Page) on the object's home.
        Assert.True(
            await _aPersistence.Likes.HasLikedAsync(aliceIri, pageIri, CancellationToken.None),
            "A (the object's home) should have recorded the like federated by B's outbox publish");

        // (b) A's likers reverse index for the Page includes alice — the per-object like counter
        // on the object's home counts the Iris like.
        var likers = await _aPersistence.Likes.GetLikersAsync(pageIri, CancellationToken.None);
        Assert.Contains(likers, l => l == aliceIri);

        // (c) GET {Page}/likes on A lists alice's like — the Lemmy author's per-object like collection
        // includes the Iris like.
        var response = await _aHttp.GetAsync($"{pageIri.Value}/likes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).ToArray();
        Assert.Single(items);
        var item = items[0];
        // The likes collection serves the full Like activity (id + actor + object).
        Assert.Equal("Like", item.GetProperty("type").GetString());
        Assert.Equal($"https://{BHost}/ap/v1/u/{Alice}", ExtractIri(item.GetProperty("actor")));
        Assert.Equal(pageIri.Value, ExtractIri(item.GetProperty("object")));
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    /// <summary>
    /// An Iris user's like of a local post (stored on B, the liker's own instance) is recorded locally
    /// and NOT delivered cross-instance (the object's owner is local — no cross-instance hop needed).
    /// </summary>
    [Fact]
    public async Task IrisLikeOfLocalPost_RecordedLocally_NoCrossInstanceDelivery()
    {
        // alice (B) has a local post.
        var noteIri = new Iri($"https://{BHost}/ap/v1/objects/note-{Guid.NewGuid():N}");
        await _bPersistence.Objects.PutObjectAsync(new Note
        {
            Id = noteIri.Value,
            Content = ["<p>A local post</p>"],
            AttributedTo = [new Link { Href = _aliceActorIri.Uri }],
        }, CancellationToken.None);

        // alice likes her own post (or another local post): a Like activity.
        var likeIri = new Iri($"https://{BHost}/activities/like-{Guid.NewGuid():N}");
        var like = new Like
        {
            Id = likeIri.Value,
            Actor = [new Link { Href = _aliceActorIri.Uri }],
            Object = [new Link { Href = noteIri.Uri }],
        };

        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, like, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // The like is recorded locally on B (the object's home).
        var aliceIri = _aliceActorIri;
        await WaitForAsync(
            async () => await _bPersistence.Likes.HasLikedAsync(aliceIri, noteIri, CancellationToken.None),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(
            await _bPersistence.Likes.HasLikedAsync(aliceIri, noteIri, CancellationToken.None),
            "B (the object's home) should have recorded the local like");

        // No cross-instance delivery: A should NOT have the like edge.
        Assert.False(
            await _aPersistence.Likes.HasLikedAsync(aliceIri, noteIri, CancellationToken.None),
            "A should NOT have the like edge (local object, no cross-instance hop)");
    }

    // --- Helpers ---

    private static IdentityKeys BuildIdentityKeys(KeyPair personKey, Iri personActorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(personKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(personActorIri, personKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, Func<TestServer> targetServer)
    {
        var client = BuildClient(host, handle, key, targetServer);
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private static IActorDocumentFetcher BuildSelfFetcher(
        string host, KeyPair key, Iri actorIri, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private static IActivityPubClient BuildClient(
        string host, string handle, KeyPair key, Func<TestServer> targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => targetServer().CreateHandler()));
    }

    private static IActivityPubClient BuildClientForSigning(Iri actorIri, KeyPair key, HttpMessageHandler handler)
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

    private static HttpRequestMessage SignedOutboxRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClientForSigning(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{BHost}{path}")
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{BHost}{path}")
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

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        throw new TimeoutException("Condition was not met within the timeout.");
    }

    private static string ExtractIri(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString()!;
        }
        return element.GetProperty("id").GetString()!;
    }

    private sealed class TestServerHolder
    {
        public TestServer? Server { get; set; }
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            });
        }
    }

    private sealed record CapturedRequest(byte[] Body, Dictionary<string, List<string>> Headers);
}
