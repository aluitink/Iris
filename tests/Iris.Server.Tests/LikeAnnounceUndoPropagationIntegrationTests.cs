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
/// Phase 24.1 integration test: cross-instance <em>like / announce</em> undo propagation. This is the
/// inverse of the outbound half (a local actor on A likes / announces a remote actor's object on B). Here
/// the actor on A <em>reverses</em> the like / announce: it publishes an <see cref="Undo"/> (of a Like, or
/// of an Announce) to its <em>own</em> outbox, and the server delivers the Undo to the object's author's
/// inbox on B; B's <see cref="Iris.Server.Inbox.UndoActivityHandler"/> resolves the original activity from
/// its activity store and <em>removes</em> the recorded edge.
/// </summary>
/// <remarks>
/// <para>
/// The cross-instance like/announce undo has a subtle dependency the single-instance tests do not
/// exercise: B can only remove an edge if it first <em>received and stored</em> the original Like/Announce
/// (the Undo references the original activity by its server-minted IRI, and the receiving handler resolves
/// the parties from the locally-stored original). This test therefore:
/// </para>
/// <list type="number">
/// <item>publishes the original Like/Announce from A and waits for B to record the edge (proving the
/// original federated and was stored in B's activity store);</item>
/// <item>publishes the <see cref="Undo"/> from A and waits for B to <em>remove</em> the edge (proving the
/// undo federated and was interpreted against B's stored copy of the original).</item>
/// </list>
/// <para>
/// The under-test invariant is the <em>delivery target</em> for the activity and its undo. A Like/Announce
/// (or an Undo of one) of a <em>remote</em> object is delivered to the object's <em>author</em> (its
/// <c>attributedTo</c>), resolved by A fetching the object's document over the wire (24.1). Without that
/// resolution the delivery would fall back to the object IRI, whose <c>/inbox</c> does not exist — the
/// remote instance would never receive the activity or its undo, so its edge would never be recorded or
/// removed. The test wires A's outbound object fetcher to B (so A resolves bob as the Note's author) and
/// asserts the edge is recorded on B (step 1) and removed on B (step 2).
/// </para>
/// <para>
/// Topology: instance A (like-undo-a.domain.local, <c>alice</c>) and instance B (like-undo-b.domain.local,
/// <c>bob</c>). B stores bob's Note in its object store (attributedTo bob). A's outbound object fetcher
/// routes to B (so A fetches bob's Note and resolves the author); A's delivery worker routes to B; A's
/// fetcher routes by actor-IRI host (alice → A, bob → B); B's fetcher routes to A (to fetch alice's
/// document and validate the signature of the federated Like/Announce/Undo). The client's writes are
/// signed POSTs to A's own outbox; the cross-instance hop (A → B) is made by A's server, signed as alice.
/// </para>
/// </remarks>
public sealed class LikeAnnounceUndoPropagationIntegrationTests : IDisposable
{
    private const string AHost = "like-undo-a.domain.local";
    private const string BHost = "like-undo-b.domain.local";
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
    private readonly Iri _bobNoteIri;

    public LikeAnnounceUndoPropagationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;

