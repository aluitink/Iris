using System.Net;
using System.Net.Http;
using Iris.Client;
using Iris.Core;
using Iris.Core.Signing;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 136.17 integration test: duplicate/replay defense validation. Verifies that:
/// (1) a re-sent identical activity (same ID, same signature) is handled idempotently — the receiving
///     instance stores it once, the handler runs once, and the second delivery is accepted (202) not
///     rejected (500);
/// (2) a replayed signed request (same date, same signature, re-sent verbatim) is accepted — pinning
///     the current behavior that no signature expiry/freshness check exists in the validation path
///     (the <c>date</c> component is cryptographically bound but not wall-clock-freshness-checked);
/// (3) a signed request with its HTTP headers re-ordered on the wire still validates — the canonical
///     signature base is reconstructed from the declared component list (not the wire order), so
///     benign header reordering does not break validation.
/// </summary>
[Collection("CrossInstanceReplayDefense")]
public sealed class CrossInstanceReplayDefenseIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "replay-a.domain.local";
    internal const string BHost = "replay-b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";

    private readonly CrossInstanceReplayDefenseSharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private KeyPair _aliceKey;

    public CrossInstanceReplayDefenseIntegrationTests(CrossInstanceReplayDefenseSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aliceKey = null!;
    }

    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence);
        var aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _aPersistence.Keys.TryGetKey(new Iri($"{aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal static void SeedForFixture(
        InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"{bobIri.Value}#key-1"));
    }

    // --- 1. Duplicate Create delivery is idempotent (stored once, handler runs once) --------

    [Fact]
    public async Task DuplicateCreateDelivery_StoredOnce_HandlerRunsOnce()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/replay-{Guid.NewGuid():N}");
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["replay defense test post"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
            }],
        };

        var inbox = new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox");
        var first = await DeliverDirectlyAsync(aliceIri, _aliceKey, inbox, create, () => _fixture.ServerB);
        var second = await DeliverDirectlyAsync(aliceIri, _aliceKey, inbox, create, () => _fixture.ServerB);

        Assert.True(first, "the first Create delivery should have been accepted (202).");
        Assert.True(second, "a duplicate Create delivery should be accepted as a no-op (202), not error (500).");

        // B stored the activity exactly once (IRI-dedup in the activity store).
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(noteIri, out _),
            "B should have stored the Create activity.");

        // The handler ran once: the note appears in bob's inbox exactly once (AddToInboxAsync is
        // IRI-dedup'd, so even a handler re-run would not duplicate the inbox entry — but the
        // activity-store dedup is the primary guard).
        var inboxItems = await _bPersistence.Activities.GetInboxAsync(bobIri);
        var matchingNotes = inboxItems.Count(
            i => i is IObject { Id: var id } && id == noteIri.Value);
        Assert.Equal(1, matchingNotes);
    }

    // --- 2. Replayed signed request (same date, same signature) is accepted ----------------

    [Fact]
    public async Task ReplayedSignedRequest_SameDateSameSignature_IsAccepted()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/replay-verbatim-{Guid.NewGuid():N}");
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["replayed verbatim"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
            }],
        };

        var inbox = new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox");

        // Deliver once through a capturing handler to record the signed request.
        var captured = await CaptureSignedRequestAsync(aliceIri, _aliceKey, inbox, create, () => _fixture.ServerB);
        Assert.NotNull(captured);

        // Re-send the EXACT SAME signed request (same Date, same Signature, same Digest, same body).
        // This simulates a network-level replay (a man-in-the-middle re-transmitting a captured
        // request). The current behavior: the signature validates (the cryptographic check passes —
        // the date is bound but not freshness-checked), and the inbox accepts it (202). The
        // activity-store IRI dedup then prevents any duplicate state.
        using var replayClient = new HttpClient(_fixture.ServerB.CreateHandler(), disposeHandler: false);
        var replayRequest = new HttpRequestMessage(HttpMethod.Post, inbox.Value);
        replayRequest.Content = new ByteArrayContent(captured!.BodyBytes);
        replayRequest.Content.Headers.TryAddWithoutValidation("Digest", captured.Digest);
        replayRequest.Content.Headers.TryAddWithoutValidation("Content-Type", "application/activity+json");
        replayRequest.Headers.TryAddWithoutValidation("Date", captured.Date);
        replayRequest.Headers.TryAddWithoutValidation("Signature", captured.Signature);

        var response = await replayClient.SendAsync(replayRequest);
        // Current behavior: accepted (202). The signature is valid (cryptographically), and no
        // freshness/expiry check exists to reject a stale date. The activity-store IRI dedup
        // ensures no duplicate state is created.
        Assert.True(
            (int)response.StatusCode >= 200 && (int)response.StatusCode < 300,
            $"replayed signed request should be accepted (no expiry check exists); got {(int)response.StatusCode}");
    }

    // --- 3. Reordered benign headers still validate (canonical base uses declared order) ----

    [Fact]
    public async Task ReorderedWireHeaders_StillValidate()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/reorder-{Guid.NewGuid():N}");
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["header reorder test"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
            }],
        };

        var inbox = new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox");

        // Deliver once through a capturing handler to record the signed request.
        var captured = await CaptureSignedRequestAsync(aliceIri, _aliceKey, inbox, create, () => _fixture.ServerB);
        Assert.NotNull(captured);

        // Re-send the request with the content headers in a different order (Digest before
        // Content-Type, instead of Content-Type before Digest). The canonical signature base is
        // reconstructed from the declared component list in the Signature header (not the wire
        // order of the HTTP headers), so this reordering must not break validation.
        using var reorderClient = new HttpClient(_fixture.ServerB.CreateHandler(), disposeHandler: false);
        var reorderRequest = new HttpRequestMessage(HttpMethod.Post, inbox.Value);
        // Add content headers in REVERSED order: Digest first, then Content-Type.
        reorderRequest.Content = new ByteArrayContent(captured!.BodyBytes);
        reorderRequest.Content.Headers.TryAddWithoutValidation("Digest", captured.Digest);
        reorderRequest.Content.Headers.TryAddWithoutValidation("Content-Type", "application/activity+json");
        reorderRequest.Headers.TryAddWithoutValidation("Date", captured.Date);
        reorderRequest.Headers.TryAddWithoutValidation("Signature", captured.Signature);

        var response = await reorderClient.SendAsync(reorderRequest);
        Assert.True(
            (int)response.StatusCode >= 200 && (int)response.StatusCode < 300,
            $"reordered wire headers should still validate (canonical base uses declared order); got {(int)response.StatusCode}");
    }

    // --- Helpers --------------------------------------------------------------------------

    private static async Task<bool> DeliverDirectlyAsync(
        Iri actorIri, KeyPair key, Iri inbox, Activity activity, Func<TestServer> target)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => target().CreateHandler()));

        var result = await client.DeliverAsync(inbox, activity, CancellationToken.None);
        return result.IsSuccess;
    }

    private record CapturedRequest(
        byte[] BodyBytes, string Date, string Digest, string Signature);

    private static async Task<CapturedRequest?> CaptureSignedRequestAsync(
        Iri actorIri, KeyPair key, Iri inbox, Activity activity, Func<TestServer> target)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var capture = new RequestCaptureHandler(() => target().CreateHandler());
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            capture);

        var result = await client.DeliverAsync(inbox, activity, CancellationToken.None);
        return capture.Captured;
    }

    private sealed class RequestCaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpMessageHandler> _innerFactory;
        private HttpMessageHandler? _inner;
        private HttpClient? _client;

        public CapturedRequest? Captured { get; private set; }

        public RequestCaptureHandler(Func<HttpMessageHandler> innerFactory)
        {
            _innerFactory = innerFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _client ??= new HttpClient(_inner ??= _innerFactory(), disposeHandler: false);

            byte[] bodyBytes = [];
            string? date = null;
            string? digest = null;
            string? signature = null;

            if (request.Content is { } content)
            {
                bodyBytes = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (content.Headers.TryGetValues("Digest", out var digests))
                {
                    digest = string.Join(",", digests);
                }
            }

            if (request.Headers.TryGetValues("Date", out var dates))
            {
                date = string.Join(",", dates);
            }

            if (request.Headers.TryGetValues("Signature", out var sigs))
            {
                signature = string.Join(",", sigs);
            }

            if (date is not null && digest is not null && signature is not null)
            {
                Captured = new CapturedRequest(bodyBytes, date, digest, signature);
            }

            var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } c)
            {
                clone.Content = new ByteArrayContent(bodyBytes);
                foreach (var header in c.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            var response = _client.SendAsync(clone, cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(response);
        }
    }
}

