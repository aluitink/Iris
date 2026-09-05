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
/// Phase 28.2 integration test: relay fan-out for <see cref="Update"/> (object edit) and
/// <see cref="Delete"/> (content removal) on the outbox-publish path. When a local author edits or
/// deletes a note (an <see cref="Update"/> or <see cref="Delete"/> published to
/// <c>POST /ap/v1/u/{handle}/outbox</c>), the activity must reach the author's subscribed <em>relays</em>
/// (F-06, ActivityPub §5.1.3) so the relays' copies of the object are kept in sync — mirroring the
/// <see cref="Create"/>/<see cref="Announce"/> relay fan-out added in 28.1.
/// </summary>
/// <remarks>
/// <para>
/// Topology: instance B (update-delete-relay-b.domain.local, <c>bob</c>) hosts the local author
/// <c>bob</c>, who has subscribed to a relay (the <c>relay</c> actor on instance R,
/// update-delete-relay-r.example.com). Bob publishes a <see cref="Create"/> (a Note) to his outbox; the
/// 28.1 fan-out delivers it to the relay, and R's <see cref="Iris.Server.Inbox.CreateActivityHandler"/>
/// stores the Note in R's object store (attributedTo bob).
/// </para>
/// <para>
/// Under test (the 28.2 fix): the outbox-publish <see cref="Update"/> and <see cref="Delete"/> branches
/// route to <see cref="Iris.Server.Inbox.UpdateActivityHandler"/> /
/// <see cref="Iris.Server.Inbox.DeleteActivityHandler"/>, which call
/// <see cref="Iris.Server.Delivery.IDeletePropagationService"/>. That service now also fans the
/// activity out to the author's subscribed relays (in addition to the remote followers), so R receives
/// and applies the <see cref="Update"/> (refreshing its stored Note) and the <see cref="Delete"/>
/// (tombstoning its stored Note).
/// </para>
/// </remarks>
public sealed class UpdateDeleteRelayFanOutIntegrationTests : IDisposable
{
    private const string BHost = "update-delete-relay-b.domain.local";
    private const string RelayHost = "update-delete-relay-r.example.com";
    private const string Bob = "bob";
    private const string Relay = "relay";

    private readonly TestServer _b;
    private readonly TestServer _relay;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly InMemoryPersistenceProvider _relayPersistence;
    private readonly KeyPair _bobKey;
    private readonly Iri _bobActorIri;
    private readonly Iri _relayActorIri;

