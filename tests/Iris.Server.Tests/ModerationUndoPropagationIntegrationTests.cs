using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.6.3 integration test: cross-instance moderation <em>undo</em> propagation. This is the
/// inverse of <see cref="OutboxPublishServerDeliversIntegrationTests"/> (which pins the outbound half —
/// a <see cref="Block"/>/see cref="Flag"/> federating from A to B and being recorded on B). Here the
/// actor on A <em>reverses</em> a moderation action: it publishes an <see cref="Undo"/> (of a Block, or
/// of a Flag) to its <em>own</em> outbox, and the server delivers the Undo to the affected actor's inbox
/// on B; B's <see cref="Iris.Server.Inbox.UndoActivityHandler"/> resolves the original moderation activity
/// from its activity store and <em>removes</em> the recorded edge.
/// </summary>
/// <remarks>
/// The cross-instance undo has a subtle dependency the single-instance tests do not exercise: B can only
/// remove an edge if it first <em>received and stored</em> the original Block/Flag (the Undo references the
/// original activity by its server-minted IRI, and the receiving handler resolves the parties from the
/// locally-stored original). This test therefore:
/// <list type="number">
/// <item>publishes the original moderation activity from A and waits for B to record the edge (proving the
/// original federated and was stored in B's activity store);</item>
/// <item>publishes the <see cref="Undo"/> from A and waits for B to <em>remove</em> the edge (proving the
/// undo federated and was interpreted against B's stored copy of the original).</item>
/// </list>
/// Both the block and flag variants are pinned, and each also asserts A's local edge was removed (the
/// outbox-publish handler records the local edge and its undo before delivering).
/// </remarks>
/// <para>
/// Topology: instance A (undo-mod-a.domain.local, <c>alice</c>) and instance B (undo-mod-b.domain.local,
/// <c>bob</c>). A's outbound delivery worker routes to B; A's fetcher routes by actor-IRI host (alice →
/// A, bob → B). B's fetcher routes to A (to fetch alice's document and validate the signature of the
/// federated Block/Flag/Undo). The client's writes are signed POSTs to A's own outbox; the cross-instance
/// hop (A → B) is made by A's server, signed as alice.
/// </para>
public sealed class ModerationUndoPropagationIntegrationTests : IDisposable
{
    private const string AHost = "undo-mod-a.domain.local";
    private const string BHost = "undo-mod-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;

