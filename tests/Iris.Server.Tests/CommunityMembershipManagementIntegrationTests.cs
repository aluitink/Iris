using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.5.2 end-to-end test: a local community (<c>iris</c>, a <see cref="Group"/> with a real
/// signing key) manages its own membership through the ActivityStreams <see cref="Add"/>/
/// <see cref="Remove"/> collection-modification primitives. The community posts a signed <c>Add</c>
/// (member to <c>object</c>) through its own inbox; B's <c>signature-validation</c> middleware resolves
/// the community's key (fetching its own <c>Group</c> document, which carries a <c>publicKey</c>), and
/// B's <see cref="Iris.Server.Inbox.AddActivityHandler"/> — now gated by the 19.5.2 self-management
/// authorization (the activity's <c>actor</c> must be the recipient community) — adds the member. The
/// community's <c>feed</c> and <c>members</c> collections reflect the change on the wire; a signed
/// <c>Remove</c> reverses it.
/// </summary>
/// <remarks>
/// <strong>Self-management (19.5.2 authorization).</strong> A community's membership is an act of the
/// community's own management surface, mirroring how it publishes <c>Follow</c>s through its own outbox
/// (the community outbox publish endpoint rejects any activity whose <c>actor</c> is not the community
/// with 403). The <c>Add</c>/<c>Remove</c> handlers apply the same gate: only an <c>Add</c>/<c>Remove</c>
/// whose <em>actor is the recipient community</em> edits that community's member set. An <c>Add</c>
/// posted by any other actor — even a remote actor with a valid signature (see
/// <see cref="AddRemoveFederationIntegrationTests"/>) — is stored (signature validated) but does not
/// modify the membership. This is the authorization 19.5.2 records.
/// </remarks>
public sealed class CommunityMembershipManagementIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Community = "iris";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _communityKey;
    private readonly Iri _communityIri;
    private readonly Iri _aliceIri;
    private readonly Iri _communityInbox;
    private readonly string _base = $"https://{AHost}";

    public CommunityMembershipManagementIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        // A hosts alice (a local member) and the community iris (a Group with a real signing key, so it
        // can sign the Add/Remove it posts through its own inbox).
        _aliceIri = TestSeeder.SeedPerson(_persistence, AHost, Alice);
        var seeded = TestSeeder.SeedCommunityWithKey(_persistence, AHost, Community);
        _communityKey = seeded.Key;
        _communityIri = seeded.CommunityIri;
        _communityInbox = new Iri($"{_communityIri.Value}/inbox");

        // A post by alice (so her content is in her outbox). The community feed is the union of the
        // community's local members' outboxes; alice is a local actor (not yet a member), so her post
        // appears in the feed only once she is added as a member.
        TestSeeder.AddCreateActivity(
            _persistence, _aliceIri, $"{_aliceIri.Value}/activities/create-1", "alice first post");

        // B's fetcher is wired to B ITSELF: to validate an Add/Remove the community signs through its own
        // inbox, B must resolve the community's signing key (iris#key-1) by fetching its OWN community
        // document (a Group carries a publicKey).
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _persistence,
            Fetcher = BuildSelfFetcher(_persistence),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A community-signed Add adds the member; feed + members reflect it -------------

    [Fact]
    public async Task Add_SignedByCommunity_DeliveredToOwnInbox_AddsMemberAndReflectsInFeedAndMembers()
    {
        // Preconditions: alice is a seeded local actor (with a post) but NOT yet a member.
        Assert.False(await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri));
        Assert.True(
            !(await FeedItemIdsAsync()).Contains($"{_aliceIri.Value}/activities/create-1"),
            "alice's post should not be in the feed before the Add");
        Assert.True(
            !(await MemberIrisAsync()).Contains(_aliceIri.Value),
            "alice should not be in the members collection before the Add");

        // The community (iris) posts a signed Add: actor = iris (itself), object = alice (the member to
        // add). The server validates the signature (resolving iris's key from its own Group document) and
        // the AddActivityHandler applies the 19.5.2 gate (actor == recipient community) → adds alice.
        var add = BuildMembershipActivity<Add>(_communityIri, _aliceIri, "add");
        using var request = SignedRequest(_communityIri, _communityKey, add, $"/ap/v1/c/{Community}/inbox");
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The Add was stored (signature validated) …
        Assert.True(await _persistence.Activities.TryGetActivityAsync(new Iri(add.Id!), out _),
            "the community's Add should be stored after the signature validated");

        // … and alice is now a member (the 19.5.2 gate passed because actor == community).
        Assert.True(
            await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri),
            "alice should be a member after the community-signed Add (19.5.2 self-management)");

        // The community feed now reflects membership: alice's post appears (the feed is the union of the
        // local members' outbox activities). The post-mutation read uses ?refresh=true to bypass the
        // collection-page cache (19.5.5) and observe the fresh (post-Add) feed.
        Assert.True(
            (await FeedItemIdsAsync(refresh: true)).Contains($"{_aliceIri.Value}/activities/create-1"),
            "alice's post should appear in the community feed after she is added as a member");

        // The members collection lists alice (post-mutation read bypasses the cache).
        Assert.True(
            (await MemberIrisAsync(refresh: true)).Contains(_aliceIri.Value),
            "alice should be in the members collection after she is added as a member");
    }

    // --- A community-signed Remove removes the member; feed + members reflect it -------

    [Fact]
    public async Task Remove_SignedByCommunity_DeliveredToOwnInbox_RemovesMemberAndReflectsInFeedAndMembers()
    {
        // Seed alice as an existing member (as a prior community-managed Add would have recorded her).
        TestSeeder.AddMember(_persistence, _communityIri, _aliceIri);
        Assert.True(await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri));
        Assert.True(
            (await FeedItemIdsAsync()).Contains($"{_aliceIri.Value}/activities/create-1"),
            "alice's post should be in the feed while she is a member");

        // The community posts a signed Remove: actor = iris, object = alice (the member to remove).
        var remove = BuildMembershipActivity<Remove>(_communityIri, _aliceIri, "remove");
        using var request = SignedRequest(_communityIri, _communityKey, remove, $"/ap/v1/c/{Community}/inbox");
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The Remove was stored (signature validated) …
        Assert.True(await _persistence.Activities.TryGetActivityAsync(new Iri(remove.Id!), out _),
            "the community's Remove should be stored after the signature validated");

        // … and alice is no longer a member (the 19.5.2 gate passed because actor == community).
        Assert.False(
            await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri),
            "alice should no longer be a member after the community-signed Remove (19.5.2 self-management)");

        // The community feed no longer reflects her: her post disappears (the post-mutation read uses
        // ?refresh=true to bypass the collection-page cache, 19.5.5).
        Assert.True(
            !(await FeedItemIdsAsync(refresh: true)).Contains($"{_aliceIri.Value}/activities/create-1"),
            "alice's post should disappear from the feed after she is removed as a member");

        // The members collection no longer lists alice (post-mutation read bypasses the cache).
        Assert.True(
            !(await MemberIrisAsync(refresh: true)).Contains(_aliceIri.Value),
            "alice should not be in the members collection after the Remove");
    }

    // --- An Add whose actor is not the community is stored but does not modify ----------

    [Fact]
    public async Task Add_SignedByCommunityButActorIsAnotherActor_DoesNotModifyMembership()
    {
        // The community signs the request (so the signature validates — the key resolves to the
        // community), but the activity's actor is alice (a different actor). The 19.5.2 gate rejects it:
        // only the community manages its own membership.
        var add = BuildMembershipActivity<Add>(actorIri: _aliceIri, _aliceIri, "add");
        using var request = SignedRequest(_communityIri, _communityKey, add, $"/ap/v1/c/{Community}/inbox");
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The activity was stored (the signature was valid — the community signed it) …
        Assert.True(await _persistence.Activities.TryGetActivityAsync(new Iri(add.Id!), out _),
            "the Add should be stored (the signature was valid)");

        // … but the membership was NOT modified (the actor is not the community).
        Assert.False(
            await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri),
            "an Add whose actor is not the community must not add a member (19.5.2 self-management gate)");
    }

    // --- Helpers ----------------------------------------------------------------------

    private async Task<List<string>> FeedItemIdsAsync(bool refresh = false)
    {
        var url = $"{_base}/ap/v1/c/{Community}/feed?limit=100"
            + (refresh ? "&refresh=true" : string.Empty);
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToList();
    }

    private async Task<List<string>> MemberIrisAsync(bool refresh = false)
    {
        var url = $"{_base}/ap/v1/c/{Community}/members?limit=100"
            + (refresh ? "&refresh=true" : string.Empty);
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToList();
    }

    /// <summary>
    /// Builds an <see cref="Add"/> or <see cref="Remove"/> membership activity: <paramref name="actorIri"/>
    /// is the activity's actor (the community for the self-management case), <paramref name="memberIri"/>
    /// is the <c>object</c> (the member being added/removed).
    /// </summary>
    private static TActivity BuildMembershipActivity<TActivity>(Iri actorIri, Iri memberIri, string kind)
        where TActivity : Activity, new()
    {
        var activity = new TActivity
        {
            Id = $"{actorIri.Value}/{kind}-{Guid.NewGuid():N}",
            Object = [new Link { Href = new Uri(memberIri.Value) }],
        };
        activity.Actor = [new Link { Href = new Uri(actorIri.Value) }];
        return activity;
    }

    /// <summary>
    /// Builds a signed <see cref="HttpRequestMessage"/> for the given activity, signed as
    /// <paramref name="actorIri"/> (key <paramref name="key"/>), for POST to <paramref name="path"/>. The
    /// request is signed by running it through the client's <see cref="SigningHandler"/> over a capture
    /// handler, and the signed request (body + signature headers) is replayed through the plain
    /// <see cref="HttpClient"/>. Mirrors <see cref="CommunityOutboxPublishIntegrationTests.SignedRequest"/>.
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
    /// Builds a self-referential <see cref="IActorDocumentFetcher"/>: the instance's fetcher reaches its
    /// OWN actor/community documents (so it can resolve the community's signing key from its own
    /// <c>Group</c> document when validating a community-signed activity posted to its own inbox).
    /// </summary>
    private IActorDocumentFetcher BuildSelfFetcher(InMemoryPersistenceProvider persistence)
    {
        // A client signed as the instance actor (alice), whose transport routes to this server's own
        // TestServer (the self-fetch).
        var aliceKey = KeyPairGenerator.GenerateRsa(new Iri($"{_aliceIri.Value}#key-1"));
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(_aliceIri, aliceKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = _aliceIri, EnableRetry = false },
            new LazyHandler(() => _server!.CreateHandler()));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
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
            var body = request.Content is null
                ? []
                : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            // Capture BOTH request headers and content headers: the SigningHandler puts Date/Digest/
            // Content-Type as content headers, so capturing only request.Headers would drop them and the
            // replayed signature would fail to verify.
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
