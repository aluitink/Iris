using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Lemmy interop (Undo shape): when a person on instance A un-follows a person on instance B, the
/// server-delivered <see cref="Undo"/> of the follow must carry the original <see cref="Follow"/>
/// EMBEDDED (its <c>object</c> set to the stored <c>Follow</c>), not a bare IRI link. A remote peer whose
/// ActivityStreams parser requires the <c>Undo</c>'s object to be an embedded activity (Lemmy's
/// <c>Activity</c> untagged enum) rejects a bare IRI with a 400 (the un-follow never reaching the remote
/// instance); embedding the original Follow is what makes the un-follow federate.
/// </summary>
/// <remarks>
/// This locks the outbound Undo shape that the cross-instance unfollow tests
/// (<see cref="PersonFollowsPersonUnfollowPropagationIntegrationTests"/>) do not assert: they verify the
/// edge is removed on B (the receiving handler resolves the parties), but not whether the delivered
/// <c>Undo</c>'s <c>object</c> is an embedded <c>Follow</c> or a bare link. The capturing delivery
/// transport on A records the signed Undo exactly as A's server hands it to the worker; the test asserts
/// the embedded shape (a regression against the pre-fix bare-link shape).
/// </remarks>
/// <para>
/// Topology: instance A (pue-a.domain.local) hosts the local person <c>alice</c> (the instance actor,
/// with its own signing key). Instance B (pue-b.domain.local) hosts the local person <c>bob</c>. A's
/// outbound delivery worker routes to B (captured, not forwarded — the shape is under test, not the
/// delivery); A's fetcher routes by actor-IRI host (alice → A, bob → B). B's fetcher routes to A. The
/// client's writes are signed POSTs to alice's outbox on A; the cross-instance hop (A → B) is made by A's
/// server, signed as alice.
/// </para>
[Collection("PersonUnfollowEmbeddedObjectPropagation")]
public sealed class PersonUnfollowEmbeddedObjectPropagationIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "pue-a.domain.local";
    internal const string BHost = "pue-b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";

    private readonly PersonUnfollowEmbeddedObjectPropagationSharedHost _fixture;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly CapturingDeliveryTransport _capture;
    private KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;

    public PersonUnfollowEmbeddedObjectPropagationIntegrationTests(PersonUnfollowEmbeddedObjectPropagationSharedHost fixture)
    {
        _fixture = fixture;
        _capture = fixture.Capture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _aliceKey = null!;
        _aHttp = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        _capture.Clear();
        SeedForFixture(_aPersistence, _bPersistence);

        _aPersistence.Keys.TryGetKey(new Iri($"{_aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores alice (on A) and bob (on B) with their existing keys.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"{bobIri.Value}#key-1"));
    }

    // --- alice follows bob (federates A → B), then un-follows; the server-delivered Undo carries the
    //     original Follow EMBEDDED (Lemmy interop: a bare IRI link is rejected by a parser that requires
    //     the Undo's object to be an embedded activity). ---

    [Fact]
    public async Task PersonUnfollowOfRemotePerson_DeliversUndoWithEmbeddedFollow()
    {
        // Step 1: establish the follow (federates A → B; B records the alice → bob edge + stores the
        // Follow in its activity store).
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        using var followRequest = SignedRequest(_aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox");
        using var followResponse = await _aHttp.SendAsync(followRequest);
        Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);

        var mintedFollowId = await LearnMintedIdAsync(followResponse);
        Assert.True(mintedFollowId is not null, "A should have returned the minted Follow id in the 2xx body.");

        // B recorded the edge (and stored the Follow in its activity store).
        await WaitForAsync(
            () => _bPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));

        // Step 2: publish alice's Undo(Follow) to alice's outbox. A removes the local edge and
        // server-delivers the signed Undo to bob's inbox on B — captured by the delivery transport.
        var undo = BuildUndo(_aliceActorIri, mintedFollowId!.Value);
        using var undoRequest = SignedRequest(_aliceActorIri, _aliceKey, undo, $"/ap/v1/u/{Alice}/outbox");
        using var undoResponse = await _aHttp.SendAsync(undoRequest);
        Assert.Equal(HttpStatusCode.Accepted, undoResponse.StatusCode);

        // A removed alice's local follow edge (the outbox-publish handler's Undo branch).
        await WaitForAsync(
            async () => !await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));

        // A's server handed the Undo to the delivery worker (to bob's inbox) — captured, not forwarded.
        // Wait specifically for the Undo delivery (the Follow from step 1 is also captured).
        var delivered = await WaitForAsync(
            async () =>
            {
                _capture.WaitUntilDrained();
                return _capture.LastUndoDelivered is not null;
            },
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(delivered, "A should have delivered the Undo to bob's inbox (captured).");
        var (targetIri, body) = _capture.LastUndoDelivered!.Value;
        // A resolved bob's inbox (bob's actor IRI + "/inbox") as the delivery target.
        Assert.Equal(_bobActorIri.Value + "/inbox", targetIri);

        // The delivered Undo carries the original Follow EMBEDDED (its object is an object with the
        // Follow's id + type), not a bare IRI link. A bare link fails a parser that requires the Undo's
        // object to be an embedded activity (Lemmy: 400 "data did not match any variant of untagged
        // enum AnnouncableActivities") — the un-follow never reaches the remote instance.
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("Undo", root.GetProperty("type").GetString());

        var objectElement = root.GetProperty("object");
        Assert.True(
            objectElement.ValueKind == JsonValueKind.Object,
            "The delivered Undo must embed the original Follow as an object (Lemmy interop); a bare IRI " +
            "link is rejected by a parser that requires the Undo's object to be an embedded activity.");

        var followType = objectElement.GetProperty("type").GetString();
        var followId = objectElement.GetProperty("id").GetString();
        var followObject = objectElement.GetProperty("object").GetString();
        var followActor = objectElement.GetProperty("actor").GetString();
        Assert.Equal("Follow", followType);
        Assert.Equal(mintedFollowId.ToString(), followId);
        Assert.Equal(_bobActorIri.ToString(), followObject);
        Assert.Equal(_aliceActorIri.ToString(), followActor);
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Builds A's signing identity: a key store carrying the instance actor's (alice's) key, a provider
    /// registering it at alice's IRI, and a signer.
    /// </summary>
    internal static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri)
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

    internal static IActorDocumentFetcher BuildFetcherFor(
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
    /// Builds an id-less <see cref="Follow"/> from <paramref name="followerIri"/> (alice) to
    /// <paramref name="targetIri"/> (bob) (id-less: the server mints the activity's id on publish —
    /// decision 055).
    /// </summary>
    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// Builds an id-less <see cref="Undo"/> by <paramref name="actorIri"/> (alice) of the original follow
    /// <paramref name="originalFollowId"/> (the server-minted Follow IRI). (id-less: the server mints the
    /// Undo's own id on publish — decision 055.)
    /// </summary>
    private static Undo BuildUndo(Iri actorIri, Iri originalFollowId) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(originalFollowId.Value) }],
    };

    /// <summary>
    /// Learns the server-minted id from a 2xx response body (decision 055: the server returns the created
    /// object in the 2xx body). Returns null when the body is empty or carries no id.
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

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return await probe();
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents based
    /// on the actor IRI's host (A's fetcher needs to reach A and B).
    /// </summary>
    internal sealed class RoutingFetcher : IActorDocumentFetcher
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