    public ModerationUndoPropagationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;

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
            Fetcher = BuildFetcherFor(AHost, Alice, aSeeded.Key, new LazyHandler(() => _a!.CreateHandler())),
        });
        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _aHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- An Undo(Block) published to A's outbox is server-delivered to B, and B removes its edge ----
    //
    // Step 1 (the outbound half, pinned separately by OutboxPublishServerDeliversIntegrationTests): A
    // publishes the Block; A records its local edge; A's server delivers the signed Block to bob's inbox
    // on B; B validates the signature (fetching alice's key from A) and records the alice → bob edge —
    // and, crucially, B's InboxProcessor stores the Block in B's activity store (the copy the Undo will
    // later resolve against).
    //
    // Step 2 (the under-test half): A publishes an Undo referencing the server-minted Block id; A removes
    // its local edge and delivers the signed Undo to bob's inbox on B; B's UndoActivityHandler resolves
    // the original Block from B's activity store, reads the blocker/blocked parties, and removes the
    // edge. The edge is gone on B only if the Undo federated AND B had stored the original.

    [Fact]
    public async Task UndoBlock_ServerDeliversToRemote_RemovesRecordedEdge()
    {
        // Step 1a: publish the Block to A's own outbox (the write surface).
        var block = BuildBlock(_aliceActorIri, _bobActorIri);
        using var blockRequest = SignedRequest(_aliceActorIri, _aliceKey, block, $"/ap/v1/u/{Alice}/outbox");
        using var blockResponse = await _aHttp.SendAsync(blockRequest);
        Assert.Equal(HttpStatusCode.Accepted, blockResponse.StatusCode);

        // A minted the Block's id; learn it from the 2xx body (decision 055).
        var mintedBlockId = await LearnMintedIdAsync(blockResponse);
        Assert.True(
            mintedBlockId is not null,
            "A should have returned the minted Block id in the 2xx body.");

        // A recorded its local edge (the outbox-publish handler records the local edge on publish).
        Assert.True(
            await _aPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            "A should record its own alice → bob block edge on publish.");

        // Step 1b: B recorded the edge — the Block federated and was validated. This also means B stored
        // the Block in its activity store, which Step 2's Undo resolution depends on.
        await WaitForAsync(
            () => _bPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            "B should have recorded the alice → bob block edge after A delivered the signed Block.");

        // Step 2a: publish the Undo (of the Block) to A's own outbox.
        var undo = BuildUndo(_aliceActorIri, mintedBlockId!.Value);
        using var undoRequest = SignedRequest(_aliceActorIri, _aliceKey, undo, $"/ap/v1/u/{Alice}/outbox");
        using var undoResponse = await _aHttp.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // Step 2b: A removed its local edge (the outbox-publish handler's Undo branch).
        await WaitForAsync(
            async () => !await _aPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _aPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            "A should remove its local alice → bob block edge when it publishes the Undo.");

        // Step 2c: B removed its edge — the cross-instance half. B's UndoActivityHandler resolved the
        // original Block from B's activity store and called RemoveBlockAsync(alice, bob).
        await WaitForAsync(
            async () => !await _bPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _bPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            "B should remove the alice → bob block edge after A's server delivered the signed Undo.");

        // The inverse index agrees: bob is no longer listed as blocked-by alice.
        Assert.DoesNotContain(
            _aliceActorIri,
            await _bPersistence.Moderation.GetBlockersAsync(_bobActorIri));
    }

    // --- An Undo(Flag) published to A's outbox is server-delivered to B, and B removes its edge ----
    //
    // The flag variant of the block test: a Flag federates A → B (B records alice → bob and stores the
    // Flag), then the Undo(Flag) federates A → B and B removes the edge. Flag edges are recorded only in
    // the forward direction (no inverse index), so the assertion is the directed HasFlaggedAsync.

    [Fact]
    public async Task UndoFlag_ServerDeliversToRemote_RemovesRecordedEdge()
    {
        // Step 1a: publish the Flag to A's own outbox.
        var flag = BuildFlag(_aliceActorIri, _bobActorIri);
        using var flagRequest = SignedRequest(_aliceActorIri, _aliceKey, flag, $"/ap/v1/u/{Alice}/outbox");
        using var flagResponse = await _aHttp.SendAsync(flagRequest);
        Assert.Equal(HttpStatusCode.Accepted, flagResponse.StatusCode);

        // A minted the Flag's id; learn it from the 2xx body.
        var mintedFlagId = await LearnMintedIdAsync(flagResponse);
        Assert.True(
            mintedFlagId is not null,
            "A should have returned the minted Flag id in the 2xx body.");

        // A recorded its local edge on publish.
        Assert.True(
            await _aPersistence.Moderation.HasFlaggedAsync(_aliceActorIri, _bobActorIri),
            "A should record its own alice → bob flag edge on publish.");

        // Step 1b: B recorded the edge — the Flag federated and was validated (and B stored the Flag in
        // its activity store, which the Undo's resolution depends on).
        await WaitForAsync(
            () => _bPersistence.Moderation.HasFlaggedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Moderation.HasFlaggedAsync(_aliceActorIri, _bobActorIri),
            "B should have recorded the alice → bob flag edge after A delivered the signed Flag.");

        // Step 2a: publish the Undo (of the Flag) to A's own outbox.
        var undo = BuildUndo(_aliceActorIri, mintedFlagId!.Value);
        using var undoRequest = SignedRequest(_aliceActorIri, _aliceKey, undo, $"/ap/v1/u/{Alice}/outbox");
        using var undoResponse = await _aHttp.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // Step 2b: A removed its local edge.
        await WaitForAsync(
            async () => !await _aPersistence.Moderation.HasFlaggedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _aPersistence.Moderation.HasFlaggedAsync(_aliceActorIri, _bobActorIri),
            "A should remove its local alice → bob flag edge when it publishes the Undo.");

        // Step 2c: B removed its edge — the cross-instance half.
        await WaitForAsync(
            async () => !await _bPersistence.Moderation.HasFlaggedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _bPersistence.Moderation.HasFlaggedAsync(_aliceActorIri, _bobActorIri),
            "B should remove the alice → bob flag edge after A's server delivered the signed Undo.");

        // The forward flag collection no longer lists bob.
        Assert.DoesNotContain(
            _bobActorIri,
            await _bPersistence.Moderation.GetFlagsAsync(_aliceActorIri));
    }

    // --- Helpers --------------------------------------------------------------------------

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on the
    /// author's outbox. Uses the client pipeline (via a capture handler) to produce a correctly signed
    /// request, then replays the signed headers onto a fresh request for delivery to A's TestServer.
    /// </summary>
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

    /// <summary>
    /// Builds an id-less <see cref="Block"/> from <paramref name="blockerIri"/> to
    /// <paramref name="blockedIri"/> (id-less: the server mints the activity's id on publish — decision
    /// 055).
    /// </summary>
    private static Block BuildBlock(Iri blockerIri, Iri blockedIri) => new()
    {
        Actor = [new Link { Href = new Uri(blockerIri.Value) }],
        Object = [new Link { Href = new Uri(blockedIri.Value) }],
    };

    /// <summary>
    /// Builds an id-less <see cref="Flag"/> from <paramref name="flaggerIri"/> to
    /// <paramref name="flaggedIri"/> (id-less: the server mints the activity's id on publish — decision
    /// 055).
    /// </summary>
    private static Flag BuildFlag(Iri flaggerIri, Iri flaggedIri) => new()
    {
        Actor = [new Link { Href = new Uri(flaggerIri.Value) }],
        Object = [new Link { Href = new Uri(flaggedIri.Value) }],
    };

    /// <summary>
    /// Builds an id-less <see cref="Undo"/> by <paramref name="actorIri"/> of the original moderation
    /// activity <paramref name="originalActivityId"/> (the server-minted Block/Flag IRI). The receiving
    /// instance resolves the original activity from its own activity store to determine the parties whose
    /// edge to remove (id-less: the server mints the Undo's own id on publish — decision 055).
    /// </summary>
    private static Undo BuildUndo(Iri actorIri, Iri originalActivityId) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(originalActivityId.Value) }],
    };

    /// <summary>
    /// Learns the server-minted id from a 2xx response body (decision 055: the server returns the
    /// created object in the 2xx body). Returns null when the body is empty or carries no id.
    /// </summary>
    private static async Task<Iri?> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var activity = ActivityJson.Deserialize<IObjectOrLink>(body) as Activity;
        var id = activity?.Id;
        return string.IsNullOrWhiteSpace(id) ? null : new Iri(id);
    }

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

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents based
    /// on the actor IRI's host (A's fetcher needs to reach A and B).
    /// </summary>
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

    /// <summary>
    /// Captures a signed request (its body + headers) instead of forwarding it, so the signed body can be
    /// replayed through a plain <see cref="HttpClient"/>.
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
}
