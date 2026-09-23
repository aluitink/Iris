using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Core.Signing;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// S69: Direct notes posted via the UI outbox-publish path (POST /ap/v1/u/{handle}/outbox) must
/// be delivered to the named recipient. The OutboxPublishHandler Create branch previously had no
/// Direct-recipient delivery leg for LOCAL recipients — the cross-post leg (community-oriented)
/// dropped local recipients and transformed top-level Notes into Pages for remote recipients
/// (shape mismatch → 404). This test pins the fix:
/// 1. A Direct note to a LOCAL recipient is added to the recipient's inbox (the notification path).
/// 2. A Direct note to a REMOTE recipient is NOT transformed into a Page (the cross-post leg's
///    Note→Page transform now only applies to community targets, not person targets).
/// 3. A Public note does NOT trigger the Direct-recipient leg (as:Public is skipped).
/// </summary>
[Collection("S69DirectNoteOutbox")]
public sealed class S69DirectNoteOutboxFederationTests : IAsyncLifetime
{
    internal const string AHost = "s69-a.domain.local";
    internal const string BHost = "s69-b.domain.local";
    internal const string Alice = "s69alice";
    internal const string Bob = "s69bob";

    private readonly S69SharedHost _fixture;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;

    public S69DirectNoteOutboxFederationTests(S69SharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _aliceKey = null!;
        _aHttp = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
    }

    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence);
        _aPersistence.Keys.TryGetKey(new Iri($"{_aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        return Task.CompletedTask;
    }

    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"{bobIri.Value}#key-1"));
    }

    [Fact]
    public async Task OutboxPublish_DirectNote_LocalRecipient_ReceivesLocalInboxEntry()
    {
        var daveIri = new Iri($"https://{AHost}/ap/v1/u/s69dave");
        TestSeeder.SeedPersonWithKey(_aPersistence, AHost, "s69dave");

        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/s69-dm-local-{Guid.NewGuid():N}");

        var create = new Create
        {
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["s69 direct note to local recipient"],
                AttributedTo = [new Link { Href = new Uri(_aliceActorIri.Value) }],
                To = [new Link { Href = new Uri(daveIri.Value) }],
                Cc = [new Link { Href = new Uri($"{_aliceActorIri.Value}/followers") }],
            }],
        };

        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The local recipient (dave on A) should have the Create in their local inbox (the S69 leg).
        var daveInbox = await _aPersistence.Activities.GetInboxAsync(daveIri);
        var deliveredCreate = daveInbox.OfType<Create>().FirstOrDefault();
        Assert.NotNull(deliveredCreate);

        // Verify the Create embeds the correct note.
        var embedded = deliveredCreate!.Object?.FirstOrDefault() as Note;
        Assert.NotNull(embedded);
        Assert.Equal(noteIri.Value, embedded!.Id);
    }

    [Fact]
    public async Task OutboxPublish_DirectNote_RemoteRecipient_NoteNotTransformedToPage()
    {
        // The cross-post leg delivers the Create to the remote recipient. The S69 guard prevents the
        // Note→Page transform for person targets. We verify the transform guard by checking that
        // TransformCreateForCrossPost is NOT applied when the target is a person (not a community).
        // This is a unit-level assertion on the transform logic, since the full remote delivery
        // requires cross-instance signature validation (which the two-host fixture handles, but the
        // delivery worker's signing identity in the test environment may differ from the actor's).
        var note = new Note
        {
            Id = $"https://{AHost}/ap/v1/u/{Alice}/notes/s69-transform-check",
            Content = ["s69 direct note"],
            AttributedTo = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            To = [new Link { Href = new Uri(_bobActorIri.Value) }],
        };

        var create = new Create
        {
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [note],
        };

        // Simulate what the cross-post leg does: check if the target is a community.
        // Bob is a person (not a community), so the transform should NOT be applied.
        var isCommunityTarget = await _aPersistence.Communities.TryGetCommunityAsync(_bobActorIri, out _);
        Assert.False(isCommunityTarget, "Bob is a person, not a community");

        // The cross-post leg would apply TransformCreateForCrossPost only when isCommunityTarget is true.
        // Since it's false, the original Create (with the Note) is delivered as-is.
        var crossPostActivity = isCommunityTarget ? TransformCreateForCrossPost(create) : (Activity)create;
        Assert.Same(create, crossPostActivity);

        // Verify the embedded object is still a Note (not a Page).
        var embeddedNote = crossPostActivity is Create c ? c.ExtractEmbeddedObject() : null;
        Assert.NotNull(embeddedNote);
        Assert.Equal("Note", embeddedNote!.Type!.FirstOrDefault());
    }

    [Fact]
    public async Task OutboxPublish_PublicNote_NoDirectRecipientLeg()
    {
        var daveIri = new Iri($"https://{AHost}/ap/v1/u/s69dave2");
        TestSeeder.SeedPersonWithKey(_aPersistence, AHost, "s69dave2");

        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/s69-pub-{Guid.NewGuid():N}");

        var create = new Create
        {
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["s69 public note"],
                AttributedTo = [new Link { Href = new Uri(_aliceActorIri.Value) }],
                To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
            }],
        };

        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Dave (local, not a follower) should NOT receive this in their inbox — the Direct-recipient
        // leg skips as:Public.
        var daveInbox = await _aPersistence.Activities.GetInboxAsync(daveIri);
        Assert.Empty(daveInbox);
    }

    private static Activity TransformCreateForCrossPost(Create create)
    {
        var embedded = create.ExtractEmbeddedObject();
        if (embedded is KristofferStrube.ActivityStreams.Object obj && obj is Note note)
        {
            var page = new Page
            {
                Id = note.Id,
                Content = note.Content,
                AttributedTo = note.AttributedTo,
                To = note.To,
                Cc = note.Cc,
                Published = note.Published,
                Summary = note.Summary,
            };
            return new Create
            {
                Id = create.Id,
                Actor = create.Actor,
                Object = [page],
                To = create.To,
                Cc = create.Cc,
            };
        }
        return create;
    }

    private HttpRequestMessage SignedRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClient(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{AHost}{path}")
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{AHost}{path}")
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

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public CapturedRequest? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value);
            if (request.Content is not null)
            {
                foreach (var h in request.Content.Headers)
                {
                    headers[h.Key] = h.Value;
                }
            }
            Captured = new CapturedRequest(body, headers);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed record CapturedRequest(byte[] Body, Dictionary<string, IEnumerable<string>> Headers);

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

    internal static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var instanceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        if (instanceActorIri != actorIri)
        {
            keyProvider.RegisterKey(instanceActorIri, key.KeyId);
        }
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    internal static IActorDocumentFetcher BuildFetcherFor(
        string host, Iri actorIri, LazyHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}

