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
/// Phase 136.9 end-to-end test (Update/Delete/Undo propagation — the delete/tombstone <em>remote
/// visibility</em> half): when a parent post is deleted on its home instance, the thread under it is
/// collapsed <strong>on every instance that holds a copy</strong>, not just the home. A remote follower's
/// instance that federated the parent (and recorded a local reply to it) must tombstone the parent copy
/// <em>and</em> drop the parent → reply edge, so the tombstoned parent's <c>replies</c> collection is empty
/// there too (136.9 — before the fix, deleting a parent left its replies orphaned but still listed and
/// served under the now-tombstoned parent, on both the home and the remote instance).
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, actor <c>bob</c> — posts the parent m1, the object's home — the
/// Lemmy stand-in) and instance B (b.domain.local, actor <c>alice</c> — follows bob). The follow edge
/// alice→bob is recorded on A (A owns bob's follower set, so A's outbound <c>Create</c>/<c>Delete</c>
/// federation targets alice's inbox on B).
/// <list type="number">
/// <item>bob (A) posts the parent m1 on A. A's outbox-publish fans the <c>Create</c> out to alice (B); B's
/// <see cref="Iris.Server.Inbox.CreateActivityHandler"/> stores the m1 copy (attributedTo bob).</item>
/// <item>alice (B) replies to m1 (r1). B's <see cref="Iris.Server.Inbox.CreateActivityHandler"/> stores r1
/// and records the m1 → r1 reply edge in B's reply store (the thread on the remote instance).</item>
/// <item>bob (A) deletes m1. A tombstones m1 and (136.9) collapses the thread under it; A's
/// <see cref="Iris.Server.Delivery.DeletePropagationService"/> delivers the <c>Delete</c> to alice's inbox
/// on B (bob's remote follower). B's <see cref="Iris.Server.Inbox.DeleteActivityHandler"/> (the owner guard
/// accepts the remote author bob, whom B holds an attributed copy of) tombstones B's m1 copy <em>and</em>
/// removes the m1 → r1 reply edge (the thread collapses on the remote instance too).</item>
/// </list>
/// The assertion is on <strong>B</strong> (the remote instance holding the copy + the reply edge): the m1
/// copy is a <c>Tombstone</c> and B's replies collection for m1 is empty (the thread collapsed there — no
/// orphaned-but-still-served replies under the tombstoned parent). The reply object r1 itself remains
/// stored (fetchable by direct IRI) — only the thread listing is collapsed.
/// </remarks>
public sealed class CrossInstanceDeleteThreadCollapseIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _bobKey;
    private readonly KeyPair _aliceKey;
    private readonly Iri _bobActorIri;
    private readonly Iri _aliceActorIri;

    public CrossInstanceDeleteThreadCollapseIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts bob (the parent's author, the object's home). B hosts alice (bob's follower).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Bob);
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Alice);
        _bobKey = aSeeded.Key;
        _bobActorIri = aSeeded.ActorIri;
        _aliceKey = bSeeded.Key;
        _aliceActorIri = bSeeded.ActorIri;

        // alice → bob is recorded on A (bob's home instance owns his follower set — the propagation
        // target set for his Create/Delete federation).
        _aPersistence.Follows.RecordFollowAsync(_aliceActorIri, _bobActorIri).GetAwaiter().GetResult();

        var aServerHolder = new TestServerHolder();
        var bServerHolder = new TestServerHolder();

        // A: its delivery transport reaches B (delivers the Create/Delete to alice via A's hosted
        // DeliveryWorker); its inbound fetcher routes by actor host (A's actor bob → A's own actor doc for
        // bob's signed Create/Delete; B's actor alice → B's actor doc for alice's signed reply) so A can
        // validate a signature from either instance.
        _a = aServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Bob,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentityKeys(aSeeded.Key, aSeeded.ActorIri),
            Fetcher = new RoutingFetcher(
                aSeeded.ActorIri, aSeeded.Key, () => aServerHolder.Server!.CreateHandler(),
                bSeeded.ActorIri, bSeeded.Key, () => bServerHolder.Server!.CreateHandler()),
            DeliveryTransport = () => new LazyHandler(() => bServerHolder.Server!.CreateHandler()),
        });

        // B: its inbound fetcher routes by actor host (B's actor alice → B's own actor doc for alice's
        // signed reply; A's actor bob → A's actor doc for bob's federated Create/Delete); its object
        // fetcher reaches A (resolves the remote parent m1's author when the reply is published); its
        // delivery transport reaches A (delivers the reply's Create to the parent's author, bob, via B's
        // hosted DeliveryWorker).
        _b = bServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Alice,
            Persistence = _bPersistence,
            IdentityKeys = BuildIdentityKeys(bSeeded.Key, bSeeded.ActorIri),
            Fetcher = new RoutingFetcher(
                bSeeded.ActorIri, bSeeded.Key, () => bServerHolder.Server!.CreateHandler(),
                aSeeded.ActorIri, aSeeded.Key, () => aServerHolder.Server!.CreateHandler()),
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

    // --- Deleting a parent collapses the thread on the remote instance too (136.9) ------------

    [Fact]
    public async Task DeleteOfParent_CollapsesThreadOnRemoteInstance()
    {
        // Step 1: bob (A) posts the parent m1 on A (the object's home). The IRI is in A's /ap/v1 serving
        // namespace so B can fetch it over the wire when the reply is published. A's outbox-publish fans the
        // Create out to alice (B); B stores the m1 copy (attributedTo bob).
        var m1 = new Iri($"https://{AHost}/ap/v1/u/{Bob}/notes/{Guid.NewGuid():N}");
        await _aPersistence.Objects.PutObjectAsync(new Note
        {
            Id = m1.Value,
            Content = ["bob's parent post"],
            AttributedTo = [new Link { Href = new Uri(_bobActorIri.Value) }],
        });

        // Publish the Create through A's outbox (the local-outbox publish path — fans out to bob's remote
        // follower, alice on B).
        var createIri = new Iri($"https://{AHost}/activities/create-{Guid.NewGuid():N}");
        var create = BuildCreate(_bobActorIri, m1, createIri, "bob's parent post", inReplyTo: null);
        using var createRequest = SignedOutboxRequest(_bobActorIri, _bobKey, create, $"/ap/v1/u/{Bob}/outbox");
        using var createResponse = await _aHttp.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);

        // Wait for B to store the federated parent copy.
        await WaitForAsync(async () => await _bPersistence.Objects.TryGetObjectAsync(m1, out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(m1, out var bParent),
            "B should have stored the federated parent copy");

        // Step 2: alice (B) replies to m1 (r1). B's CreateActivityHandler stores r1 and records the m1 → r1
        // reply edge in B's reply store (the thread on the remote instance).
        var r1 = new Iri($"https://{BHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");
        var replyCreateIri = new Iri($"https://{BHost}/activities/create-{Guid.NewGuid():N}");
        var reply = BuildCreate(_aliceActorIri, r1, replyCreateIri, "alice's reply to bob's post", inReplyTo: m1);
        using var replyRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, reply, $"/ap/v1/u/{Alice}/outbox");
        using var replyResponse = await _bHttp.SendAsync(replyRequest);
        Assert.Equal(HttpStatusCode.Accepted, replyResponse.StatusCode);

        // Wait for B to record the m1 → r1 reply edge (the thread on the remote instance).
        await WaitForAsync(async () => await _bPersistence.Replies.HasReplyAsync(m1, r1),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(await _bPersistence.Replies.HasReplyAsync(m1, r1),
            "B should have recorded the m1 → r1 reply edge (the thread on the remote instance)");

        // Step 3: bob (A) deletes m1. A tombstones m1 and collapses the thread under it; A's
        // DeletePropagationService delivers the Delete to alice's inbox on B. B's DeleteActivityHandler
        // tombstones B's m1 copy AND (136.9) removes the m1 → r1 reply edge (the thread collapses on the
        // remote instance too).
        var deleteIri = new Iri($"https://{AHost}/activities/delete-{Guid.NewGuid():N}");
        var delete = BuildDelete(_bobActorIri, m1, deleteIri);
        using var deleteRequest = SignedOutboxRequest(_bobActorIri, _bobKey, delete, $"/ap/v1/u/{Bob}/outbox");
        using var deleteResponse = await _aHttp.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.Accepted, deleteResponse.StatusCode);

        // Wait for B to tombstone the parent copy AND collapse the thread (both effects on the remote
        // instance).
        await WaitForAsync(
            async () =>
            {
                if (!await _bPersistence.Objects.TryGetObjectAsync(m1, out var b) || b is not Tombstone)
                {
                    return false;
                }

                return (await _bPersistence.Replies.GetRepliesAsync(m1)).Count == 0;
            },
            timeout: TimeSpan.FromSeconds(30));

        // (a) B's copy of the parent is a Tombstone (the federated delete applied on the remote instance).
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(m1, out var bTomb));
        Assert.IsType<Tombstone>(bTomb);
        Assert.Equal(m1.Value, bTomb!.Id);

        // (b) B's replies collection for the tombstoned parent is empty (136.9 — the thread collapsed on
        // the remote instance too: no orphaned-but-still-served reply under the tombstoned parent).
        Assert.Empty(await _bPersistence.Replies.GetRepliesAsync(m1));
        Assert.False(await _bPersistence.Replies.HasReplyAsync(m1, r1));

        // (c) The reply object r1 itself remains stored on B (fetchable by direct IRI) — only the thread
        // listing is collapsed, not the content.
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(r1, out var bReply));
        Assert.IsType<Note>(bReply);

        // (d) A (the home) also collapsed: the parent is tombstoned and its replies collection is empty.
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(m1, out var aTomb));
        Assert.IsType<Tombstone>(aTomb);
    }

    // --- Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="Create"/>: the actor creates <paramref name="objectIri"/> with
    /// <paramref name="content"/> (optionally replying to <paramref name="inReplyTo"/>).
    /// </summary>
    private static Create BuildCreate(Iri actorIri, Iri objectIri, Iri createIri, string content, Iri? inReplyTo) => new()
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
                InReplyTo = inReplyTo is { } parent ? [new Link { Href = new Uri(parent.Value) }] : null,
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
    /// author's outbox. Uses the client pipeline (via a <see cref="CaptureHandler"/>) to produce a correctly
    /// signed request, then replays the signed headers onto a fresh request for delivery to the
    /// <see cref="TestServer"/>.
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
    /// Builds an <see cref="IActorDocumentFetcher"/> whose client (signed as <paramref name="handle"/>)
    /// routes to the (deferred) <paramref name="targetServer"/> — i.e. the instance's fetcher reaches the
    /// other instance's actor documents.
    /// </summary>
    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, Func<TestServer> targetServer)
    {
        var client = BuildClient(host, handle, key, targetServer);
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes an actor IRI to the fetcher for the instance
    /// that hosts it (by host authority), so an instance can validate a signature signed by an actor on
    /// <em>either</em> of the two instances (its own local actor, or the other instance's actor). Each
    /// per-host fetcher is built signed as that instance's seeded actor, reaching that instance's own
    /// server (so it can resolve any actor's key from that instance's actor documents).
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
    /// Builds an <see cref="IActivityPubClient"/> (signed as <paramref name="handle"/>) routing to the
    /// (deferred) <paramref name="targetServer"/> — the instance's outbound object fetcher (resolves a
    /// remote object's author / owner over the wire).
    /// </summary>
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
