using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 23 (22.11) — <strong>Moderation collection page-cache invalidation on a local moderation
/// write</strong>: an actor's <c>blocks</c>, <c>flags</c>, and <c>mutes</c> collections are served
/// through the local collection-page response cache (a 60s TTL), exactly like the outbox collection.
/// Before this fix, a moderation write that was not paired with an invalidation left the cached page-1
/// stale: the owner's card would not reflect the edge it just recorded (a <see cref="Block"/> / a
/// <see cref="Flag"/> / a mute) or removed (an <see cref="Undo"/> of one / an un-mute) until the TTL
/// lapsed or a <c>?refresh=true</c> bypass was issued.
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts two local actors — <c>alice</c> (the moderator,
/// the instance's Handle actor, holding a signing key for the signed outbox-publish path) and
/// <c>bob</c> (the target of the block/flag, and the muter for the mute tests; bob is an extra local
/// actor so his inbox is served in-process and his key is resolvable). Each test primes the affected
/// collection's page-1 cache with a plain read (the baseline the UI takes on first load), performs the
/// moderation write through the real user path (a signed <c>Block</c>/<c>Flag</c>/<c>Undo</c> to the
/// outbox publish endpoint, or a Basic-authenticated mute/un-mute to the local-moderation endpoint), and
/// then does a PLAIN (non-<c>?refresh</c>) read asserting the edge is immediately visible (or gone).
/// </remarks>
/// <remarks>
/// This is the HTTP-write half of the same boundary <c>OutboxPublishCacheInvalidationIntegrationTests</c>
/// pins for the outbox collection: a write that goes through the moderation write handlers invalidates
/// the collection page so the primary (plain) read is fresh. The three collections share the page-1 key
/// shape <c>{owner}/{collection}</c>, so one generalized invalidation helper covers all of them.
/// </remarks>
public sealed class ModerationCollectionCacheInvalidationIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string BobPassword = "bob-password";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;
    private readonly ILocalModerationClient _local;
    private readonly string _base = $"https://{AHost}";

    public ModerationCollectionCacheInvalidationIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var alice = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        var bob = TestSeeder.SeedPersonWithKey(_persistence, AHost, Bob);
        _aliceKey = alice.Key;
        _aliceActorIri = alice.ActorIri;
        _bobActorIri = bob.ActorIri;

        // A Basic-auth credential validator: bob's credentials are ("bob", "bob-password") for bob's IRI
        // (the mute write authenticates the acting local actor — bob is the muter in the mute tests).
        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(iri == _bobActorIri && username == Bob && password == BobPassword));

        // The inbound key resolver fetches the signing actor's document to recover the public key. That
        // fetch must resolve IN-PROCESS (routed to this TestServer), not over the real network — so the
        // host's IActorDocumentFetcher is an IrisActorDocumentFetcher backed by a LazyHandler that
        // defers to the TestServer once it is constructed (the chicken-and-egg the LazyHandler exists
        // for). A TestServer[] holds the server so the fetcher closure can see it after StartServer.
        var self = new TestServer[1];
        _server = StartServer(_persistence, _aliceKey, _aliceActorIri, credentialValidator, bob.ActorIri, () => self[0]!.CreateHandler());
        self[0] = _server;
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
        _local = BuildLocalModerationClient(bob.ActorIri, bob.Key, () => _server.CreateHandler(),
            new ProxyCredentials(Bob, BobPassword));
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A signed Block is immediately visible on a plain (non-refresh) blocks read ---------

    [Fact]
    public async Task Block_IsVisibleOnPlainBlocksRead_WithoutRefreshBypass()
    {
        // 1) Prime the page-1 cache of alice's blocks collection with a plain read (a miss → renders an
        //    empty page and caches it). This is the baseline the UI takes on first load.
        using (var prime = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/blocks?limit=10"))
        {
            prime.EnsureSuccessStatusCode();
            Assert.DoesNotContain(_bobActorIri.Value, JsonDoc.ItemIdsOf(await prime.Content.ReadAsStringAsync()));
        }

        // 2) Alice blocks bob through the signed outbox-publish endpoint (the real user path). The
        //    handler records the block edge (alice → bob) AND invalidates the cached blocks page-1.
        var block = BuildBlock(_aliceActorIri, _bobActorIri);
        using var request = SignedRequest(_aliceActorIri, _aliceKey, block, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // 3) A PLAIN (non-?refresh) read of alice's blocks collection now lists bob immediately — the
        //    handler's invalidation dropped the stale cached page, so the next read re-renders from the
        //    live store. Without the invalidation this read would serve the stale (empty) cached page
        //    until the 60s TTL lapsed.
        using var read = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/blocks?limit=10");
        read.EnsureSuccessStatusCode();
        Assert.Contains(_bobActorIri.Value, JsonDoc.ItemIdsOf(await read.Content.ReadAsStringAsync()));
    }

    // --- A signed Undo(Block) immediately removes bob from a plain blocks read -------------

    [Fact]
    public async Task UndoBlock_IsGoneFromPlainBlocksRead_WithoutRefreshBypass()
    {
        // Seed a recorded block edge directly in the store (the steady state the UI sees after a block
        // has been in place), then prime the blocks page-1 cache with a plain read (the baseline).
        await _persistence.Moderation.RecordBlockAsync(_aliceActorIri, _bobActorIri);
        using (var prime = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/blocks?limit=10"))
        {
            prime.EnsureSuccessStatusCode();
            Assert.Contains(_bobActorIri.Value, JsonDoc.ItemIdsOf(await prime.Content.ReadAsStringAsync()));
        }

        // Publish the original Block through the outbox-publish endpoint (so it is minted + stored in the
        // activity store — an Undo resolves the undone activity from the store). Capture its minted id.
        var block = BuildBlock(_aliceActorIri, _bobActorIri);
        using var blockRequest = SignedRequest(_aliceActorIri, _aliceKey, block, $"/ap/v1/u/{Alice}/outbox");
        var blockResponse = await _http.SendAsync(blockRequest);
        Assert.Equal(HttpStatusCode.Accepted, blockResponse.StatusCode);
        var blockId = await LearnMintedIdAsync(blockResponse);
        blockResponse.Dispose();

        // Re-prime the cache (the block publish above invalidated it; prime again so the stale page holds
        // the now-present edge — the baseline the Undo must invalidate).
        using (var rePrime = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/blocks?limit=10"))
        {
            rePrime.EnsureSuccessStatusCode();
            Assert.Contains(_bobActorIri.Value, JsonDoc.ItemIdsOf(await rePrime.Content.ReadAsStringAsync()));
        }

        // Alice undoes the block through the signed outbox-publish endpoint. The handler resolves the
        // undone Block from the activity store, removes the edge, AND invalidates the cached blocks page-1.
        var undo = new Undo
        {
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(blockId.Value) }],
        };
        using var undoRequest = SignedRequest(_aliceActorIri, _aliceKey, undo, $"/ap/v1/u/{Alice}/outbox");
        using var undoResponse = await _http.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // A PLAIN (non-?refresh) read of alice's blocks collection is empty again immediately.
        using var read = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/blocks?limit=10");
        read.EnsureSuccessStatusCode();
        Assert.DoesNotContain(_bobActorIri.Value, JsonDoc.ItemIdsOf(await read.Content.ReadAsStringAsync()));
    }

    // --- A signed Flag is immediately visible on a plain (non-refresh) flags read ----------

    [Fact]
    public async Task Flag_IsVisibleOnPlainFlagsRead_WithoutRefreshBypass()
    {
        // 1) Prime the page-1 cache of alice's flags collection with a plain read (a miss → empty page).
        using (var prime = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/flags?limit=10"))
        {
            prime.EnsureSuccessStatusCode();
            Assert.DoesNotContain(_bobActorIri.Value, JsonDoc.ItemIdsOf(await prime.Content.ReadAsStringAsync()));
        }

        // 2) Alice flags bob through the signed outbox-publish endpoint (the real user path).
        var flag = BuildFlag(_aliceActorIri, _bobActorIri);
        using var request = SignedRequest(_aliceActorIri, _aliceKey, flag, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // 3) A PLAIN (non-?refresh) read of alice's flags collection now lists bob immediately.
        using var read = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/flags?limit=10");
        read.EnsureSuccessStatusCode();
        Assert.Contains(_bobActorIri.Value, JsonDoc.ItemIdsOf(await read.Content.ReadAsStringAsync()));
    }

    // --- A Basic-authenticated mute is immediately visible on a plain (non-refresh) mutes read --

    [Fact]
    public async Task Mute_IsVisibleOnPlainMutesRead_WithoutRefreshBypass()
    {
        // 1) Prime the page-1 cache of bob's mutes collection with a plain read (a miss → empty page).
        using (var prime = await _http.GetAsync($"{_base}/ap/v1/u/{Bob}/mutes?limit=10"))
        {
            prime.EnsureSuccessStatusCode();
            Assert.DoesNotContain(_aliceActorIri.Value, JsonDoc.ItemIdsOf(await prime.Content.ReadAsStringAsync()));
        }

        // 2) Bob mutes alice through the Basic-authenticated local-moderation endpoint (the real user
        //    path). The handler records the mute edge (bob → alice) AND invalidates the cached mutes
        //    page-1.
        var muteStatus = await _local.MuteAsync(_bobActorIri, _aliceActorIri);
        Assert.Equal(204, muteStatus.StatusCode);

        // 3) A PLAIN (non-?refresh) read of bob's mutes collection now lists alice immediately.
        using var read = await _http.GetAsync($"{_base}/ap/v1/u/{Bob}/mutes?limit=10");
        read.EnsureSuccessStatusCode();
        Assert.Contains(_aliceActorIri.Value, JsonDoc.ItemIdsOf(await read.Content.ReadAsStringAsync()));
    }

    // --- A Basic-authenticated un-mute immediately removes alice from a plain mutes read -----

    [Fact]
    public async Task Unmute_IsGoneFromPlainMutesRead_WithoutRefreshBypass()
    {
        // Seed a recorded mute edge directly in the store (the steady state the UI sees after a mute has
        // been in place), then prime the mutes page-1 cache with a plain read (the baseline holds the
        // edge).
        await _persistence.Moderation.RecordMuteAsync(_bobActorIri, _aliceActorIri);
        using (var prime = await _http.GetAsync($"{_base}/ap/v1/u/{Bob}/mutes?limit=10"))
        {
            prime.EnsureSuccessStatusCode();
            Assert.Contains(_aliceActorIri.Value, JsonDoc.ItemIdsOf(await prime.Content.ReadAsStringAsync()));
        }

        // Bob un-mutes alice through the Basic-authenticated local-moderation endpoint. The handler
        // removes the edge AND invalidates the cached mutes page-1.
        var unmuteStatus = await _local.UnmuteAsync(_bobActorIri, _aliceActorIri);
        Assert.Equal(204, unmuteStatus.StatusCode);

        // A PLAIN (non-?refresh) read of bob's mutes collection is empty again immediately.
        using var read = await _http.GetAsync($"{_base}/ap/v1/u/{Bob}/mutes?limit=10");
        read.EnsureSuccessStatusCode();
        Assert.DoesNotContain(_aliceActorIri.Value, JsonDoc.ItemIdsOf(await read.Content.ReadAsStringAsync()));
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Builds an id-less <see cref="Block"/> from <paramref name="actorIri"/> to
    /// <paramref name="targetIri"/> (id-less: the server mints the activity's id on publish — decision
    /// 055).
    /// </summary>
    private static Block BuildBlock(Iri actorIri, Iri targetIri) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// Builds an id-less <see cref="Flag"/> from <paramref name="actorIri"/> to
    /// <paramref name="targetIri"/> (id-less: the server mints the activity's id on publish — decision
    /// 055).
    /// </summary>
    private static Flag BuildFlag(Iri actorIri, Iri targetIri) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    private TestServer StartServer(
        InMemoryPersistenceProvider persistence,
        KeyPair aliceKey,
        Iri aliceActorIri,
        IActorCredentialValidator credentialValidator,
        Iri bobActorIri,
        Func<HttpMessageHandler> selfHandlerFactory)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(aliceActorIri, aliceKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        // The inbound fetcher resolves the local actor's document in-process (so the signature middleware
        // can recover the signing actor's public key without a real network hop).
        var fetchClient = new ActivityPubClientFactory(keyStore, keyProvider, signer).Create(
            new ActivityPubClientOptions { ActorId = aliceActorIri, EnableRetry = false },
            new LazyHandler(selfHandlerFactory));
        var fetcher = new IrisActorDocumentFetcher(fetchClient, new RemoteActorCache());

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            CredentialValidator = credentialValidator,
            ExtraLocalActors = [bobActorIri],
            Fetcher = fetcher,
        });
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
                    new HttpRequestMessage(HttpMethod.Post, $"{_base}{path}")
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}{path}")
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

    private static ILocalModerationClient BuildLocalModerationClient(
        Iri actorIri, KeyPair key, Func<HttpMessageHandler> handlerFactory, ProxyCredentials credentials)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.CreateLocalModerationClient(
            new ActivityPubClientOptions
            {
                ActorId = actorIri,
                EnableRetry = false,
                LocalCredentials = credentials,
            },
            new LazyHandler(handlerFactory));
    }

    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var created = ActivityJson.Deserialize<Activity>(body);
        Assert.NotNull(created);
        Assert.NotNull(created!.Id);
        return new Iri(created.Id);
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