    public UpdateDeleteRelayFanOutIntegrationTests()
    {
        _bPersistence = new InMemoryPersistenceProvider();
        _relayPersistence = new InMemoryPersistenceProvider();

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        _bobActorIri = bSeeded.ActorIri;

        var relaySeeded = TestSeeder.SeedPersonWithKey(_relayPersistence, RelayHost, Relay);
        _relayActorIri = relaySeeded.ActorIri;

        // bob (on B) has subscribed to the relay: the F-06 subscription edge (recorded in B's relay
        // store, which is the source DeliverToRelaysAsync reads).
        _bPersistence.Relays
            .RecordRelayAsync(_bobActorIri, _relayActorIri)
            .GetAwaiter().GetResult();

        // B hosts bob; its outbound delivery routes to the relay's TestServer (so the fanned-out
        // Update/Delete reaches the relay's inbox), signed as bob. B's fetcher resolves the relay's
        // document from R and bob's document from B.
        _b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
            IdentityKeys = BuildIdentity(bSeeded.Key, bSeeded.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => _relay!.CreateHandler()),
            Fetcher = new RoutingFetcher(
                BHost, new LazyHandler(() => _b!.CreateHandler()),
                RelayHost, new LazyHandler(() => _relay!.CreateHandler()),
                bSeeded.Key, bSeeded.ActorIri),
        });

        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false);

        // R hosts the relay; its fetcher is wired to B so R validates the fanned-out activity by
        // fetching B's actor document (bob's key).
        _relay = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = RelayHost,
            Handle = Relay,
            Persistence = _relayPersistence,
            IdentityKeys = BuildIdentity(relaySeeded.Key, relaySeeded.ActorIri),
            Fetcher = BuildFetcherFor(RelayHost, Relay, relaySeeded.Key, new LazyHandler(() => _b.CreateHandler())),
        });
    }

    public void Dispose()
    {
        _bHttp.Dispose();
        _b.Dispose();
        _relay.Dispose();
    }

    // --- An Update published to the author's outbox is fanned out to the subscribed relay ----------

    [Fact]
    public async Task OutboxPublish_Update_IsFannedOutToRelay()
    {
        // Step 1: bob publishes a Create (a Note) to B's outbox. The 28.1 fan-out delivers it to the
        // relay; R's CreateActivityHandler stores the Note in R's object store (attributedTo bob).
        var create = BuildCreate(_bobActorIri, "original note from bob");
        using (var request = SignedRequest(_bobActorIri, _bobKey, create, BHost, $"/ap/v1/u/{Bob}/outbox"))
        {
            using var response = await _bHttp.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var createIri = await LearnMintedIdAsync(response);
            var noteIri = await WaitForNoteIriAsync(createIri, _bPersistence, _relayPersistence);

            // Verify R stored the original content (the 28.1 Create fan-out worked).
            Assert.True(
                await _relayPersistence.Objects.TryGetObjectAsync(noteIri, out var originalNote),
                "R should have stored the federated Note in its object store");
            Assert.Contains("original note from bob", originalNote!.Content?.FirstOrDefault() ?? "");

            // Step 2 (the 28.2 fix): bob publishes an Update (the same Note with new content) to B's
            // outbox. B's outbox-publish routes to UpdateActivityHandler → PropagateUpdateAsync, which
            // now also fans out to the relay (F-06). R's UpdateActivityHandler accepts it (remote actor
            // bob, attributed copy) and refreshes R's stored Note with the updated content.
            var updatedNote = new Note
            {
                Id = noteIri.Value,
                Content = ["updated note from bob"],
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

            // Wait for R to refresh its stored Note with the updated content.
            await TestFederation.WaitForAsync(
                async () =>
                {
                    if (!await _relayPersistence.Objects.TryGetObjectAsync(noteIri, out var refreshed))
                    {
                        return false;
                    }

                    return refreshed!.Content?.FirstOrDefault() is { } c && c.Contains("updated note from bob");
                },
                timeout: TimeSpan.FromSeconds(10));

            // Assert: R's stored Note has the updated content (not the original).
            Assert.True(
                await _relayPersistence.Objects.TryGetObjectAsync(noteIri, out var finalNote),
                "R should still have the Note in its object store");
            Assert.Contains("updated note from bob", finalNote!.Content?.FirstOrDefault() ?? "");
            Assert.DoesNotContain("original note from bob", finalNote!.Content?.FirstOrDefault() ?? "");
        }
    }

    // --- A Delete published to the author's outbox is fanned out to the subscribed relay -----------

    [Fact]
    public async Task OutboxPublish_Delete_IsFannedOutToRelay()
    {
        // Step 1: bob publishes a Create (a Note) to B's outbox. The 28.1 fan-out delivers it to the
        // relay; R's CreateActivityHandler stores the Note in R's object store (attributedTo bob).
        var create = BuildCreate(_bobActorIri, "note to be deleted by bob");
        using (var request = SignedRequest(_bobActorIri, _bobKey, create, BHost, $"/ap/v1/u/{Bob}/outbox"))
        {
            using var response = await _bHttp.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var createIri = await LearnMintedIdAsync(response);
            var noteIri = await WaitForNoteIriAsync(createIri, _bPersistence, _relayPersistence);

            // Verify R stored the Note before the delete.
            Assert.True(
                await _relayPersistence.Objects.TryGetObjectAsync(noteIri, out _),
                "R should have stored the federated Note in its object store before the Delete");

            // Step 2 (the 28.2 fix): bob publishes a Delete (the Note) to B's outbox. B's outbox-publish
            // routes to DeleteActivityHandler → PropagateDeleteAsync, which now also fans out to the
            // relay (F-06). R's DeleteActivityHandler tombstones R's stored Note.
            var delete = new Delete
            {
                Actor = [new Link { Href = new Uri(_bobActorIri.Value) }],
                Object = [new Link { Href = noteIri.Uri }],
            };

            using var deleteRequest = SignedRequest(_bobActorIri, _bobKey, delete, BHost, $"/ap/v1/u/{Bob}/outbox");
            using var deleteResponse = await _bHttp.SendAsync(deleteRequest);
            Assert.Equal(HttpStatusCode.Accepted, deleteResponse.StatusCode);

            // Wait for R to tombstone its stored Note. The object store replaces the Note with a
            // Tombstone (or removes it); either way the original content is gone.
            await TestFederation.WaitForAsync(
                async () =>
                {
                    if (!await _relayPersistence.Objects.TryGetObjectAsync(noteIri, out var current))
                    {
                        return true; // removed from the store
                    }

                    // The object is now a Tombstone (no content), or its content no longer has the note.
                    var content = current!.Content?.FirstOrDefault();
                    return content is null
                        || !(content is { Length: > 0 })
                        || !content.Contains("note to be deleted by bob");
                },
                timeout: TimeSpan.FromSeconds(10));

            // Assert: R's stored Note no longer carries the original content (it was tombstoned or
            // removed).
            if (await _relayPersistence.Objects.TryGetObjectAsync(noteIri, out var finalNote))
            {
                var content = finalNote!.Content?.FirstOrDefault() ?? "";
                Assert.DoesNotContain("note to be deleted by bob", content);
            }
        }
    }

    // --- A post with no subscribed relays: the Update is not fanned out (negative control) ---------

    [Fact]
    public async Task OutboxPublish_Update_WithNoSubscribedRelays_IsNotFannedOut()
    {
        // A fresh author (carol) on B with no subscribed relays publishes a Create then an Update.
        // The relay store (for carol) is empty, so no relay fan-out occurs for the Update.
        var carolSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, "carol");
        var carolActorIri = carolSeeded.ActorIri;

        var create = BuildCreate(carolActorIri, "carol's original note");
        using (var request = SignedRequest(carolActorIri, carolSeeded.Key, create, BHost, "/ap/v1/u/carol/outbox"))
        {
            using var response = await _bHttp.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var createIri = await LearnMintedIdAsync(response);

            // Wait for B to store carol's Note (the Create was recorded locally on B).
            await TestFederation.WaitForAsync(
                async () =>
                {
                    if (!await _bPersistence.Activities.TryGetActivityAsync(createIri, out var storedCreate))
                    {
                        return false;
                    }

                    var embedded = (storedCreate as Create)?.ExtractEmbeddedObject();
                    var noteIri = embedded?.ResolveObjectIri();
                    return noteIri.HasValue
                        && await _bPersistence.Objects.TryGetObjectAsync(noteIri.Value, out _);
                },
                timeout: TimeSpan.FromSeconds(10));

            // Resolve carol's note IRI.
            Iri? noteIri = null;
            if (await _bPersistence.Activities.TryGetActivityAsync(createIri, out var storedCreate))
            {
                noteIri = (storedCreate as Create)?.ExtractEmbeddedObject()?.ResolveObjectIri();
            }

            Assert.NotNull(noteIri);

            // carol publishes an Update to her own note.
            var updatedNote = new Note
            {
                Id = noteIri!.Value.Value,
                Content = ["carol's updated note"],
                AttributedTo = [new Link { Href = new Uri(carolActorIri.Value) }],
            };
            var update = new Update
            {
                Actor = [new Link { Href = new Uri(carolActorIri.Value) }],
                Object = [updatedNote],
            };

            using var updateRequest = SignedRequest(carolActorIri, carolSeeded.Key, update, BHost, "/ap/v1/u/carol/outbox");
            using var updateResponse = await _bHttp.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.Accepted, updateResponse.StatusCode);

            // Wait for B to refresh carol's Note locally (the Update was recorded).
            await TestFederation.WaitForAsync(
                async () =>
                {
                    if (!await _bPersistence.Objects.TryGetObjectAsync(noteIri!.Value, out var refreshed))
                    {
                        return false;
                    }

                    return refreshed!.Content?.FirstOrDefault() is { } c && c.Contains("carol's updated note");
                },
                timeout: TimeSpan.FromSeconds(10));

            // The relay stored nothing for carol (no subscription) — R's object store has no trace of
            // carol's note.
            Assert.False(
                await _relayPersistence.Objects.TryGetObjectAsync(noteIri!.Value, out _),
                "R should not have stored carol's Note (carol has no subscribed relays)");
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

    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        return new Iri(id!);
    }

    private static async Task<Iri> WaitForNoteIriAsync(
        Iri createIri,
        InMemoryPersistenceProvider sourcePersistence,
        InMemoryPersistenceProvider targetPersistence)
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
            timeout: TimeSpan.FromSeconds(10));

        return noteIri!.Value;
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
            string bHost, HttpMessageHandler bHandler,
            string relayHost, HttpMessageHandler relayHandler,
            KeyPair signingKey, Iri signingActor)
        {
            _ = signingActor;
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [bHost] = BuildFetcherFor(bHost, "local", signingKey, bHandler),
                [relayHost] = BuildFetcherFor(relayHost, "local", signingKey, relayHandler),
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
