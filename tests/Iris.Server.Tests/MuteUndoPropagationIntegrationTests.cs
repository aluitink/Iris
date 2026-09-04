using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.Inbox;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 24.2 integration test: cross-instance <em>mute</em> / un-mute propagation. <c>Mute</c> is an
/// Iris-specific activity (there is no ActivityStreams <c>Mute</c> type): a local actor on A mutes a
/// remote actor on B, A records the <c>muter → muted</c> edge and publishes the <see cref="MuteActivity"/>
/// to its own outbox, A's server delivers it (signed as the muter) to the muted actor's inbox on B, and B's
/// <see cref="MuteActivityHandler"/> records the edge on the muted actor's home instance (so B knows its
/// actor was muted). The inverse — an <see cref="Undo"/> of the <see cref="MuteActivity"/> — removes the
/// edge on both sides: A's outbox-publish Undo branch removes A's edge, and B's
/// <see cref="Iris.Server.Inbox.UndoActivityHandler"/> resolves the stored <see cref="MuteActivity"/> and
/// removes B's edge.
/// </summary>
/// <remarks>
/// The cross-instance half has the same subtle dependency as the block/flag undo test: B can only remove
/// an edge if it first <em>received and stored</em> the original Mute (the Undo references the original
/// activity by its server-minted IRI, and B's handler resolves the parties from its locally-stored copy).
/// This test therefore:
/// <list type="number">
/// <item>publishes the Mute from A and waits for B to record the edge (proving the Mute federated and was
/// stored in B's activity store);</item>
/// <item>publishes the <see cref="Undo"/> from A and waits for B to <em>remove</em> the edge (proving the
/// undo federated and was interpreted against B's stored copy of the original).</item>
/// </list>
/// Both sides' local edges are asserted too (the outbox-publish handler records the local edge on publish
/// and removes it on undo before delivering).
/// </remarks>
/// <para>
/// Topology: instance A (mute-a.domain.local, <c>alice</c>, the muter) and instance B (mute-b.domain.local,
/// <c>bob</c>, the muted actor). A's outbound delivery worker routes to B; A's fetcher routes by actor-IRI
/// host (alice → A, bob → B). B's fetcher routes to A (to fetch alice's document and validate the signature
/// of the federated Mute/Undo). The client's writes are signed POSTs to A's own outbox; the cross-instance
/// hop (A → B) is made by A's server, signed as alice.
/// </para>
public sealed class MuteUndoPropagationIntegrationTests : IDisposable
{
    private const string AHost = "mute-a.domain.local";
    private const string BHost = "mute-b.domain.local";
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

    public MuteUndoPropagationIntegrationTests()
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

    // --- A Mute published to A's outbox is server-delivered to B, and B records its edge; an Undo(Mute)
    // --- then removes the edge on both instances.
    //
    // Step 1 (the outbound half): A publishes the Mute; A records its local alice → bob mute edge; A's
    // server delivers the signed Mute to bob's inbox on B; B validates the signature (fetching alice's key
    // from A) and B's MuteActivityHandler records the alice → bob edge — and, crucially, B's InboxProcessor
    // stores the MuteActivity in B's activity store (the copy the Undo will later resolve against).
    //
    // Step 2 (the under-test half): A publishes an Undo referencing the server-minted Mute id; A removes
    // its local edge and delivers the signed Undo to bob's inbox on B; B's UndoActivityHandler resolves the
    // original MuteActivity from B's activity store, reads the muter/muted parties, and removes the edge.
    // The edge is gone on B only if the Undo federated AND B had stored the original.

    [Fact]
    public async Task Mute_AndUndo_ServerDeliverToRemote_RecordAndRemoveEdge()
    {
        // Step 1a: publish the Mute to A's own outbox (the write surface).
        var mute = BuildMute(_aliceActorIri, _bobActorIri);
        using var muteRequest = SignedRequest(_aliceActorIri, _aliceKey, mute, $"/ap/v1/u/{Alice}/outbox");
        using var muteResponse = await _aHttp.SendAsync(muteRequest);
        Assert.Equal(HttpStatusCode.Accepted, muteResponse.StatusCode);

        // A minted the Mute's id; learn it from the 2xx body (decision 055).
        var mintedMuteId = await LearnMintedIdAsync(muteResponse);
        Assert.True(
            mintedMuteId is not null,
            "A should have returned the minted Mute id in the 2xx body.");

        // A recorded its local edge (the outbox-publish mute arm records the muter → muted edge on publish).
        Assert.True(
            await _aPersistence.Moderation.IsMutedAsync(_aliceActorIri, _bobActorIri),
            "A should record its own alice → bob mute edge on publish.");

        // Step 1b: B recorded the edge — the Mute federated and was validated (and B stored the MuteActivity
        // in its activity store, which Step 2's Undo resolution depends on).
        await WaitForAsync(
            () => _bPersistence.Moderation.IsMutedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Moderation.IsMutedAsync(_aliceActorIri, _bobActorIri),
            "B should have recorded the alice → bob mute edge after A delivered the signed Mute.");

        // Step 2a: publish the Undo (of the Mute) to A's own outbox.
        var undo = BuildUndo(_aliceActorIri, mintedMuteId!.Value);
        using var undoRequest = SignedRequest(_aliceActorIri, _aliceKey, undo, $"/ap/v1/u/{Alice}/outbox");
        using var undoResponse = await _aHttp.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // Step 2b: A removed its local edge (the outbox-publish handler's Undo(Mute) branch).
        await WaitForAsync(
            async () => !await _aPersistence.Moderation.IsMutedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _aPersistence.Moderation.IsMutedAsync(_aliceActorIri, _bobActorIri),
            "A should remove its local alice → bob mute edge when it publishes the Undo.");

        // Step 2c: B removed its edge — the cross-instance half. B's UndoActivityHandler resolved the
        // original MuteActivity from B's activity store and called RemoveMuteAsync(alice, bob).
        await WaitForAsync(
            async () => !await _bPersistence.Moderation.IsMutedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _bPersistence.Moderation.IsMutedAsync(_aliceActorIri, _bobActorIri),
            "B should remove the alice → bob mute edge after A's server delivered the signed Undo.");

        // The forward mutes collection agrees: bob is no longer listed as muted-by alice on either side.
        Assert.DoesNotContain(
            _bobActorIri,
            await _aPersistence.Moderation.GetMutesAsync(_aliceActorIri));
        Assert.DoesNotContain(
            _bobActorIri,
            await _bPersistence.Moderation.GetMutesAsync(_aliceActorIri));
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
    /// Builds an id-less <see cref="MuteActivity"/> from <paramref name="muterIri"/> to
    /// <paramref name="mutedIri"/> (id-less: the server mints the activity's id on publish — decision
    /// 055). The <c>actor</c> is the muter and the <c>object</c> is the muted actor.
    /// </summary>
    private static MuteActivity BuildMute(Iri muterIri, Iri mutedIri) => new()
    {
        Actor = [new Link { Href = new Uri(muterIri.Value) }],
        Object = [new Link { Href = new Uri(mutedIri.Value) }],
    };

    /// <summary>
    /// Builds an id-less <see cref="Undo"/> by <paramref name="actorIri"/> of the original
    /// <paramref name="originalActivityId"/> (the server-minted Mute IRI). The receiving instance resolves
    /// the original activity from its own activity store to determine the parties whose edge to remove
    /// (id-less: the server mints the Undo's own id on publish — decision 055).
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
