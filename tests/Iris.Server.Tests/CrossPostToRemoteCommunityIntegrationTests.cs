using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
/// Phase 138.10 end-to-end test (push-content-to-Lemmy delivery path — the cross-post leg, decision 058):
/// an Iris author (alice on instance B) cross-posts to a <em>remote</em> community (a community on
/// instance A — the Lemmy stand-in) by addressing that community in the <c>Create</c>'s <c>to</c>
/// audience. A community *follow* is a pull (decision 036 / Phase 89.1) — it does not push the
/// follower's own posts into the followed community — so the post reaching the remote community's own
/// content requires the explicit cross-post: the author names the target community in <c>to</c>, and the
/// server delivers the <c>Create</c> to that community's inbox, exactly as a Lemmy/Mastodon client
/// cross-posts.
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local) hosts a community (<c>inter</c>, member bob — the cross-post
/// target, the Lemmy community stand-in); instance B (b.domain.local) hosts alice (the author).
/// Bidirectional federation wiring:
/// <list type="bullet">
/// <item>B's delivery transport routes to A, so B's hosted <see cref="Iris.Server.Delivery.DeliveryWorker"/>
/// delivers the cross-posted <c>Create</c> to A's community inbox.</item>
/// <item>A's inbound fetcher reaches B, so A can validate the <c>Create</c>'s signature (resolving
/// alice's key from B's actor doc).</item>
/// </list>
/// The cross-post is authored by a signed <c>POST /ap/v1/u/alice/outbox</c> to B (the local-outbox publish
/// path, where the 138.10 cross-post delivery leg lives). The <c>Create</c>'s <c>to</c> carries A's
/// community IRI (the cross-post target); the <c>to</c> is preserved by the audience rewrite (which only
/// appends followers to <c>cc</c>), so the cross-post leg delivers to it. The assertions read A's
/// persistence: the cross-posted <c>Note</c> is stored on A (the target instance) and recorded in the
/// community's local member's outbox (so it surfaces in the community feed — the post landed in the
/// target community).
/// </remarks>
public sealed class CrossPostToRemoteCommunityIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string ACommunity = "inter";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _aCommunityIri;
    private readonly Iri _bobActorIri;

    public CrossPostToRemoteCommunityIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts bob (a member of the cross-post-target community) + the community (the Lemmy
        // stand-in). B hosts alice (the author who cross-posts).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Bob);
        _bobActorIri = aSeeded.ActorIri;
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Alice);
        _aliceKey = bSeeded.Key;
        _aliceActorIri = bSeeded.ActorIri;

        // A's community, with bob as a local member (so the inbound cross-post is recorded in bob's
        // outbox by CommunityContentRecorder). The community key is registered with A's identity keys
        // (not strictly required for the inbound path, but mirrors the production wiring).
        var (aCommunityKey, aCommunityIri, _) = TestSeeder.SeedCommunityWithKey(
            _aPersistence, AHost, ACommunity, memberIri: _bobActorIri);
        _aCommunityIri = aCommunityIri;

        var aServerHolder = new TestServerHolder();
        var bServerHolder = new TestServerHolder();

        // A: its inbound fetcher reaches B (validates the cross-posted Create signed by alice).
        _a = aServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Bob,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentityKeys(aSeeded.Key, aSeeded.ActorIri, aCommunityKey, aCommunityIri),
            Fetcher = BuildFetcherFor(AHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
            Client = BuildClient(BHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
        });

        // B: its delivery transport reaches A (delivers the cross-posted Create to A's community inbox
        // via B's hosted DeliveryWorker); its inbound fetcher is a self-fetcher (validates the Create
        // signed by alice, B's local actor).
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

        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri($"https://{BHost}") };
    }

    public void Dispose()
    {
        _bHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- The cross-posted Create reaches the remote community and lands there --------------------

    [Fact]
    public async Task CrossPostToRemoteCommunity_DeliversCreateToCommunityInbox_AndLandsThere()
    {
        // alice (B) cross-posts to A's community by addressing the community in the Create's `to`
        // audience (the cross-post target, per decision 058). The embedded Note is a standard post.
        var noteIri = new Iri($"https://{BHost}/ap/v1/objects/note-{Guid.NewGuid():N}");
        var createIri = new Iri($"https://{BHost}/activities/create-{Guid.NewGuid():N}");
        var create = BuildCrossPostCreate(_aliceActorIri, noteIri, createIri, _aCommunityIri);

        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // Wait on the EFFECT of the cross-post (A storing the cross-posted Note), not on B's storage: the
        // Create must have crossed to A (the target community's instance) and been stored there (A
        // validated alice's signature via its fetcher → B, then A's CreateActivityHandler stored the
        // embedded Note).
        await WaitForAsync(
            async () => await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(30));

        // (a) The cross-posted Note is stored on A (the target community's instance) — the post reached
        // the target instance (it was not stranded on B alone). The Note's IRI is in B's namespace (B
        // minted it, decision 055); A stores it under that IRI and serves it from its object store.
        Assert.True(
            await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var storedNote),
            "A (the cross-post target community's instance) should have stored the cross-posted Note");

        // (b) The cross-post landed in the target community: the community's local member (bob) has the
        // cross-posted Create in his outbox (CommunityContentRecorder recorded it on A's inbound Create
        // path), so it surfaces in the community feed. The embedded object is a Page (138.11
        // Note-vs-Page: a top-level cross-post carries a Page, not a Note).
        var bobOutbox = (await _aPersistence.Activities.GetOutboxAsync(_bobActorIri)).ToList();
        Assert.Contains(bobOutbox, a =>
            a is Create crossPost
                && crossPost.Object?.OfType<Page>().Any(n => n.Id == noteIri.Value) == true);
    }

    // --- Negative control: a post NOT addressed to the remote community does not reach it --------

    [Fact]
    public async Task PlainPostNotAddressedToRemoteCommunity_DoesNotReachIt()
    {
        // alice (B) posts a plain public note (to: the as:Public sentinel only — no cross-post target).
        // A community follow is a pull (decision 036 / 89.1), and alice is NOT a follower of A's
        // community (and vice versa), so nothing should deliver this post to A's community. This is the
        // negative control for the cross-post leg: it proves the `to`-addressing (decision 058) is the
        // mechanism that delivers a post to a remote community, not an incidental fan-out path.
        var noteIri = new Iri($"https://{BHost}/ap/v1/objects/note-{Guid.NewGuid():N}");
        var createIri = new Iri($"https://{BHost}/activities/create-{Guid.NewGuid():N}");
        var create = BuildPlainPublicCreate(_aliceActorIri, noteIri, createIri);

        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // Give the (non-)delivery a chance to complete, then assert the post did NOT reach A's community:
        // the Note is not stored on A, and bob (the community's local member) has no such Create in his
        // outbox.
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(
            await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            "A plain post (not cross-posted) must not reach the remote community's instance");

        var bobOutbox = (await _aPersistence.Activities.GetOutboxAsync(_bobActorIri)).ToList();
        Assert.DoesNotContain(bobOutbox, a =>
            a is Create crossPost
                && crossPost.Object?.OfType<Note>().Any(n => n.Id == noteIri.Value) == true);
    }

    // --- Regression: a cached remote community is still a cross-post target ----------------------

    [Fact]
    public async Task CrossPostToCachedRemoteCommunity_StillDelivers()
    {
        // The peered-Lemmy case (138.11 live defect): the authoring instance (B) has FOLLOWED the target
        // community (A's community), so it has A's community document cached in B's OWN durable actor
        // store. The cross-post leg's "is local?" test must be host-based, not actor-store-membership-
        // based: a cached remote community is NOT on B's instance, so the cross-post must still deliver
        // to it. Before the fix, GetCrossPostTargetsAsync used ILocalActorResolver.IsLocalActorAsync
        // (store membership), which misclassified the cached remote as local and dropped the cross-post
        // entirely — the post never reached A.
        //
        // Seed A's community into B's actor store to simulate B having followed/cached it (exactly what
        // RemoteActorPersister does on a follow/fetch). The community's IRI is on A's host, so the
        // host-based check correctly treats it as remote.
        var cachedCommunity = new Group
        {
            Id = _aCommunityIri.Value,
            PreferredUsername = ACommunity,
        };
        await _bPersistence.Actors.PutActorAsync(cachedCommunity, CancellationToken.None);

        // Sanity: the cached community is now in B's actor store (so the old store-membership check WOULD
        // have misclassified it as local).
        Assert.True(
            await _bPersistence.Actors.TryGetActorAsync(_aCommunityIri, out _),
            "precondition: A's community must be cached in B's actor store for this regression test");

        // alice (B) cross-posts to A's community (addressing it in the Create's `to`, decision 058).
        var noteIri = new Iri($"https://{BHost}/ap/v1/objects/note-{Guid.NewGuid():N}");
        var createIri = new Iri($"https://{BHost}/activities/create-{Guid.NewGuid():N}");
        var create = BuildCrossPostCreate(_aliceActorIri, noteIri, createIri, _aCommunityIri);

        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // Despite the community being cached in B's actor store, the cross-post must still deliver to A
        // (host-based "is local?" → remote → delivered). Wait on the effect (A storing the Note).
        await WaitForAsync(
            async () => await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(30));

        Assert.True(
            await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            "a cross-post to a CACHED remote community must still deliver to the remote instance " +
            "(the 'is local?' test is host-based, not actor-store-membership-based)");
    }

    // --- Regression: the Iris client composes `to` on the embedded Note, not the Create ----------

    [Fact]
    public async Task CrossPostWithNoteLevelAudience_StillDelivers()
    {
        // The Iris <see cref="IActivityPubClient.PostNoteAsync"/> client composes the audience on the
        // embedded Note's `to` (leaving the Create's activity-level `to` empty). The cross-post leg must
        // read the audience from BOTH levels: before this fix it read only the Create's `to`, so a
        // client-form cross-post (Note-level `to`) silently produced NO target and the post never
        // reached the remote community (the 138.11 live defect — the stored Create had an empty `to`).
        var noteIri = new Iri($"https://{BHost}/ap/v1/objects/note-{Guid.NewGuid():N}");
        var createIri = new Iri($"https://{BHost}/activities/create-{Guid.NewGuid():N}");
        var create = BuildCrossPostCreateWithNoteAudience(_aliceActorIri, noteIri, createIri, _aCommunityIri);

        // Sanity: the Create's activity-level `to` is null/empty (the audience is on the Note, client form).
        Assert.True(create.To is null || !create.To.Any(), "precondition: the Create's activity-level `to` must be empty (client form)");

        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // Despite the audience being on the Note (not the Create), the cross-post must still deliver to A.
        await WaitForAsync(
            async () => await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(30));

        Assert.True(
            await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            "a cross-post whose audience is on the embedded Note (the Iris client form) must still " +
            "deliver to the remote community (the leg reads `to` from both the Create and the Note)");
    }

    // --- Helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Builds a cross-post <see cref="Create"/>: the actor (alice) creates a <see cref="Note"/> and
    /// explicitly addresses the target community (the cross-post target, decision 058) in the activity's
    /// <c>to</c> audience (in addition to the public sentinel).
    /// </summary>
    private static Create BuildCrossPostCreate(Iri actorIri, Iri noteIri, Iri createIri, Iri targetCommunityIri) => new()
    {
        Id = createIri.Value,
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        To =
        [
            new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") },
            new Link { Href = new Uri(targetCommunityIri.Value) },
        ],
        Object =
        [
            new Note
            {
                Id = noteIri.Value,
                Content = ["a post cross-posted to a remote community"],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            },
        ],
    };

    /// <summary>
    /// Builds a plain public <see cref="Create"/>: the actor (alice) creates a <see cref="Note"/> addressed
    /// only to the public sentinel (no cross-post target community in <c>to</c>).
    /// </summary>
    private static Create BuildPlainPublicCreate(Iri actorIri, Iri noteIri, Iri createIri) => new()
    {
        Id = createIri.Value,
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        To =
        [
            new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") },
        ],
        Object =
        [
            new Note
            {
                Id = noteIri.Value,
                Content = ["a plain public post, not cross-posted"],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            },
        ],
    };

    /// <summary>
    /// Builds a cross-post <see cref="Create"/> in the <em>Iris client form</em> (matching the string
    /// overload of <c>IActivityPubClient.PostNoteAsync</c>): the community IRI (the cross-post target) is
    /// composed on the embedded <see cref="Note"/>'s <c>to</c> audience, and the Create's activity-level
    /// <c>to</c> is left empty. This is the form that the 138.11 live defect missed (the leg read only the
    /// Create's <c>to</c>).
    /// </summary>
    private static Create BuildCrossPostCreateWithNoteAudience(Iri actorIri, Iri noteIri, Iri createIri, Iri targetCommunityIri) => new()
    {
        Id = createIri.Value,
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        // No activity-level `to` (the Iris client leaves it empty; the audience is on the Note).
        Object =
        [
            new Note
            {
                Id = noteIri.Value,
                Content = ["a post cross-posted to a remote community (audience on the Note, client form)"],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
                To =
                [
                    new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") },
                    new Link { Href = new Uri(targetCommunityIri.Value) },
                ],
            },
        ],
    };

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on the
    /// author's outbox (on <see cref="BHost"/>). Uses the client pipeline (via a
    /// <see cref="CaptureHandler"/>) to produce a correctly signed request, then replays the signed
    /// headers onto a fresh request for delivery to B's <see cref="TestServer"/>.
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
    /// Builds the identity keys (key store + provider + signer) for one or two local actors (a person and,
    /// optionally, a community).
    /// </summary>
    private static IdentityKeys BuildIdentityKeys(KeyPair personKey, Iri personActorIri, KeyPair? communityKey = null, Iri? communityIri = null)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(personKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(personActorIri, personKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        if (communityKey is { } cKey && communityIri is { } cIri)
        {
            keyStore.PutKey(cKey);
            keyProvider.RegisterKey(cIri, cKey.KeyId);
        }

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
    /// (deferred) <paramref name="targetServer"/> — the instance's outbound object fetcher.
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