        // bob's Note, stored in B's object store with attributedTo bob, served at its IRI so A can fetch
        // it over the wire and resolve the author (the 24.1 delivery-target resolution).
        _bobNoteIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}/notes/n1");
        _bPersistence.Objects.PutObjectAsync(new Note
        {
            Id = _bobNoteIri.Value,
            Content = ["a note from bob"],
            AttributedTo = [new Link { Href = new Uri(_bobActorIri.Value) }],
        }).GetAwaiter().GetResult();

        _a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentity(aSeeded.Key, aSeeded.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => _b!.CreateHandler()),
            // 24.1: A's outbound object fetcher routes to B so A resolves the Note's author (bob).
            Client = BuildClientTo(aSeeded.ActorIri, aSeeded.Key, new LazyHandler(() => _b!.CreateHandler())),
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => _a!.CreateHandler()),
                BHost, new LazyHandler(() => _b!.CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
        });
        // bob follows alice. In the real federation flow the Follow federates B → A and A records bob in
        // alice's followers (so A can deliver to bob later); here we record that follow edge directly in
        // A's persistence (alice's followers include bob) so A's GetRemoteNonBlockedFollowersAsync finds
        // bob and delivers the Announce to bob's inbox on B. Seeded in the constructor (not the test
        // method, per xUnit 1031); it only matters for the Announce variant (the Like variant ignores it).
        _aPersistence.Follows.RecordFollowAsync(_bobActorIri, _aliceActorIri).GetAwaiter().GetResult();

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

    // --- An Undo(Like) published to A's outbox federates to B (the object's author's instance) ----
    //
    // A like edge is recorded on the <em>liker's home instance</em>: A (alice's home) records the
    // alice → note edge on publish; B does <em>not</em> record a like edge (the liker is remote to B —
    // LikeActivityHandler records only when the liker is local). The cross-instance invariant for a Like
    // is therefore <em>activity delivery</em> (not edge removal on B): A must deliver the Like — and its
    // Undo — to the object's author's inbox (bob's inbox on B), resolved by A fetching bob's Note over the
    // wire (24.1). Without that resolution the delivery would fall back to the Note IRI, whose /inbox does
    // not exist, and B would never receive the activity. B's InboxProcessor stores every inbound activity
    // (the copy the Undo's resolution depends on), so the under-test assertion is that B stored the Like
    // (step 1) and B stored the Undo (step 2), while A records then removes its own like edge.

    [Fact]
    public async Task UndoLike_ServerDeliversToRemote_StoredOnRemoteInstance()
    {
        // Step 1a: publish the Like (of bob's Note) to A's own outbox.
        var like = BuildLike(_aliceActorIri, _bobNoteIri);
        using var likeRequest = SignedRequest(_aliceActorIri, _aliceKey, like, $"/ap/v1/u/{Alice}/outbox");
        using var likeResponse = await _aHttp.SendAsync(likeRequest);
        Assert.Equal(HttpStatusCode.Accepted, likeResponse.StatusCode);

        // A minted the Like's id; learn it from the 2xx body (decision 055).
        var mintedLikeId = await LearnMintedIdAsync(likeResponse);
        Assert.True(
            mintedLikeId is not null,
            "A should have returned the minted Like id in the 2xx body.");

        // A recorded its local edge (the outbox-publish handler records the local edge on publish).
        Assert.True(
            await _aPersistence.Likes.HasLikedAsync(_aliceActorIri, _bobNoteIri),
            "A should record its own alice → note like edge on publish.");

        // Step 1b: B stored the Like activity — the Like federated (A resolved bob as the author and
        // delivered to bob's inbox) and was validated. B does not record a like edge (the liker is remote
        // to B); the under-test invariant is that the activity reached B (the 24.1 delivery-target fix).
        await WaitForAsync(
            () => _bPersistence.Activities.TryGetActivityAsync(mintedLikeId!.Value, out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedLikeId!.Value, out _),
            "B should have stored the Like activity after A delivered the signed Like to bob's inbox.");

        // Step 2a: publish the Undo (of the Like) to A's own outbox.
        var undo = BuildUndo(_aliceActorIri, mintedLikeId!.Value);
        using var undoRequest = SignedRequest(_aliceActorIri, _aliceKey, undo, $"/ap/v1/u/{Alice}/outbox");
        using var undoResponse = await _aHttp.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // Step 2b: A removed its local edge (the outbox-publish handler's Undo branch) — the like edge is
        // home-instance-local, so the reversible half lives on A.
        await WaitForAsync(
            async () => !await _aPersistence.Likes.HasLikedAsync(_aliceActorIri, _bobNoteIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _aPersistence.Likes.HasLikedAsync(_aliceActorIri, _bobNoteIri),
            "A should remove its local alice → note like edge when it publishes the Undo.");

        // Step 2c: B stored the Undo activity — the cross-instance half. A resolved bob as the author and
        // delivered the signed Undo to bob's inbox on B; B's InboxProcessor stored it (B's UndoActivityHandler
        // resolved the original Like from B's activity store — a no-op on B's edge, since B recorded none,
        // but the delivery + storage is the invariant).
        var mintedUndoId = await LearnMintedIdAsync(undoResponse);
        Assert.True(mintedUndoId is not null, "A should have returned the minted Undo id in the 2xx body.");
        await WaitForAsync(
            () => _bPersistence.Activities.TryGetActivityAsync(mintedUndoId!.Value, out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedUndoId!.Value, out _),
            "B should have stored the Undo activity after A's server delivered the signed Undo to bob's inbox.");
    }

    // --- An Undo(Announce) published to A's outbox is server-delivered to B, and B removes its edge ----
    //
    // The announce variant of the like test. Unlike a Like, an outbound Announce federates to the
    // announcer's <em>remote followers</em> (not the object's author), so bob must follow alice for the
    // Announce to reach B. The Undo(Announce), however, is delivered to the object's <em>author</em> (the
    // 24.1 resolution), so it reaches B regardless of the follow. B records the announce edge on inbound
    // (bob is the local recipient of the boost), then the UndoActivityHandler removes it.

    [Fact]
    public async Task UndoAnnounce_ServerDeliversToRemote_RemovesRecordedEdge()
    {
        // bob follows alice (seeded in the constructor, B records the bob → alice follow edge locally), so
        // the outbound Announce federates to bob (a remote follower of alice).

        // Step 1a: publish the Announce (of bob's Note) to A's own outbox.
        var announce = BuildAnnounce(_aliceActorIri, _bobNoteIri);
        using var announceRequest = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var announceResponse = await _aHttp.SendAsync(announceRequest);
        Assert.Equal(HttpStatusCode.Accepted, announceResponse.StatusCode);

        // A minted the Announce's id; learn it from the 2xx body.
        var mintedAnnounceId = await LearnMintedIdAsync(announceResponse);
        Assert.True(
            mintedAnnounceId is not null,
            "A should have returned the minted Announce id in the 2xx body.");

        // A recorded its local edge on publish.
        Assert.True(
            await _aPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, _bobNoteIri),
            "A should record its own alice → note announce edge on publish.");

        // Step 1b: B recorded the edge — the Announce federated to bob (a follower) and was validated (and
        // B stored the Announce in its activity store, which the Undo's resolution depends on).
        await WaitForAsync(
            () => _bPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, _bobNoteIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, _bobNoteIri),
            "B should have recorded the alice → note announce edge after A delivered the signed Announce.");

        // Step 2a: publish the Undo (of the Announce) to A's own outbox.
        var undo = BuildUndo(_aliceActorIri, mintedAnnounceId!.Value);
        using var undoRequest = SignedRequest(_aliceActorIri, _aliceKey, undo, $"/ap/v1/u/{Alice}/outbox");
        using var undoResponse = await _aHttp.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // Step 2b: A removed its local edge.
        await WaitForAsync(
            async () => !await _aPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, _bobNoteIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _aPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, _bobNoteIri),
            "A should remove its local alice → note announce edge when it publishes the Undo.");

        // Step 2c: B removed its edge — the cross-instance half. The Undo(Announce) was delivered to the
        // object's author (bob, the 24.1 resolution); B's UndoActivityHandler resolved the original
        // Announce from B's activity store and called RemoveAnnounceAsync(alice, note).
        await WaitForAsync(
            async () => !await _bPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, _bobNoteIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.False(
            await _bPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, _bobNoteIri),
            "B should remove the alice → note announce edge after A's server delivered the signed Undo.");

        // The inverse index agrees: the note no longer lists alice as an announcer.
        Assert.DoesNotContain(
            _aliceActorIri,
            await _bPersistence.Announces.GetAnnouncersAsync(_bobNoteIri));
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
    /// Builds an <see cref="IActivityPubClient"/> signed as <paramref name="actorIri"/> whose transport
    /// is <paramref name="handler"/> (so the client's <c>GetObjectAsync</c> fetches from the routed
    /// instance). This is the 24.1 outbound object fetcher: A uses it to fetch bob's Note and resolve the
    /// author for the Like/Announce delivery target.
    /// </summary>
    private static IActivityPubClient BuildClientTo(Iri actorIri, KeyPair key, HttpMessageHandler handler)
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
    /// Builds an id-less <see cref="Like"/> by <paramref name="likerIri"/> of <paramref name="objectIri"/>
    /// (id-less: the server mints the activity's id on publish — decision 055).
    /// </summary>
    private static Like BuildLike(Iri likerIri, Iri objectIri) => new()
    {
        Actor = [new Link { Href = new Uri(likerIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    /// <summary>
    /// Builds an id-less <see cref="Announce"/> by <paramref name="announcerIri"/> of
    /// <paramref name="objectIri"/> (id-less: the server mints the activity's id on publish — decision
    /// 055).
    /// </summary>
    private static Announce BuildAnnounce(Iri announcerIri, Iri objectIri) => new()
    {
        Actor = [new Link { Href = new Uri(announcerIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    /// <summary>
    /// Builds an id-less <see cref="Undo"/> by <paramref name="actorIri"/> of the original activity
    /// <paramref name="originalActivityId"/> (the server-minted Like/Announce IRI). The receiving instance
    /// resolves the original activity from its own activity store to determine the parties whose edge to
    /// remove (id-less: the server mints the Undo's own id on publish — decision 055).
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
