using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 27.1 integration test: cross-instance <see cref="Update"/> (object edit) propagation. When a
/// remote author edits a note (an <see cref="Update"/> of a <see cref="Note"/>), the update federates to
/// the followers' instances, and each instance that holds a federated copy of the note refreshes its
/// stored object with the updated content.
/// </summary>
/// <remarks>
/// <para>
/// Topology: instance A (update-a.domain.local, <c>alice</c>) and instance B (update-b.domain.local,
/// <c>bob</c>). Alice follows bob (the follow edge is recorded on B, bob's home instance, so B's
/// <c>GetRemoteNonBlockedFollowersAsync</c> finds alice and delivers to her).
/// </para>
/// <para>
/// Flow:
/// <list type="number">
/// <item>Bob publishes a <see cref="Create"/> (a Note) to B's outbox. B's outbox-publish fans out the
/// Create to alice on A. A's <see cref="Iris.Server.Inbox.CreateActivityHandler"/> stores the Note in
/// A's object store (attributedTo bob).</item>
/// <item>Bob publishes an <see cref="Update"/> (the same Note with new content) to B's outbox. B's
/// outbox-publish invokes <see cref="Iris.Server.Inbox.UpdateActivityHandler"/> (the local object-store
/// refresh + <c>PropagateUpdateAsync</c> to remote followers). The Update is delivered to alice on A.</item>
/// <item>A's inbox receives the Update. A's <see cref="Iris.Server.Inbox.UpdateActivityHandler"/> accepts
/// it (remote actor bob, attributed copy) and refreshes A's stored Note with the updated content.</item>
/// </list>
/// The test asserts that A's stored Note has the updated content (not the original).
/// </para>
/// <para>
/// The under-test invariant is the outbox-publish <c>Update</c> branch: before the fix, an
/// <see cref="Update"/> published to the outbox fell into the catch-all branch with no recipient
/// resolution (no delivery), so remote instances never received the update and kept serving stale
/// content. The fix routes the <c>Update</c> to <see cref="Iris.Server.Inbox.UpdateActivityHandler"/>
/// (mirroring the <c>Delete</c> branch), which refreshes the local copy and propagates to remote
/// followers.
/// </para>
/// </remarks>
public sealed class UpdatePropagationIntegrationTests : IDisposable
{
    private const string AHost = "update-a.domain.local";
    private const string BHost = "update-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _bobKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;

    public UpdatePropagationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        _bobActorIri = bSeeded.ActorIri;

        // Alice follows bob: the follow edge is recorded on B (bob's home instance) so that B's
        // GetRemoteNonBlockedFollowersAsync finds alice and delivers the Create/Update to her inbox.
        _bPersistence.Follows.RecordFollowAsync(_aliceActorIri, _bobActorIri).GetAwaiter().GetResult();