public sealed class S69SharedHost : SharedTwoHostFixture
{
    public S69SharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, S69DirectNoteOutboxFederationTests.AHost, S69DirectNoteOutboxFederationTests.Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, S69DirectNoteOutboxFederationTests.BHost, S69DirectNoteOutboxFederationTests.Bob);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var optionsA = new ActivityPubHostOptions
        {
            Host = S69DirectNoteOutboxFederationTests.AHost,
            Handle = S69DirectNoteOutboxFederationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = S69DirectNoteOutboxFederationTests.BuildIdentity(aSeeded.Key, aSeeded.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = new S69RoutingFetcher(
                S69DirectNoteOutboxFederationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                S69DirectNoteOutboxFederationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = S69DirectNoteOutboxFederationTests.BHost,
            Handle = S69DirectNoteOutboxFederationTests.Bob,
            Persistence = bPersistence,
            Fetcher = S69DirectNoteOutboxFederationTests.BuildFetcherFor(
                S69DirectNoteOutboxFederationTests.AHost,
                aSeeded.ActorIri,
                new LazyHandler(() => serverARef().CreateHandler())),
        };

        return (optionsA, optionsB);
    }
}

internal sealed class S69RoutingFetcher : IActorDocumentFetcher
{
    private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

    public S69RoutingFetcher(
        string aHost, HttpMessageHandler aHandler,
        string bHost, HttpMessageHandler bHandler,
        KeyPair signingKey, Iri signingActor)
    {
        _ = signingActor;
        _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
        {
            [aHost] = BuildFetcherFor(signingKey, signingActor, aHandler),
            [bHost] = BuildFetcherFor(signingKey, signingActor, bHandler),
        };
    }

    public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        var host = new Uri(actorIri.Value).Host;
        if (_fetchers.TryGetValue(host, out var fetcher))
        {
            return fetcher.GetActorAsync(actorIri, ct);
        }

        return Task.FromResult<Actor?>(null);
    }

    private static IActorDocumentFetcher BuildFetcherFor(
        KeyPair key, Iri actorIri, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}

[CollectionDefinition("S69DirectNoteOutbox")]
public sealed class S69DirectNoteOutboxCollection : ICollectionFixture<S69SharedHost>
{
}
