using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 136.7 end-to-end test (replies, threading, and context integrity across instances): an Iris
/// user (alice on instance B) replies to a post on a <em>remote</em> instance (bob's note m1 on
/// instance A — the Lemmy stand-in). The reply's <c>inReplyTo</c> points at the remote parent m1, which
/// B has <em>never stored</em> (it was only ever seen by IRI). The cross-instance reply chain must stay
/// coherent on the <strong>parent's home instance</strong> (A): the reply must actually reach A, where
/// m1 is local, and be threaded under m1. Three invariants are pinned:
/// <list type="number">
/// <item><em>The reply federates to the parent's home:</em> B's outbox publish resolves the remote
/// parent's author (by fetching m1 over the wire) and delivers the reply to it, so A stores the reply
/// (gap (a) — before 136.7 the reply's parent author was only resolved from the local object store, so a
/// remote parent's author was never resolved and the reply was never delivered to the parent's home).</item>
/// <item><em>The parent's home serves the coherent thread:</em> <c>GET {m1}/replies</c> on A lists the
/// reply (m1 is local on A, the reply edge is recorded on A) — the Lemmy author's thread includes the
/// Iris reply (gap (b)).</item>
/// <item><em>Thread-root + inReplyTo stability:</em> on A the stored reply's <c>inReplyTo</c> is m1's
/// IRI intact, and its <c>conversationId</c> is anchored to m1 (the thread root) — parent-child
/// reconstruction survives the cross-origin hop (gap (c)).</item>
/// </list>
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, actor <c>bob</c> — posts the parent note m1, the parent's home)
/// and instance B (b.domain.local, actor <c>alice</c> — replies to m1). Bidirectional federation wiring:
/// <list type="bullet">
/// <item>B's outbound object fetcher (<see cref="ActivityPubHostOptions.Client"/>) routes to A, so B's
/// outbox publish can fetch the remote parent m1 and resolve its author (bob).</item>
/// <item>B's delivery transport routes to A, so the reply is delivered to bob (the parent author) by B's
/// hosted <see cref="DeliveryWorker"/>.</item>
/// <item>A's inbound fetcher routes to B, so A can validate the reply's signature (resolving alice's key
/// from B's actor doc).</item>
/// </list>
/// The reply is authored by a signed <c>POST /ap/v1/u/alice/outbox</c> to B (the local-outbox publish
/// path, where the 136.7 audience-rewrite + parent-author delivery live). The assertions read A's
/// persistence (the stored reply's <c>inReplyTo</c> + <c>conversationId</c> + the reply edge) and A's
/// <c>GET {m1}/replies</c> endpoint (the coherent thread on the parent's home).
/// </remarks>
public sealed class CrossInstanceReplyThreadIntegrationTests : IDisposable
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
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;

    public CrossInstanceReplyThreadIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts bob (the parent note's author, the parent's home). B hosts alice (the replier).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Bob);
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Alice);
        _aliceKey = bSeeded.Key;
        _aliceActorIri = bSeeded.ActorIri;

        var aServerHolder = new TestServerHolder();
        var bServerHolder = new TestServerHolder();

        // A: its inbound fetcher reaches B (validates the reply signed by alice). A's object fetcher
        // reaches B (for the symmetric direction, unused here).
        _a = aServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Bob,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentityKeys(aSeeded.Key, aSeeded.ActorIri),
            Fetcher = BuildFetcherFor(AHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
            Client = BuildClient(BHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
        });

        // B: its object fetcher reaches A (resolves the remote parent m1's author), its delivery transport
        // reaches A (delivers the reply to bob via B's hosted DeliveryWorker), its inbound fetcher is a
        // self-fetcher (validates the reply signed by alice, B's local actor).
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

    // --- The cross-instance reply reaches the parent's home and is threaded there -------------

    [Fact]
    public async Task ReplyToRemoteParent_FederatesToParentHome_AndIsThreadedThere()
    {
        // bob (A) posts the parent note m1 on A (the parent's home — the Lemmy stand-in's post). The IRI
        // must be in A's serving namespace (the /ap/v1 route prefix) so A's object-document endpoint can
        // serve it when B fetches it over the wire (B's outbox publish resolves the remote parent's author
        // by fetching m1).
        var parentIri = new Iri($"https://{AHost}/ap/v1/objects/m1-{Guid.NewGuid():N}");
        await _aPersistence.Objects.PutObjectAsync(new Note
        {
            Id = parentIri.Value,
            Content = ["a post on the remote instance"],
            AttributedTo = [new Link { Href = new Uri($"https://{AHost}/ap/v1/u/{Bob}") }],
        });

        // alice (B) authors a reply to m1 via a signed POST to B's outbox (the local-outbox publish path,
        // where the 136.7 audience-rewrite + parent-author delivery live). B resolves m1's author (bob) by
        // fetching m1 over the wire, adds bob to the reply's `to`, and delivers the reply to bob (A) —
        // the parent's home.
        var replyIri = new Iri($"https://{BHost}/ap/v1/objects/reply-{Guid.NewGuid():N}");
        var create = BuildReplyCreate(_aliceActorIri, parentIri, replyIri);

        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // Wait on the EFFECT of the federation (A storing the reply), not on B's storage: the reply must
        // have crossed to A (the parent's home) and been stored there (A validated alice's signature via
        // its fetcher → B).
        await WaitForAsync(async () =>
            await _aPersistence.Objects.TryGetObjectAsync(replyIri, out _),
            timeout: TimeSpan.FromSeconds(30));

        // (a) A validated the reply (resolving alice's key from B's actor doc) and stored the embedded
        // reply note — the reply reached the parent's home (it was not stranded on B alone).
        Assert.True(
            await _aPersistence.Objects.TryGetObjectAsync(replyIri, out var stored),
            "A (the parent's home) should have stored the reply federated by B's outbox publish");
        Assert.NotNull(stored);
        var reply = (IObject)stored!;

        // (c, inReplyTo stability) the reply's inReplyTo — the parent's IRI — survived the wire intact.
        Assert.Equal(parentIri.Value, reply.GetParentIri()?.Value);

        // (c, thread-root anchoring) the reply's conversationId is anchored to m1 (the thread root). On A
        // m1 is local, so A's EnsureConversationIdAsync reads m1's conversationId (or m1's IRI).
        Assert.Equal(parentIri.Value, reply.GetConversationId()?.Value);

        // (b, reply edge on the parent's home) A recorded the parent → child reply edge (parent = m1,
        // child = the reply) — the backing data for m1's replies collection.
        var replies = await _aPersistence.Replies.GetRepliesAsync(parentIri);
        Assert.Contains(replies, r => r.Value == replyIri.Value);

        // (b, coherent thread on the parent's home) GET {m1}/replies on A lists the reply — the Lemmy
        // author's thread includes the Iris reply (the cross-instance reply chain is coherent on A).
        var response = await _aHttp.GetAsync($"{parentIri.Value}/replies");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).Select(JsonDoc.ItemId).ToArray();
        Assert.Equal([replyIri.Value], items);
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal(parentIri.RepliesOf().Value, doc.RootElement.GetProperty("id").GetString());

        // S62: a remote instance (Mastodon/Lemmy) reads the embedded NOTE's to/cc to decide delivery +
        // notification, not the Create activity's audience. B's outbox publish must therefore name the
        // parent's author (bob, resolved by fetching m1 over the wire) in the reply NOTE's to AND cc —
        // otherwise a cross-instance reply neither reaches nor notifies the parent author on the parent's
        // home instance. (Before S62 the note carried only to:#Public and no cc, so the parent author was
        // absent from the note's audience even though the Create activity's `to` named it.)
        var replyNote = Assert.IsType<Note>(stored);
        var bobIri = $"https://{AHost}/ap/v1/u/{Bob}";
        bool NamedIn(IEnumerable<IObjectOrLink>? audience)
            => audience is not null &&
                audience.Any(a => a is { } item &&
                    string.Equals(item.ResolveObjectIri()?.Value, bobIri, StringComparison.OrdinalIgnoreCase));
        Assert.True(NamedIn(replyNote.To), "the reply NOTE's `to` must include the parent's author (bob)");
        Assert.True(NamedIn(replyNote.Cc), "the reply NOTE's `cc` must include the parent's author (bob)");
    }

    // --- Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on the
    /// author's outbox (on <see cref="BHost"/>). Uses the client pipeline (via a
    /// <see cref="CaptureHandler"/>) to produce a correctly signed request, then replays the signed headers
    /// onto a fresh request for delivery to B's <see cref="TestServer"/>.
    /// </summary>
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
    /// Builds an <see cref="IActorDocumentFetcher"/> that reaches the instance's own server (a
    /// self-fetcher), so the instance can validate an inbound <see cref="Create"/> signed by one of its
    /// local actors (resolving the actor's key from its own actor doc).
    /// </summary>
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

    /// <summary>
    /// Builds a reply <see cref="Create"/>: the actor (alice) posts a note whose <c>inReplyTo</c> is the
    /// (remote) parent IRI.
    /// </summary>
    private static Create BuildReplyCreate(Iri actorIri, Iri parentIri, Iri replyIri) => new()
    {
        Id = $"https://{BHost}/activities/reply-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = replyIri.Value,
                Content = ["a reply to the remote parent"],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
                InReplyTo = [new Link { Href = new Uri(parentIri.Value) }],
            },
        ],
    };

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
