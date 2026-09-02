using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 integration test for the <strong>outbound community-follow</strong> slice (gap G-3): the
/// community outbox write surface (<c>POST /ap/v1/c/{name}/outbox</c>). A local community (a
/// <see cref="Group"/> actor) publishes a <see cref="Follow"/> (or an <see cref="Undo"/> of a follow) to
/// its own outbox; the server records the community's follows-set edge + the activity + the community's
/// outbox, and is the only thing that server-delivers the activity to the target's inbox (signed as the
/// community). This is the write surface the delivery model requires for a community (mirroring the actor
/// outbox publish endpoint) — previously the only outbound follow construction was a local <em>person</em>'s
/// outbox publish, and a community could not initiate a follow of a remote actor/community at all.
/// </summary>
/// <remarks>
/// <strong>Single-instance tests</strong> exercise the endpoint's contract: a signed follow is recorded in
/// the community's follows set + activity store + outbox (202); an unsigned request is 401; an unknown
/// community is 404; an activity whose actor is not this community is 403; a non-follow/undo is 400; and
/// an <c>Undo</c> of a previously-recorded follow removes the follows-set edge.
/// </remarks>
/// <remarks>
/// <strong>Two-instance federation loop</strong> proves the outbound follow end to end: B's community
/// <c>iris</c> publishes a follow of A's community <c>lumen</c> to its own outbox; B's server records the
/// edge and server-delivers the <c>Follow</c> to A's community inbox (signed as <c>iris</c>); A validates
/// (fetching B's community document for the Group's key), records the edge, and queues an
/// <see cref="Accept"/> back to <c>iris</c>'s inbox; B's <see cref="AcceptActivityHandler"/> finalizes
/// <c>iris</c>'s own edge (the follower side — the gap this slice's AcceptActivityHandler extension
/// closes). Both sides' <c>following</c> collections then carry the edge. An <c>Undo</c> published to
/// <c>iris</c>'s outbox removes B's edge and server-delivers the <c>Undo</c> to A, which removes A's edge.
/// </remarks>
public sealed class CommunityOutboxPublishIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string RemoteCommunity = "lumen";
    private const string LocalCommunity = "iris";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _aCommunityKey;
    private readonly KeyPair _bCommunityKey;
    private readonly KeyPair _bobKey;
    private readonly Iri _bobActorIri;
    private readonly Iri _localCommunityIri;
    private readonly Iri _remoteCommunityIri;
    private readonly Iri _remoteCommunityInboxIri;

    public CommunityOutboxPublishIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts alice (a public actor) + the community lumen (a Group with a real key, so it can sign
        // back the Accept). B hosts bob + the community iris (a Group with a real key, so it can sign the
        // outbound follow published to its outbox).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aCommunityKey = TestSeeder.SeedCommunityWithKey(_aPersistence, AHost, RemoteCommunity).Key;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        _bobActorIri = bSeeded.ActorIri;
        _bCommunityKey = TestSeeder.SeedCommunityWithKey(_bPersistence, BHost, LocalCommunity, _bobActorIri).Key;

        _localCommunityIri = new Iri($"https://{BHost}/ap/v1/c/{LocalCommunity}");
        _remoteCommunityIri = new Iri($"https://{AHost}/ap/v1/c/{RemoteCommunity}");
        _remoteCommunityInboxIri = _remoteCommunityIri.InboxOf();

        // A's fetcher is wired to B (lazy, to break the A<->B chicken-and-egg): A validates B's
        // community-as-follower's key by fetching B's community document. B's delivery transport routes
        // to A (B's server-delivery of the community follow). B's fetcher is wired to B ITSELF: to
        // validate a follow published to its own community outbox, B must resolve the signing key
        // (iris#key-1) by fetching its OWN community document (a Group carries a publicKey).
        _a = StartServer(AHost, Alice, _aPersistence, _aliceKey, _aCommunityKey,
            fetcher: BuildFetcherFor(AHost, Alice, _aliceKey, new LazyHandler(() => _b!.CreateHandler())));
        _b = StartServer(BHost, Bob, _bPersistence, _bobKey, _bCommunityKey,
            deliveryTransport: () => _a.CreateHandler(),
            fetcher: BuildFetcherFor(BHost, Bob, _bobKey, new LazyHandler(() => _b!.CreateHandler())));
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

    // --- Single-instance: the endpoint's contract --------------------------------

    [Fact]
    public async Task CommunityOutbox_SignedFollow_RecordsEdgeActivityAndOutbox()
    {
        var follow = BuildFollow(_localCommunityIri, _remoteCommunityIri);
        using var request = SignedRequest(_localCommunityIri, _bCommunityKey, follow, $"/ap/v1/c/{LocalCommunity}/outbox");

        using var response = await _bHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Decision 055: the server minted the follow's id; learn it from the 2xx body.
        var mintedIdNullable = await LearnMintedIdAsync(response);
        Assert.True(mintedIdNullable != null, "B should have returned the minted follow id in the 2xx body.");
        Iri mintedId = mintedIdNullable.Value;

        // The community's follows-set edge is recorded (the community `following` collection lists the
        // target) ...
        Assert.Contains(_remoteCommunityIri, await _bPersistence.Communities.GetFollowsAsync(_localCommunityIri));

        // ... and the activity + the community's outbox are recorded (under the MINTED id).
        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            "B should have stored the community's follow in the activity store under its minted id");
        var outbox = await _bPersistence.Activities.GetOutboxAsync(_localCommunityIri);
        Assert.Single(outbox);
        Assert.IsType<Follow>(outbox[0]);
    }

    [Fact]
    public async Task CommunityOutbox_UnknownCommunity_Returns404()
    {
        var follow = BuildFollow(_localCommunityIri, _remoteCommunityIri);
        using var request = SignedRequest(_localCommunityIri, _bCommunityKey, follow, "/ap/v1/c/nobody/outbox");

        var response = await _bHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CommunityOutbox_ActorIsNotThisCommunity_Returns403()
    {
        // The follow's actor is bob (a person, not the community iris); iris's outbox rejects it.
        var follow = BuildFollow(_bobActorIri, _remoteCommunityIri);
        using var request = SignedRequest(_bobActorIri, _bobKey, follow, $"/ap/v1/c/{LocalCommunity}/outbox");

        var response = await _bHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CommunityOutbox_NonFollowOrUndo_Returns400()
    {
        // A Like (not a Follow/Undo) is not a community outbox activity.
        var like = new Like
        {
            Id = $"https://{BHost}/activities/like-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(_localCommunityIri.Value) }],
            Object = [new Link { Href = new Uri(_remoteCommunityIri.Value) }],
        };
        using var request = SignedRequest(_localCommunityIri, _bCommunityKey, like, $"/ap/v1/c/{LocalCommunity}/outbox");

        var response = await _bHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CommunityOutbox_InvalidSignature_Returns401()
    {
        // An unsigned follow to the community outbox: no Signature header -> 401 (the middleware gate).
        var follow = BuildFollow(_localCommunityIri, _remoteCommunityIri);
        var json = ActivityJson.Serialize(follow);
        using var http = new HttpClient(_b.CreateHandler());
        using var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/activity+json");
        var response = await http.PostAsync(
            $"https://{BHost}/ap/v1/c/{LocalCommunity}/outbox", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CommunityOutbox_SignedUndoRemovesTheFollowEdge()
    {
        // Record a follow first (publish it to the outbox), then undo it.
        var follow = BuildFollow(_localCommunityIri, _remoteCommunityIri);
        Iri mintedFollowId = default!;
        using (var followRequest = SignedRequest(_localCommunityIri, _bCommunityKey, follow, $"/ap/v1/c/{LocalCommunity}/outbox"))
        {
            using var followResponse = await _bHttp.SendAsync(followRequest);
            Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);
            // Decision 055: learn the follow's minted id from the 2xx body.
            var followMintedNullable = await LearnMintedIdAsync(followResponse);
            Assert.True(followMintedNullable != null, "the server should have returned the minted follow id in the 2xx body.");
            mintedFollowId = followMintedNullable.Value;
        }

        Assert.Contains(_remoteCommunityIri, await _bPersistence.Communities.GetFollowsAsync(_localCommunityIri));

        // The Undo references the follow by its LEARNED (minted) IRI; its actor is the community (the
        // un-follower).
        var undo = BuildUndo(_localCommunityIri, mintedFollowId);
        using var undoRequest = SignedRequest(_localCommunityIri, _bCommunityKey, undo, $"/ap/v1/c/{LocalCommunity}/outbox");
        using var undoResponse = await _bHttp.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // The community's follows-set edge is removed.
        Assert.DoesNotContain(
            _remoteCommunityIri, await _bPersistence.Communities.GetFollowsAsync(_localCommunityIri));
    }

    // --- Two-instance: the community follow federates end to end ------------------

    [Fact]
    public async Task CommunityOutbox_FollowOfRemoteCommunity_FederatesAndAcceptFinalizesBothSides()
    {
        var follow = BuildFollow(_localCommunityIri, _remoteCommunityIri);
        using var request = SignedRequest(_localCommunityIri, _bCommunityKey, follow, $"/ap/v1/c/{LocalCommunity}/outbox");

        // B's community iris publishes the follow to its own outbox.
        using var response = await _bHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Decision 055: B minted the follow's id (returned in the 2xx body). Inbound federation keeps the
        // originator's id, so A stores the follow under this same minted id.
        var mintedIdNullable = await LearnMintedIdAsync(response);
        Assert.True(mintedIdNullable != null, "B should have returned the minted follow id in the 2xx body.");
        Iri mintedId = mintedIdNullable.Value;

        // B recorded its side of the edge immediately (the community's follows set).
        Assert.Contains(_remoteCommunityIri, await _bPersistence.Communities.GetFollowsAsync(_localCommunityIri));

        // B's server delivered the follow to A's community inbox (signed as iris); A validated it (fetching
        // B's community document for the Group's key), stored it, and recorded A's side of the edge (lumen
        // follows iris) + the inverse (lumen's followers set lists iris). Wait on the EFFECT of the
        // delivery (A's follows-set edge), not on storage.
        await WaitForAsync(async () =>
            (await _aPersistence.Communities.GetFollowsAsync(_remoteCommunityIri)).Contains(_localCommunityIri),
            timeout: TimeSpan.FromSeconds(10));
        Assert.True(await _aPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            "A should have stored the community follow delivered by B's server (under the minted id)");
        Assert.Contains(_localCommunityIri, await _aPersistence.Communities.GetFollowsAsync(_remoteCommunityIri));
        Assert.Contains(_localCommunityIri, await _aPersistence.Communities.GetFollowersAsync(_remoteCommunityIri));

        // A's FollowActivityHandler queued an Accept back to iris's inbox; B's DeliveryWorker delivers it;
        // B's AcceptActivityHandler finalizes iris's own edge (the follower side — the gap the
        // AcceptActivityHandler extension closes). Both sides' `following` collections then carry the edge.
        var aFollowing = await CollectionItemsAsync(_aHttp, $"https://{AHost}/ap/v1/c/{RemoteCommunity}/following");
        Assert.Contains(_localCommunityIri.Value, aFollowing);
        var bFollowing = await CollectionItemsAsync(_bHttp, $"https://{BHost}/ap/v1/c/{LocalCommunity}/following");
        Assert.Contains(_remoteCommunityIri.Value, bFollowing);
    }

    [Fact]
    public async Task CommunityOutbox_UndoOfRemoteFollow_RemovesEdgeOnBothSides()
    {
        var follow = BuildFollow(_localCommunityIri, _remoteCommunityIri);
        Iri mintedFollowId = default!;
        using (var followRequest = SignedRequest(_localCommunityIri, _bCommunityKey, follow, $"/ap/v1/c/{LocalCommunity}/outbox"))
        {
            using var followResponse = await _bHttp.SendAsync(followRequest);
            Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);
            // Decision 055: learn the follow's minted id from the 2xx body (the Undo references it).
            var followMintedNullable = await LearnMintedIdAsync(followResponse);
            Assert.True(followMintedNullable != null, "the server should have returned the minted follow id in the 2xx body.");
            mintedFollowId = followMintedNullable.Value;
        }

        // Wait for the follow to federate to A (A records its side of the edge).
        await WaitForAsync(async () =>
            (await _aPersistence.Communities.GetFollowsAsync(_remoteCommunityIri)).Contains(_localCommunityIri),
            timeout: TimeSpan.FromSeconds(10));

        // iris un-follows lumen: publish the Undo (referencing the follow's LEARNED id) to iris's outbox.
        var undo = BuildUndo(_localCommunityIri, mintedFollowId);
        using var undoRequest = SignedRequest(_localCommunityIri, _bCommunityKey, undo, $"/ap/v1/c/{LocalCommunity}/outbox");
        using var undoResponse = await _bHttp.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // B removed its side of the edge immediately.
        Assert.DoesNotContain(
            _remoteCommunityIri, await _bPersistence.Communities.GetFollowsAsync(_localCommunityIri));

        // B's server delivered the Undo to A's community inbox (signed as iris); A's UndoActivityHandler
        // removes A's side of the edge (lumen no longer follows iris). Wait on the EFFECT of the delivery.
        await WaitForAsync(async () =>
            !(await _aPersistence.Communities.GetFollowsAsync(_remoteCommunityIri)).Contains(_localCommunityIri),
            timeout: TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(_localCommunityIri, await _aPersistence.Communities.GetFollowsAsync(_remoteCommunityIri));
    }

    // --- Helpers ------------------------------------------------------------------

    /// <summary>
    /// Builds a signed <see cref="StringContent"/> for the given activity: the request is signed as
    /// <paramref name="actorIri"/> (key <paramref name="key"/>) by running it through the client's
    /// <see cref="SigningHandler"/> over a capture handler, and the signed request (body + signature
    /// headers) is returned for replay through the plain <see cref="HttpClient"/>. The returned message
    /// is disposable.
    /// </summary>
    private HttpRequestMessage SignedRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        // Sign with the activity+json content-type so the signature covers it, then replay with the
        // SAME content-type (a mismatch on any signed component — content-type, digest, or date —
        // invalidates the signature). A plain StringContent defaults to text/plain, so the content
        // type is set explicitly on the signed request.
        var capture = new CaptureHandler();
        using (var client = BuildClient(actorIri, key, capture))
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
            // Content-Type and Date are handled separately (content-type is a content header; date is a
            // RESTRICTED header on content headers, so it must go on the request headers).
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

        // Date is restricted on content headers but allowed on request headers; the server merges
        // request + content headers when reconstructing the signature base.
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
    /// Builds a signed <see cref="IActivityPubClient"/> (signed as <paramref name="actorIri"/>, key
    /// <paramref name="key"/>) whose transport is the given <paramref name="handler"/>.
    /// </summary>
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

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> whose client (signed as the given
    /// <paramref name="handle"/>) routes over <paramref name="handler"/> — i.e. the instance's fetcher
    /// reaches the other instance's actor/community documents.
    /// </summary>
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
    /// Starts a single-instance <c>TestServer</c> with the given host/handle/persistence, registering the
    /// instance actor's key (and the community's key, so the outbound <c>DeliveryWorker</c> can sign as the
    /// community), optionally overriding the <see cref="IActorDocumentFetcher"/> (federation wiring) and the
    /// outbound delivery transport (so this instance's server-delivery routes to the other instance's
    /// <c>TestServer</c>).
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        KeyPair instanceKey, KeyPair communityKey,
        IActorDocumentFetcher? fetcher = null,
        Func<HttpMessageHandler>? deliveryTransport = null)
    {
        var instanceActorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        var communityIri = new Iri($"https://{host}/ap/v1/c/{LocalCommunity}");

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(instanceKey);
        keyStore.PutKey(communityKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(instanceActorIri, instanceKey.KeyId);
        keyProvider.RegisterKey(communityIri, communityKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri($"https://{host}");
                    opts.InstanceName = $"iris-{host}";
                    opts.InstanceActorId = instanceActorIri;
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(keyStore);
                s.AddSingleton<IKeyProvider>(keyProvider);
                s.AddSingleton<ISignatureSigner>(signer);

                if (fetcher is not null)
                {
                    s.AddSingleton<IActorDocumentFetcher>(fetcher);
                }

                if (deliveryTransport is not null)
                {
                    s.AddSingleton<Func<HttpMessageHandler>>(() => deliveryTransport());
                }
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }

    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        // Decision 055: the client sends the follow's shape (no id); the server mints the id and returns
        // it in the 2xx body.
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    private static Undo BuildUndo(Iri actorIri, Iri originalFollowId) => new()
    {
        // Decision 055: the Undo references the original follow by its LEARNED (server-minted) id and
        // carries no id of its own (the server mints the Undo's id).
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(originalFollowId.Value) }],
    };

    /// <summary>
    /// Learns the server-minted id from a community-outbox 2xx response body (decision 055: the server
    /// returns the created object in the 2xx body). Returns null when the body is empty or carries no id.
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

    private static async Task<List<string>> CollectionItemsAsync(HttpClient http, string url)
    {
        var response = await http.GetAsync(url + "?limit=100");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToList();
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
    /// Captures a signed request (its body + headers) instead of forwarding it, so the signed body can be
    /// replayed through a plain <see cref="HttpClient"/> (the <c>SigningHandler</c> adds the
    /// Signature/Digest/Date headers; this handler records them rather than performing the POST).
    /// </summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public CapturedRequest? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            // Capture BOTH request headers and content headers: the SigningHandler puts Date/Digest/
            // Content-Type as content headers (not in request.Headers), so capturing only request.Headers
            // would drop them and the replayed signature would fail to verify.
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = values.ToList();
            }

            if (request.Content is { } contentHeaders)
            {
                foreach (var (name, values) in contentHeaders.Headers)
                {
                    headers[name] = values.ToList();
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