        _a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentity(aSeeded.Key, aSeeded.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => _b!.CreateHandler()),
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => _a!.CreateHandler()),
                BHost, new LazyHandler(() => _b!.CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
        });

        _b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
            IdentityKeys = BuildIdentity(bSeeded.Key, bSeeded.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => _a!.CreateHandler()),
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => _a!.CreateHandler()),
                BHost, new LazyHandler(() => _b!.CreateHandler()),
                bSeeded.Key, bSeeded.ActorIri),
        });

        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);
        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    [Fact]
    public async Task OutboxPublish_UpdateFederatesToFollower_RefreshesStoredCopy()
    {
        // Step 1: Bob publishes a Create (a Note) to B's outbox. The Create federates to alice on A,
        // and A's CreateActivityHandler stores the Note in A's object store.
        var create = BuildCreate(_bobActorIri, "original content from bob");
        using (var request = SignedRequest(_bobActorIri, _bobKey, create, BHost, $"/ap/v1/u/{Bob}/outbox"))
        {
            using var response = await _bHttp.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var createIri = await LearnMintedIdAsync(response);
            var noteIri = await WaitForNoteIriAsync(createIri, _bPersistence, _aPersistence);

            // Verify A stored the original content.
            Assert.True(
                await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var originalNote),
                "A should have stored the federated Note in its object store");
            Assert.Contains("original content from bob", originalNote!.Content?.FirstOrDefault() ?? "");

            // Step 2: Bob publishes an Update (the same Note with new content) to B's outbox.
            var updatedNote = new Note
            {
                Id = noteIri.Value,
                Content = ["updated content from bob"],
                AttributedTo = [new Link { Href = new Uri(_bobActorIri.Value) }],
            };
            var update = new Update
            {
                Actor = [new Link { Href = new Uri(_bobActorIri.Value) }],
                Object = [updatedNote],
            };

            using var updateRequest = SignedRequest(_bobActorIri, _bobKey, update, BHost, $"/ap/v1/u/{Bob}/outbox");
            using var updateResponse = await _bHttp.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.Accepted, updateResponse.StatusCode);

            // Wait for A to refresh its stored Note with the updated content.
            await TestFederation.WaitForAsync(
                async () =>
                {
                    if (!await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var refreshed))
                    {
                        return false;
                    }

                    return refreshed!.Content?.FirstOrDefault() is { } c && c.Contains("updated content from bob");
                },
                timeout: TimeSpan.FromSeconds(30));

            // Assert: A's stored Note has the updated content (not the original).
            Assert.True(
                await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var finalNote),
                "A should still have the Note in its object store");
            Assert.Contains("updated content from bob", finalNote!.Content?.FirstOrDefault() ?? "");
            Assert.DoesNotContain("original content from bob", finalNote!.Content?.FirstOrDefault() ?? "");
        }
    }

    [Fact]
    public async Task OutboxPublish_UpdateWithNoRemoteFollowers_RefreshesLocalCopyOnly()
    {
        var daveSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, "dave");
        var daveActorIri = daveSeeded.ActorIri;

        var create = BuildCreate(daveActorIri, "dave's original note");
        using (var createRequest = SignedRequest(daveActorIri, daveSeeded.Key, create, AHost, "/ap/v1/u/dave/outbox"))
        {
            using var createResponse = await _aHttp.SendAsync(createRequest);
            Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
            var createIri = await LearnMintedIdAsync(createResponse);

            var noteIri = await WaitForNoteIriAsync(createIri, _aPersistence, _aPersistence);

            var updatedNote = new Note
            {
                Id = noteIri.Value,
                Content = ["dave's updated note"],
                AttributedTo = [new Link { Href = new Uri(daveActorIri.Value) }],
            };
            var update = new Update
            {
                Actor = [new Link { Href = new Uri(daveActorIri.Value) }],
                Object = [updatedNote],
            };

            using var updateRequest = SignedRequest(daveActorIri, daveSeeded.Key, update, AHost, "/ap/v1/u/dave/outbox");
            using var updateResponse = await _aHttp.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.Accepted, updateResponse.StatusCode);

            await TestFederation.WaitForAsync(
                async () =>
                {
                    if (!await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var refreshed))
                    {
                        return false;
                    }

                    return refreshed!.Content?.FirstOrDefault() is { } c && c.Contains("dave's updated note");
                },
                timeout: TimeSpan.FromSeconds(10));

            Assert.True(
                await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var finalNote),
                "A should still have the Note in its object store");
            Assert.Contains("dave's updated note", finalNote!.Content?.FirstOrDefault() ?? "");
        }
    }

    [Fact]
    public async Task OutboxPublish_UpdateByNonOwnerOfRemoteObject_IsNoOp()
    {
        var create = BuildCreate(_bobActorIri, "bob's note that alice cannot edit");
        using (var request = SignedRequest(_bobActorIri, _bobKey, create, BHost, $"/ap/v1/u/{Bob}/outbox"))
        {
            using var response = await _bHttp.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var createIri = await LearnMintedIdAsync(response);
            var noteIri = await WaitForNoteIriAsync(createIri, _bPersistence, _aPersistence);

            Assert.True(
                await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var originalNote),
                "A should have stored the federated Note");
            var originalContent = originalNote!.Content?.FirstOrDefault() ?? "";

            // Alice (local on A) publishes an Update to bob's note IRI. The Update's actor is alice,
            // but the stored object is attributedTo bob. The owner guard rejects it.
            var maliciousNote = new Note
            {
                Id = noteIri.Value,
                Content = ["alice's forged update"],
                AttributedTo = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            };
            var update = new Update
            {
                Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
                Object = [maliciousNote],
            };

            using var updateRequest = SignedRequest(_aliceActorIri, _aliceKey, update, AHost, $"/ap/v1/u/{Alice}/outbox");
            using var updateResponse = await _aHttp.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.Accepted, updateResponse.StatusCode);

            // The stored note on A is unchanged (the owner guard rejected the update).
            Assert.True(
                await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var unchangedNote),
                "A should still have the Note");
            Assert.Equal(originalContent, unchangedNote!.Content?.FirstOrDefault());
        }
    }

    // --- Helpers ---------------------------------------------------------------------------

    private static Create BuildCreate(Iri actorIri, string content) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            },
        ],
    };

    private async Task<Iri> WaitForNoteIriAsync(Iri createIri, InMemoryPersistenceProvider sourcePersistence, InMemoryPersistenceProvider targetPersistence)
    {
        Assert.True(
            await sourcePersistence.Activities.TryGetActivityAsync(createIri, out var storedCreate),
            "Source instance should have stored the Create in its activity store");
        Assert.IsType<Create>(storedCreate);

        var create = (Create)storedCreate!;
        var embedded = create.ExtractEmbeddedObject();
        Assert.NotNull(embedded);
        var noteIri = embedded!.ResolveObjectIri();
        Assert.NotNull(noteIri);

        await TestFederation.WaitForAsync(
            async () => await targetPersistence.Objects.TryGetObjectAsync(noteIri!.Value, out _),
            timeout: TimeSpan.FromSeconds(30));

        return noteIri!.Value;
    }

    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        return new Iri(id!);
    }

    private HttpRequestMessage SignedRequest(Iri actorIri, KeyPair key, Activity activity, string host, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();

        using (var client = BuildClient(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}")
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}")
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

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, HttpMessageHandler handler)
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
            handler);

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            string aHost, HttpMessageHandler aHandler,
            string bHost, HttpMessageHandler bHandler,
            KeyPair signingKey, Iri signingActor)
        {
            _ = signingActor;
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [aHost] = BuildFetcherFor(aHost, "local", signingKey, aHandler),
                [bHost] = BuildFetcherFor(bHost, "local", signingKey, bHandler),
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
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(byte[] Body, Dictionary<string, List<string>> Headers);
}
