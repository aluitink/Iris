using System.Net;
using System.Net.Http.Headers;
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
/// Phase 136.19 end-to-end test (Data lifecycle and tombstone retention — the <em>re-animation</em>
/// guard): once an object is deleted (tombstoned), it must not be re-animated by a subsequent write for
/// the same IRI. Before 136.19, the <see cref="Iris.Server.Inbox.CreateActivityHandler"/>,
/// <see cref="Iris.Server.Inbox.UpdateActivityHandler"/>, and the AP proxy re-store path all called
/// <c>PutObjectAsync</c> unconditionally — a Create/Update/proxy-refetch for a tombstoned IRI would
/// overwrite the Tombstone with live content, resurrecting the deleted object. 136.19 adds a
/// tombstone check to each write path so the Tombstone is the authoritative final state.
/// </summary>
/// <remarks>
/// Topology: instance A (tomb-a.domain.local, actor <c>alice</c> — posts the object, the object's home)
/// and instance B (tomb-b.domain.local, actor <c>bob</c> — follows alice). The follow edge bob→alice is
/// recorded on A (A owns alice's follower set, so A's outbound <c>Create</c>/<c>Delete</c> federation
/// targets bob's inbox on B).
/// <list type="number">
/// <item>alice (A) posts the object m1. A's outbox-publish fans the <c>Create</c> out to bob (B); B's
/// <see cref="Iris.Server.Inbox.CreateActivityHandler"/> stores the m1 copy (attributedTo alice).</item>
/// <item>alice (A) deletes m1. A tombstones m1; A's <see cref="Iris.Server.Delivery.DeletePropagationService"/>
/// delivers the <c>Delete</c> to bob's inbox on B. B's <see cref="Iris.Server.Inbox.DeleteActivityHandler"/>
/// tombstones B's m1 copy.</item>
/// <item>A re-delivered <c>Create</c> for m1 (fresh activity IRI, same object IRI) reaches B. B's
/// <see cref="Iris.Server.Inbox.CreateActivityHandler"/> must NOT re-store the live content (the
/// re-animation guard) — the Tombstone is preserved.</item>
/// <item>A late-arriving <c>Update</c> for m1 (attributed to alice) reaches A. A's
/// <see cref="Iris.Server.Inbox.UpdateActivityHandler"/> must NOT re-store the live content (the
/// re-animation guard) — the Tombstone is preserved.</item>
/// </list>
/// Additionally, a unit test verifies that a <see cref="Tombstone"/> stored in the object store is
/// excluded from search (the read path already handles this; the test pins it).
/// </remarks>
public sealed class CrossInstanceTombstoneRetentionIntegrationTests : IDisposable
{
    private const string AHost = "tomb-a.domain.local";
    private const string BHost = "tomb-b.domain.local";
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

    public CrossInstanceTombstoneRetentionIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts alice (the object's author, the object's home). B hosts bob (alice's follower).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;
        _bobKey = bSeeded.Key;
        _bobActorIri = bSeeded.ActorIri;

        // bob → alice is recorded on A (alice's home instance owns her follower set — the propagation
        // target set for her Create/Delete federation).
        _aPersistence.Follows.RecordFollowAsync(_bobActorIri, _aliceActorIri).GetAwaiter().GetResult();

        var aServerHolder = new TestServerHolder();
        var bServerHolder = new TestServerHolder();

        // A: its delivery transport reaches B (delivers the Create/Delete to bob via A's hosted
        // DeliveryWorker); its inbound fetcher routes by actor host so A can validate a signature from
        // either instance.
        _a = aServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentityKeys(aSeeded.Key, aSeeded.ActorIri),
            Fetcher = new RoutingFetcher(
                aSeeded.ActorIri, aSeeded.Key, () => aServerHolder.Server!.CreateHandler(),
                bSeeded.ActorIri, bSeeded.Key, () => bServerHolder.Server!.CreateHandler()),
            DeliveryTransport = () => new LazyHandler(() => bServerHolder.Server!.CreateHandler()),
        });

        // B: its inbound fetcher routes by actor host (B's actor bob → B's own actor doc for bob's signed
        // requests; A's actor alice → A's actor doc for alice's federated Create/Delete); its delivery
        // transport reaches A.
        _b = bServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
            IdentityKeys = BuildIdentityKeys(bSeeded.Key, bSeeded.ActorIri),
            Fetcher = new RoutingFetcher(
                bSeeded.ActorIri, bSeeded.Key, () => bServerHolder.Server!.CreateHandler(),
                aSeeded.ActorIri, aSeeded.Key, () => aServerHolder.Server!.CreateHandler()),
            DeliveryTransport = () => new LazyHandler(() => aServerHolder.Server!.CreateHandler()),
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

    // --- A re-delivered Create for a tombstoned object does not re-animate it (136.19) ------------

    [Fact]
    public async Task ReDeliveredCreate_DoesNotReAnimate_TombstonedObject()
    {
        // Step 1: alice (A) posts m1 on A (the object's home). A's outbox-publish fans the Create out to
        // bob (B); B stores the m1 copy (attributedTo alice).
        var m1 = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");
        var createIri = new Iri($"https://{AHost}/activities/create-{Guid.NewGuid():N}");
        var create = BuildCreate(_aliceActorIri, m1, createIri, "alice's post");
        using var createRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var createResponse = await _aHttp.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);

        // Wait for B to store the federated m1 copy.
        await WaitForAsync(async () => await _bPersistence.Objects.TryGetObjectAsync(m1, out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(m1, out var bOriginal),
            "B should have stored the federated m1 copy");
        Assert.IsType<Note>(bOriginal);
        Assert.Contains("alice's post", bOriginal!.Content?.FirstOrDefault() ?? "");

        // Step 2: alice (A) deletes m1. A tombstones m1; A's DeletePropagationService delivers the Delete
        // to bob's inbox on B. B's DeleteActivityHandler tombstones B's m1 copy.
        var deleteIri = new Iri($"https://{AHost}/activities/delete-{Guid.NewGuid():N}");
        var delete = BuildDelete(_aliceActorIri, m1, deleteIri);
        using var deleteRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, delete, $"/ap/v1/u/{Alice}/outbox");
        using var deleteResponse = await _aHttp.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.Accepted, deleteResponse.StatusCode);

        // Wait for B to tombstone the m1 copy.
        await WaitForAsync(
            async () =>
            {
                if (!await _bPersistence.Objects.TryGetObjectAsync(m1, out var b))
                {
                    return false;
                }

                return b is Tombstone;
            },
            timeout: TimeSpan.FromSeconds(30));

        // (a) B's copy of m1 is a Tombstone (the federated delete applied on the remote instance).
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(m1, out var bTomb));
        Assert.IsType<Tombstone>(bTomb);
        Assert.Equal(m1.Value, bTomb!.Id);

        // Step 3: a re-delivered Create for m1 (fresh activity IRI, same object IRI) reaches B. This
        // simulates a re-delivery (at-least-once delivery semantics) or a backfill re-fetch. B's
        // CreateActivityHandler must NOT re-store the live content (the re-animation guard) — the
        // Tombstone is preserved.
        var reCreateIri = new Iri($"https://{AHost}/activities/create-{Guid.NewGuid():N}");
        var reCreate = BuildCreate(_aliceActorIri, m1, reCreateIri, "alice's post (re-delivered)");

        // Deliver the re-Create directly to B's inbox (bob's inbox — the recipient is the local actor
        // bob on B, so B's CreateActivityHandler processes it).
        using var reCreateRequest = SignedInboxRequest(_aliceActorIri, _aliceKey, reCreate, BHost, $"/ap/v1/u/{Bob}/inbox");
        using var reCreateResponse = await _bHttp.SendAsync(reCreateRequest);
        Assert.Equal(HttpStatusCode.Accepted, reCreateResponse.StatusCode);

        // Give B a moment to process (the guard should skip the re-store, but the activity is still
        // recorded in the inbox).
        await Task.Delay(500);

        // (b) B's copy of m1 is STILL a Tombstone (the re-delivered Create did NOT re-animate it).
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(m1, out var bAfterRecreate));
        Assert.IsType<Tombstone>(bAfterRecreate);
        Assert.Equal(m1.Value, bAfterRecreate!.Id);

        // (c) A (the home) also still has the Tombstone (the re-Create was delivered to B, not A —
        // A's copy is unaffected).
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(m1, out var aTomb));
        Assert.IsType<Tombstone>(aTomb);
    }

    // --- A late-arriving Update for a tombstoned object does not re-animate it (136.19) ------------

    [Fact]
    public async Task LateUpdate_DoesNotReAnimate_TombstonedObject()
    {
        // Step 1: alice (A) posts m1 on A (the object's home).
        var m1 = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");
        var createIri = new Iri($"https://{AHost}/activities/create-{Guid.NewGuid():N}");
        var create = BuildCreate(_aliceActorIri, m1, createIri, "alice's post");
        using var createRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var createResponse = await _aHttp.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);

        // Wait for A to store m1.
        await WaitForAsync(async () => await _aPersistence.Objects.TryGetObjectAsync(m1, out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(m1, out var aOriginal),
            "A should have stored m1");
        Assert.IsType<Note>(aOriginal);

        // Step 2: alice (A) deletes m1. A tombstones m1.
        var deleteIri = new Iri($"https://{AHost}/activities/delete-{Guid.NewGuid():N}");
        var delete = BuildDelete(_aliceActorIri, m1, deleteIri);
        using var deleteRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, delete, $"/ap/v1/u/{Alice}/outbox");
        using var deleteResponse = await _aHttp.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.Accepted, deleteResponse.StatusCode);

        // Wait for A to tombstone m1.
        await WaitForAsync(
            async () =>
            {
                if (!await _aPersistence.Objects.TryGetObjectAsync(m1, out var a))
                {
                    return false;
                }

                return a is Tombstone;
            },
            timeout: TimeSpan.FromSeconds(30));

        // (a) A's copy of m1 is a Tombstone.
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(m1, out var aTomb));
        Assert.IsType<Tombstone>(aTomb);
        Assert.Equal(m1.Value, aTomb!.Id);

        // Step 3: a late-arriving Update for m1 (attributed to alice, the owner) reaches A's outbox.
        // This simulates an edit delivered after the Delete (out-of-order delivery). A's
        // UpdateActivityHandler must NOT re-store the live content (the re-animation guard) — the
        // Tombstone is preserved.
        var updatedNote = new Note
        {
            Id = m1.Value,
            Content = ["alice's post (updated after delete)"],
            AttributedTo = [new Link { Href = new Uri(_aliceActorIri.Value) }],
        };
        var update = new Update
        {
            Id = new Iri($"https://{AHost}/activities/update-{Guid.NewGuid():N}").Value,
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [updatedNote],
        };
        using var updateRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, update, $"/ap/v1/u/{Alice}/outbox");
        using var updateResponse = await _aHttp.SendAsync(updateRequest);
        Assert.Equal(HttpStatusCode.Accepted, updateResponse.StatusCode);

        // Give A a moment to process (the guard should skip the re-store, but the activity is still
        // recorded in the inbox).
        await Task.Delay(500);

        // (b) A's copy of m1 is STILL a Tombstone (the late Update did NOT re-animate it).
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(m1, out var aAfterUpdate));
        Assert.IsType<Tombstone>(aAfterUpdate);
        Assert.Equal(m1.Value, aAfterUpdate!.Id);
    }

    // --- A tombstoned object is excluded from search (136.19) --------------------------------------

    [Fact]
    public async Task TombstonedObject_ExcludedFromSearch()
    {
        // Store a live Note and a Tombstone under different IRIs. Search should find the Note but not
        // the Tombstone (the read path already excludes tombstones; this test pins it).
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");
        var tombIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");

        await _aPersistence.Objects.PutObjectAsync(new Note
        {
            Id = noteIri.Value,
            Content = ["a searchable note about cats"],
            AttributedTo = [new Link { Href = new Uri(_aliceActorIri.Value) }],
        });

        await _aPersistence.Objects.PutObjectAsync(
            tombIri.BuildTombstone("Note"));

        var results = await _aPersistence.Objects.SearchObjectsAsync("cats", 10, 0);

        // The live Note is found; the Tombstone is not (it has no searchable content — it only has
        // formerType + deleted).
        Assert.Single(results);
        Assert.Equal(noteIri.Value, results[0].Id);
    }

    // --- Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="Create"/>: the actor creates <paramref name="objectIri"/> with
    /// <paramref name="content"/>.
    /// </summary>
    private static Create BuildCreate(Iri actorIri, Iri objectIri, Iri createIri, string content) => new()
    {
        Id = createIri.Value,
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = objectIri.Value,
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            },
        ],
    };

    /// <summary>
    /// Builds a <see cref="Delete"/>: the actor deletes <paramref name="objectIri"/> (a bare link
    /// reference — the common Delete shape).
    /// </summary>
    private static Delete BuildDelete(Iri actorIri, Iri objectIri, Iri deleteIri) => new()
    {
        Id = deleteIri.Value,
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on the
    /// author's outbox. Uses the client pipeline (via a <see cref="CaptureHandler"/>) to produce a
    /// correctly signed request, then replays the signed headers onto a fresh request for delivery to
    /// the <see cref="TestServer"/>.
    /// </summary>
    private static HttpRequestMessage SignedOutboxRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var host = new Uri(actorIri.Value).Authority;
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClientForSigning(actorIri, key, capture))
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

        return RebuildSignedRequest(json, capture.Captured!, $"https://{host}{path}");
    }

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to the recipient's inbox on
    /// <paramref name="targetHost"/> (used to deliver a re-Create directly to a remote instance's inbox).
    /// </summary>
    private static HttpRequestMessage SignedInboxRequest(
        Iri actorIri, KeyPair key, Activity activity, string targetHost, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClientForSigning(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{targetHost}{path}")
                    {
                        Content = signedContent,
                    },
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            response.Dispose();
        }

        return RebuildSignedRequest(json, capture.Captured!, $"https://{targetHost}{path}");
    }

    /// <summary>
    /// Rebuilds a signed <see cref="HttpRequestMessage"/> from captured headers (the signing client's
    /// output), replaying the signature headers onto a fresh request for delivery to the
    /// <see cref="TestServer"/>.
    /// </summary>
    private static HttpRequestMessage RebuildSignedRequest(string json, CapturedRequest captured, string url)
    {
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
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

    /// <summary>
    /// Builds the identity keys (key store + provider + signer) for a single local actor.
    /// </summary>
    private static IdentityKeys BuildIdentityKeys(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes an actor IRI to the fetcher for the instance
    /// that hosts it (by host authority), so an instance can validate a signature signed by an actor on
    /// <em>either</em> of the two instances (its own local actor, or the other instance's actor).
    /// </summary>
    private sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            Iri aActorIri, KeyPair aKey, Func<HttpMessageHandler> aHandler,
            Iri bActorIri, KeyPair bKey, Func<HttpMessageHandler> bHandler)
        {
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [new Uri(aActorIri.Value).Authority] = BuildHostFetcher(aActorIri, aKey, aHandler),
                [new Uri(bActorIri.Value).Authority] = BuildHostFetcher(bActorIri, bKey, bHandler),
            };
        }

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            var host = new Uri(actorIri.Value).Authority;
            if (_fetchers.TryGetValue(host, out var fetcher))
            {
                return fetcher.GetActorAsync(actorIri, ct);
            }

            return Task.FromResult<Actor?>(null);
        }

        private static IActorDocumentFetcher BuildHostFetcher(
            Iri actorIri, KeyPair key, Func<HttpMessageHandler> handler)
        {
            var keyStore = new InMemoryKeyStore();
            keyStore.PutKey(key);
            var keyProvider = new InMemoryKeyProvider(keyStore);
            keyProvider.RegisterKey(actorIri, key.KeyId);
            var signer = new HttpSignatureSigner(keyStore);

            var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
            var client = factory.Create(
                new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
                new LazyHandler(handler));

            return new IrisActorDocumentFetcher(client, new RemoteActorCache());
        }
    }

    /// <summary>
    /// Builds a signing <see cref="IActivityPubClient"/> (signed as <paramref name="actorIri"/>) routing
    /// to <paramref name="handler"/> (a <see cref="CaptureHandler"/>) — used to produce a correctly signed
    /// request whose headers are then replayed.
    /// </summary>
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

    /// <summary>
    /// A mutable holder for a <see cref="TestServer"/>, used to break the fetcher/client ↔ server
    /// circularity in the constructor (the fetcher reads <see cref="Server"/> lazily, after it is
    /// populated).
    /// </summary>
    private sealed class TestServerHolder
    {
        public TestServer? Server { get; set; }
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that captures the request (body + headers) the signing client
    /// produces, returning a stub 200. The captured signed headers are then replayed onto a fresh request
    /// for delivery to the real <see cref="TestServer"/>.
    /// </summary>
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
}