/// <summary>
/// A delivery transport that CAPTURES each delivered activity (its target inbox IRI + body) instead of
/// forwarding it, so the test can assert the exact shape A's server hands to the delivery worker (the
/// outbound Undo's embedded-object shape). <see cref="LastDelivered"/> is the most recent capture.
/// </summary>
public sealed class CapturingDeliveryTransport : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<(string TargetIri, string Body)> _delivered = new();

    /// <summary>The most recently captured delivery (target inbox IRI + body).</summary>
    public (string TargetIri, string Body)? LastDelivered
    {
        get { lock (_gate) { return _delivered.Count == 0 ? null : _delivered[^1]; } }
    }

    /// <summary>The most recently captured delivery whose body is an <c>Undo</c> activity.</summary>
    public (string TargetIri, string Body)? LastUndoDelivered
    {
        get
        {
            lock (_gate)
            {
                for (var i = _delivered.Count - 1; i >= 0; i--)
                {
                    if (_delivered[i].Body.Contains("\"Undo\""))
                    {
                        return _delivered[i];
                    }
                }

                return null;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _delivered.Clear();
        }
    }

    public void WaitUntilDrained()
    {
        // The capture is synchronous in SendAsync; nothing to drain. Kept as an explicit no-op seam so a
        // test reads clearly ("wait for the delivery to land").
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? ""
            : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        lock (_gate)
        {
            _delivered.Add((request.RequestUri!.ToString(), body));
        }

        var response = new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new ByteArrayContent([]),
        };
        return Task.FromResult(response);
    }
}

