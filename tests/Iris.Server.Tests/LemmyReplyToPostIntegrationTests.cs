using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
/// Integration tests for 138.14 (Iris reply → Lemmy comment): when an Iris user replies to a
/// Lemmy-sourced post (a <c>Page</c>), the reply (<c>Create(Note)</c> with <c>inReplyTo</c> pointing
/// to the <c>Page</c>) is delivered to the parent's home instance (Lemmy), so Lemmy can display it as
/// a comment under the post. This exercises Phase 136.7's cross-instance reply integrity path with a
/// <c>Page</c> parent (Lemmy's post format) rather than a <c>Note</c> parent (Iris's post format).
/// </summary>
public sealed class LemmyReplyToPostIntegrationTests : IDisposable
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
    private readonly Iri _bobActorIri;

    public LemmyReplyToPostIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts bob (the Lemmy stand-in — the parent Page's author). B hosts alice (the Iris replier).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Bob);
        _bobActorIri = aSeeded.ActorIri;
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Alice);
        _aliceKey = bSeeded.Key;
        _aliceActorIri = bSeeded.ActorIri;

        var aServerHolder = new TestServerHolder();
        var bServerHolder = new TestServerHolder();

        // A: its inbound fetcher reaches B (validates the reply signed by alice).
        _a = aServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Bob,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentityKeys(aSeeded.Key, aSeeded.ActorIri),
            Fetcher = BuildFetcherFor(AHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
            Client = BuildClient(BHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
        });

        // B: its object fetcher reaches A (resolves the remote parent Page's author), its delivery
        // transport reaches A (delivers the reply to bob via B's hosted DeliveryWorker), its inbound
        // fetcher is a self-fetcher (validates the reply signed by alice, B's local actor).
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
    /// An Iris reply to a Lemmy-sourced post (a <c>Page</c>) is delivered to the parent's home
    /// instance (A, the Lemmy stand-in), so the parent's home can store the reply and serve it under
    /// the parent's <c>/replies</c> collection. The reply is a <c>Create(Note)</c> with
    /// <c>inReplyTo</c> pointing to the <c>Page</c>.
    /// </summary>
    [Fact]
    public async Task IrisReplyToLemmyPage_DeliveredToParentHome_AndThreadedThere()
    {
        // bob (A) has a Page (the Lemmy stand-in's post). The Page's IRI is in A's serving namespace
        // so B can fetch it over the wire to resolve the parent's author.
        var pageIri = new Iri($"https://{AHost}/ap/v1/objects/page-{Guid.NewGuid():N}");
        await _aPersistence.Objects.PutObjectAsync(new Page
        {
            Id = pageIri.Value,
            Name = ["A Lemmy post"],
            Content = ["<p>Post content</p>"],
            AttributedTo = [new Link { Href = _bobActorIri.Uri }],
        }, CancellationToken.None);

        // alice (B) replies to the Page: a Create(Note) with inReplyTo pointing to the Page.
        var noteIri = new Iri($"https://{BHost}/ap/v1/objects/note-{Guid.NewGuid():N}");
        var createIri = new Iri($"https://{BHost}/activities/create-{Guid.NewGuid():N}");
        var replyNote = new Note
        {
            Id = noteIri.Value,
            Content = ["<p>A reply from Iris</p>"],
            AttributedTo = [new Link { Href = _aliceActorIri.Uri }],
            InReplyTo = [new Link { Href = pageIri.Uri }],
        };
        var create = new Create
        {
            Id = createIri.Value,
            Actor = [new Link { Href = _aliceActorIri.Uri }],
            Object = [replyNote],
        };

        // Publish the reply to alice's outbox (the local-outbox publish path, where the cross-instance
        // reply delivery leg lives — Phase 136.7).
        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // Wait for the effect: the reply Note is stored on A (the parent's home — the Lemmy stand-in).
        await WaitForAsync(
            async () => await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(30));

        // (a) The reply Note is stored on A (the parent's home instance).
        Assert.True(
            await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var storedNote),
            "A (the parent Page's home) should have stored the reply Note");
        Assert.IsType<Note>(storedNote);

        // (b) The reply edge (Page → Note) is recorded on A, so the reply threads under the Page.
        var replies = await _aPersistence.Replies.GetRepliesAsync(pageIri, CancellationToken.None);
        Assert.Contains(replies, r => r.Value == noteIri.Value);
    }

    /// <summary>
    /// The reply's <c>Note</c> carries <c>inReplyTo</c> pointing to the parent <c>Page</c> (not a
    /// <c>Note</c>), confirming the reply is correctly anchored to a Lemmy-format parent.
    /// </summary>
    [Fact]
    public async Task IrisReplyToLemmyPage_CarriesInReplyTo_PageIri()
    {
        var pageIri = new Iri($"https://{AHost}/ap/v1/objects/page-{Guid.NewGuid():N}");
        await _aPersistence.Objects.PutObjectAsync(new Page
        {
            Id = pageIri.Value,
            Name = ["A Lemmy post"],
            Content = ["<p>Post content</p>"],
            AttributedTo = [new Link { Href = _bobActorIri.Uri }],
        }, CancellationToken.None);

        var noteIri = new Iri($"https://{BHost}/ap/v1/objects/note-{Guid.NewGuid():N}");
        var createIri = new Iri($"https://{BHost}/activities/create-{Guid.NewGuid():N}");
        var replyNote = new Note
        {
            Id = noteIri.Value,
            Content = ["<p>A reply from Iris</p>"],
            AttributedTo = [new Link { Href = _aliceActorIri.Uri }],
            InReplyTo = [new Link { Href = pageIri.Uri }],
        };
        var create = new Create
        {
            Id = createIri.Value,
            Actor = [new Link { Href = _aliceActorIri.Uri }],
            Object = [replyNote],
        };

        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        await WaitForAsync(
            async () => await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(30));

        // The reply Note on A has inReplyTo pointing to the Page IRI.
        Assert.True(
            await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var storedNote),
            "The reply Note should be stored on A");
        var storedNoteAsNote = (Note)storedNote!;
        Assert.NotNull(storedNoteAsNote.InReplyTo);
        Assert.Contains(storedNoteAsNote.InReplyTo, l =>
            l is Link link && link.Href == pageIri.Uri);
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