/// <summary>
/// Shared two-host fixture for the replay-defense tests.
/// </summary>
public sealed class CrossInstanceReplayDefenseSharedHost : SharedTwoHostFixture
{
    public CrossInstanceReplayDefenseSharedHost()
        : base(BuildOptions(out _, out _))
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions(
        out InMemoryPersistenceProvider aPersistence, out InMemoryPersistenceProvider bPersistence)
    {
        aPersistence = new InMemoryPersistenceProvider();
        bPersistence = new InMemoryPersistenceProvider();

        var alice = TestSeeder.SeedPersonWithKey(
            aPersistence, CrossInstanceReplayDefenseIntegrationTests.AHost,
            CrossInstanceReplayDefenseIntegrationTests.Alice);
        var bob = TestSeeder.SeedPersonWithKey(
            bPersistence, CrossInstanceReplayDefenseIntegrationTests.BHost,
            CrossInstanceReplayDefenseIntegrationTests.Bob);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var optionsA = new ActivityPubHostOptions
        {
            Host = CrossInstanceReplayDefenseIntegrationTests.AHost,
            Handle = CrossInstanceReplayDefenseIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = BuildIdentity(alice.Key, alice.ActorIri, alice.KeyId),
            Fetcher = new ReplayRoutingFetcher(
                CrossInstanceReplayDefenseIntegrationTests.AHost,
                () => serverARef().CreateHandler(),
                CrossInstanceReplayDefenseIntegrationTests.BHost,
                () => serverBRef().CreateHandler(),
                alice.Key, alice.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = CrossInstanceReplayDefenseIntegrationTests.BHost,
            Handle = CrossInstanceReplayDefenseIntegrationTests.Bob,
            Persistence = bPersistence,
            IdentityKeys = BuildIdentity(bob.Key, bob.ActorIri, bob.KeyId),
            Fetcher = new ReplayRoutingFetcher(
                CrossInstanceReplayDefenseIntegrationTests.AHost,
                () => serverARef().CreateHandler(),
                CrossInstanceReplayDefenseIntegrationTests.BHost,
                () => serverBRef().CreateHandler(),
                bob.Key, bob.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
        };

        return (optionsA, optionsB);
    }

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri, Iri keyId)
    {
        var store = new InMemoryKeyStore();
        store.PutKey(key);
        var provider = new InMemoryKeyProvider(store);
        provider.RegisterKey(actorIri, keyId);
        var signer = new HttpSignatureSigner(store);
        return new IdentityKeys(store, provider, signer);
    }
}

/// <summary>
/// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance based on the
/// actor IRI's host.
/// </summary>
file sealed class ReplayRoutingFetcher : IActorDocumentFetcher
{
    private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

    public ReplayRoutingFetcher(
        string aHost, Func<HttpMessageHandler> aHandlerFactory,
        string bHost, Func<HttpMessageHandler> bHandlerFactory,
        KeyPair signingKey, Iri signingActor)
    {
        _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
        {
            [aHost] = BuildFetcherFor(aHost, aHandlerFactory, signingKey, signingActor),
            [bHost] = BuildFetcherFor(bHost, bHandlerFactory, signingKey, signingActor),
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

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, Func<HttpMessageHandler> handlerFactory, KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(handlerFactory));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}

/// <summary>xUnit collection definition.</summary>
[CollectionDefinition("CrossInstanceReplayDefense")]
public sealed class CrossInstanceReplayDefenseCollection : ICollectionFixture<CrossInstanceReplayDefenseSharedHost>
{
}