/// <summary>
/// Shared two-host fixture for
/// <see cref="PersonUnfollowEmbeddedObjectPropagationIntegrationTests"/> (A: pue-a.domain.local alice,
/// B: pue-b.domain.local bob). Seeds alice + bob with keys ONCE; wires A's outbound delivery to the
/// CAPTURING transport (the Undo shape is under test, not the delivery) and routing fetchers.
/// </summary>
public sealed class PersonUnfollowEmbeddedObjectPropagationSharedHost : SharedTwoHostFixture
{
    private static readonly CapturingDeliveryTransport _capture = new();

    public CapturingDeliveryTransport Capture => _capture;

    public PersonUnfollowEmbeddedObjectPropagationSharedHost()
        : base(BuildOptions(_capture))
    {
    }

    public new void Reset()
    {
        _capture.Clear();
        base.Reset();
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions(CapturingDeliveryTransport capture)
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, PersonUnfollowEmbeddedObjectPropagationIntegrationTests.AHost, PersonUnfollowEmbeddedObjectPropagationIntegrationTests.Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, PersonUnfollowEmbeddedObjectPropagationIntegrationTests.BHost, PersonUnfollowEmbeddedObjectPropagationIntegrationTests.Bob);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var optionsA = new ActivityPubHostOptions
        {
            Host = PersonUnfollowEmbeddedObjectPropagationIntegrationTests.AHost,
            Handle = PersonUnfollowEmbeddedObjectPropagationIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = PersonUnfollowEmbeddedObjectPropagationIntegrationTests.BuildIdentity(aSeeded.Key, aSeeded.ActorIri),
            // A's outbound delivery is CAPTURED (the Undo shape is under test), not forwarded to B.
            DeliveryTransport = () => capture,
            Fetcher = new PersonUnfollowEmbeddedObjectPropagationIntegrationTests.RoutingFetcher(
                PersonUnfollowEmbeddedObjectPropagationIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                PersonUnfollowEmbeddedObjectPropagationIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = PersonUnfollowEmbeddedObjectPropagationIntegrationTests.BHost,
            Handle = PersonUnfollowEmbeddedObjectPropagationIntegrationTests.Bob,
            Persistence = bPersistence,
            Fetcher = PersonUnfollowEmbeddedObjectPropagationIntegrationTests.BuildFetcherFor(
                PersonUnfollowEmbeddedObjectPropagationIntegrationTests.AHost,
                PersonUnfollowEmbeddedObjectPropagationIntegrationTests.Alice,
                aSeeded.Key,
                new LazyHandler(() => serverARef().CreateHandler())),
        };

        return (optionsA, optionsB);
    }
}

/// <summary>
/// xunit collection definition for the embedded-Undo unfollow-propagation shared two-host fixture.
/// </summary>
[CollectionDefinition("PersonUnfollowEmbeddedObjectPropagation")]
public sealed class PersonUnfollowEmbeddedObjectPropagationCollection : ICollectionFixture<PersonUnfollowEmbeddedObjectPropagationSharedHost>
{
}
