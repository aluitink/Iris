using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 136.10 integration test: cross-instance moderation trust-boundary behavior. Pins the
/// "blocked content is not reintroduced via new delivery" property: when a local actor (alice on A)
/// blocks a remote actor (bob on B), bob's <em>new</em> content (published after the block) federates
/// B→A, is <em>stored</em> on A (in the activity store, fetchable by direct IRI), but is <em>excluded
/// from alice's feed</em> (the <see cref="Iris.Server.Services.FeedService"/> applies the block edge on
/// the reader's side — the authoritative guarantee). The feed filtering is the trust-boundary
/// enforcement; the delivery suppression (on the deliverer's side) is a local optimization that does
/// not apply cross-instance (the deliverer B does not have alice's block edge — it is on A).
/// </summary>
/// <remarks>
/// Topology: instance A (block-content-a.domain.local, <c>alice</c>) and instance B
/// (block-content-b.domain.local, <c>bob</c>). alice (A) follows bob (B). bob (B) has an existing post
/// m1 (so alice's feed shows m1 before the block). alice blocks bob via a signed outbox POST to A; A
/// records the alice → bob edge. bob then posts <em>new</em> content m2 (after the block); B delivers
/// m2 to bob's followers (alice on A) — B's <see cref="Iris.Server.Delivery.DeliveryService"/> checks
/// its <em>local</em> moderation store for the block edge (it does not have alice → bob; that edge is on
/// A), so no suppression occurs; m2 federates to A and is stored. Asserted on A: m2 is stored (in the
/// activity store, fetchable by direct IRI) but is <em>not</em> in alice's feed (the feed filtering
/// excludes it); m1 (the pre-block post) is also not in alice's feed (the block excludes all of bob's
/// content).
/// </remarks>
[Collection("CrossInstanceBlockedContent")]
public sealed class CrossInstanceBlockedContentIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "block-content-a.domain.local";
    internal const string BHost = "block-content-b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";

    private readonly CrossInstanceBlockedContentSharedHost _fixture;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private KeyPair _aliceKey;
    private KeyPair _bobKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;

    public CrossInstanceBlockedContentIntegrationTests(CrossInstanceBlockedContentSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _aliceKey = null!;
        _bobKey = null!;
        _aHttp = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
        _bHttp = new HttpClient(fixture.ServerB.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence);

        _aPersistence.Keys.TryGetKey(new Iri($"{_aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        _bPersistence.Keys.TryGetKey(new Iri($"{_bobActorIri.Value}#key-1"), out var bobKey);
        _bobKey = (KeyPair)bobKey!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
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

    // --- Blocked content is not reintroduced via new delivery (the trust-boundary guarantee) ----
    //
    // alice (A) follows bob (B). alice blocks bob (A records the alice → bob edge). bob posts new
    // content m2 (after the block); B delivers m2 to alice (no suppression — B's local store does not
    // have the alice → bob edge; it is on A); m2 federates to A and is stored. Asserted on A: m2 is
    // stored (in the object store, fetchable by direct IRI); the block edge exists (alice → bob); and
    // the FeedService (constructed with A's persistence + moderation store) excludes bob's content from
    // alice's feed (the trust-boundary guarantee). The feed filtering is the authoritative guarantee;
    // the delivery suppression is a local optimization that does not apply cross-instance.

    [Fact]
    public async Task BlockedActorNewContent_IsStoredButExcludedFromFeed()
    {
        // Setup: alice (A) follows bob (B). The follow edge is recorded on B (bob's home instance
        // owns his follower set — the propagation target set for his Create federation) and on A
        // (alice's following list, used by A's FeedService).
        await _bPersistence.Follows.RecordFollowAsync(_aliceActorIri, _bobActorIri);
        await _aPersistence.Follows.RecordFollowAsync(_aliceActorIri, _bobActorIri);

        // bob (B) posts NEW content m2. Pre-seed the note in B's object store (so the Create handler
        // can store the embedded object), then publish the Create through B's outbox.
        const string m2Iri = $"https://{BHost}/ap/v1/u/{Bob}/notes/m2";
        await _bPersistence.Objects.PutObjectAsync(new Note
        {
            Id = m2Iri,
            Content = ["bob new post after block"],
            AttributedTo = [new Link { Href = new Uri(_bobActorIri.Value) }],
        });
        var m2Create = new Create
        {
            Actor = [new Link { Href = new Uri(_bobActorIri.Value) }],
            Object = [new Note { Id = m2Iri, Content = ["bob new post after block"] }],
        };
        using var m2Request = SignedRequest(_bobActorIri, _bobKey, m2Create, $"/ap/v1/u/{Bob}/outbox");
        using var m2Response = await _bHttp.SendAsync(m2Request);
        Assert.Equal(HttpStatusCode.Accepted, m2Response.StatusCode);

        // m2 federates B→A (B delivers to bob's followers, which includes alice on A). B's
        // DeliveryService checks its LOCAL moderation store for the block edge (it does not have
        // alice → bob; the block has not been published yet), so no suppression occurs. m2 is stored
        // on A (in the object store, fetchable by direct IRI).
        await WaitForAsync(
            async () => await _aPersistence.Objects.TryGetObjectAsync(new Iri(m2Iri), out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _aPersistence.Objects.TryGetObjectAsync(new Iri(m2Iri), out _),
            "bob's new post m2 should be stored on A (it federated B→A and was accepted).");

        // alice (A) blocks bob (B) via a signed outbox POST to A.
        var block = BuildBlock(_aliceActorIri, _bobActorIri);
        using var blockRequest = SignedRequest(_aliceActorIri, _aliceKey, block, $"/ap/v1/u/{Alice}/outbox");
        using var blockResponse = await _aHttp.SendAsync(blockRequest);
        Assert.Equal(HttpStatusCode.Accepted, blockResponse.StatusCode);

        // A recorded the alice → bob edge.
        Assert.True(
            await _aPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            "A should record the alice → bob block edge on publish.");

        // THE KEY ASSERTION: the FeedService (constructed with A's persistence + moderation store)
        // excludes bob's content from alice's feed (the trust-boundary guarantee). Even though m2 was
        // delivered and stored, it is not shown to alice because A has the alice → bob block edge.
        var feedService = BuildFeedService(_aPersistence);
        var feed = await feedService.GetFeedAsync(_aliceActorIri);
        Assert.DoesNotContain(
            feed,
            HasObjectIriContaining(m2Iri));
    }

    /// <summary>
    /// Builds a <see cref="Iris.Server.Services.FeedService"/> with the given persistence provider (its
    /// <see cref="Iris.Server.Stores.IModerationStore"/> is used for block/mute filtering). The
    /// <see cref="Iris.Client.IActivityPubClient"/> and <see cref="IActorDocumentFetcher"/> are stubs
    /// (the test only exercises the local moderation filtering, not the remote outbox walk).
    /// </summary>
    private static Iris.Server.Services.FeedService BuildFeedService(InMemoryPersistenceProvider persistence)
    {
        var localActors = new LocalOnlyResolver(persistence);
        var actorDocs = new StubActorDocumentFetcher();
        var client = new StubClient();
        var options = Microsoft.Extensions.Options.Options.Create(new Iris.Server.Services.FeedOptions());
        return new Iris.Server.Services.FeedService(
            persistence,
            localActors,
            actorDocs,
            client,
            options,
            persistence.Moderation);
    }

    // --- Reverse-direction block: bob (B) blocks alice (A); the block federates B→A ----
    //
    // bob (B) blocks alice (A) via a signed outbox POST to B. B records the bob → alice edge. The
    // Block federates B→A; A's BlockActivityHandler records the bob → alice edge on A (the remote
    // blocker of a local actor — the inverse query: alice's blockers include bob). Asserted on A: the
    // bob → alice edge is recorded (A's moderation store), and alice's blockers include bob (the
    // inverse index).

    [Fact]
    public async Task ReverseDirectionBlock_BlocksLocalActor_EdgeRecordedOnLocalInstance()
    {
        // bob (B) blocks alice (A) via a signed outbox POST to B.
        var block = BuildBlock(_bobActorIri, _aliceActorIri);
        using var blockRequest = SignedRequest(_bobActorIri, _bobKey, block, $"/ap/v1/u/{Bob}/outbox");
        using var blockResponse = await _bHttp.SendAsync(blockRequest);
        Assert.Equal(HttpStatusCode.Accepted, blockResponse.StatusCode);

        // B recorded the bob → alice edge (B's local edge on publish).
        Assert.True(
            await _bPersistence.Moderation.IsBlockedAsync(_bobActorIri, _aliceActorIri),
            "B should record its own bob → alice block edge on publish.");

        // The Block federates B→A; A's BlockActivityHandler records the bob → alice edge on A (the
        // remote blocker of a local actor).
        await WaitForAsync(
            async () => await _aPersistence.Moderation.IsBlockedAsync(_bobActorIri, _aliceActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _aPersistence.Moderation.IsBlockedAsync(_bobActorIri, _aliceActorIri),
            "A should record the bob → alice block edge after B delivered the signed Block.");

        // The inverse index agrees: alice's blockers include bob (bob has blocked alice).
        Assert.Contains(
            _bobActorIri,
            await _aPersistence.Moderation.GetBlockersAsync(_aliceActorIri));
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// A <see cref="Predicate{IObjectOrLink}"/> that matches feed items whose object IRI contains the
    /// given <paramref name="iriFragment"/>.
    /// </summary>
    private static Predicate<IObjectOrLink> HasObjectIriContaining(string iriFragment)
        => item => item is IObject { Id: { Length: > 0 } id }
            && id.ToString().Contains(iriFragment, StringComparison.Ordinal);

    /// <summary>
    /// Reads alice's feed over the wire and returns true when a content object with the given
    /// <paramref name="noteIri"/> is present in the feed's items.
    /// </summary>
    private static async Task<bool> FeedContainsNoteAsync(HttpClient http, string noteIri)
    {
        var response = await http.GetAsync($"https://{AHost}/ap/v1/u/{Alice}/feed");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return body.Contains(noteIri, StringComparison.Ordinal);
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
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on the
    /// author's outbox. Uses the client pipeline (via a capture handler) to produce a correctly signed
    /// request, then replays the signed headers onto a fresh request for delivery to the author's
    /// instance's TestServer.
    /// </summary>
    private HttpRequestMessage SignedRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var host = actorIri.Value.Contains(AHost) ? AHost : BHost;
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClient(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}")
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}")
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

    /// <summary>
    /// Builds an <see cref="IdentityKeys"/> for the given key and actor IRI (the signing infrastructure
    /// for the host's outbox-publish path).
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

    /// <summary>
    /// A minimal <see cref="ILocalActorResolver"/> that reports only the given persistence provider's
    /// local actors as local (all others are remote).
    /// </summary>
    private sealed class LocalOnlyResolver(IPersistenceProvider persistence) : ILocalActorResolver
    {
        public async Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default)
            => await persistence.Actors.TryGetActorAsync(actorIri, out _, ct);
    }

    /// <summary>
    /// A stub <see cref="IActorDocumentFetcher"/> that returns null (no remote actor documents are
    /// fetched — the test only exercises the local moderation filtering, not the remote outbox walk).
    /// </summary>
    private sealed class StubActorDocumentFetcher : IActorDocumentFetcher
    {
        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => Task.FromResult<Actor?>(null);
    }

    /// <summary>
    /// A stub <see cref="IActivityPubClient"/> that returns empty collections (the remote outbox walk
    /// yields nothing — the test only exercises the local moderation filtering, not the remote outbox
    /// fetch). All write methods throw <see cref="NotSupportedException"/> (they are not called by the
    /// <see cref="Iris.Server.Services.FeedService"/> in this test).
    /// </summary>
    private sealed class StubClient : IActivityPubClient
    {
        public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<NodeInfo?> GetNodeInfoAsync(Iri instanceBase, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> DeliverAsync(Iri targetId, IObject activity, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> UndoFollowAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> RejectAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> RequestJoinAsync(Iri actorId, Iri communityIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> RequestLeaveAsync(Iri actorId, Iri communityIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> AcceptJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> RejectJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> SetManuallyApprovesMembersAsync(Iri communityIri, bool enabled, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> SetManuallyApprovesFollowersAsync(Iri actorIri, bool enabled, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> UnlikeAsync(Iri actorId, Iri originalLikeId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> UnannounceAsync(Iri actorId, Iri originalAnnounceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> UpdateActorAsync(Iri actorId, Actor updatedActor, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LemmyPostScore?> GetLemmyPostScoreAsync(Iri iri, CancellationToken ct = default) => Task.FromResult<LemmyPostScore?>(null);
        public Task<DeliveryResult> DislikeAsync(Iri objectIri, Iri actorIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> UndislikeAsync(Iri objectIri, Iri actorIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> UpdateNoteAsync(Iri actorId, Note updatedNote, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(
            Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptySequence();

        public Task<DeliveryResult> UnblockAsync(Iri actorId, Iri originalBlockId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(
            Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptySequence();

        public Task<DeliveryResult> UnflagAsync(Iri actorId, Iri originalFlagId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> CreateCommunityAsync(
            Iri actorId, string name, string displayName, string? description = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(
            Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptySequence();

        public IAsyncEnumerable<IObjectOrLink> GetRelaysAsync(
            Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptySequence();

        public Task<DeliveryResult> PostNoteAsync(
            Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> PostNoteAsync(
            Iri actorId, Note note, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> PostQuestionAsync(
            Iri actorId,
            string content,
            IEnumerable<string> options,
            DateTime? endsAt = null,
            bool multiple = false,
            IEnumerable<Iri>? to = null,
            IEnumerable<Iri>? cc = null,
            IEnumerable<Iri>? mentions = null,
            IEnumerable<string>? hashtags = null,
            Func<string, string?>? hashtagHrefFactory = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> PostReplyAsync(
            Iri actorId,
            Iri parentIri,
            string content,
            IEnumerable<Iri>? mentions = null,
            IEnumerable<Iri>? to = null,
            IEnumerable<string>? hashtags = null,
            Iri? conversationIri = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetRepliesAsync(
            Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptySequence();

        public IAsyncEnumerable<IObjectOrLink> GetLikesAsync(
            Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptySequence();

        public IAsyncEnumerable<IObjectOrLink> GetSharesAsync(
            Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptySequence();

        public IAsyncEnumerable<IObjectOrLink> GetInboxItemsAsync(
            Iri actorId,
            Iris.Client.Pipeline.ProxyCredentials credentials,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptySequence();

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<Iris.Core.Collections.CollectionPage> GetCollectionAsync(
            Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyPages();

        public IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(
            Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptySequence();

        public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(
            Iri communityId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptySequence();

        public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(
            Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptySequence();

        public IAsyncEnumerable<IObjectOrLink> SearchAsync(
            Iri instanceBase,
            string? query = null,
            SearchOptions? options = null,
            CancellationToken ct = default)
            => EmptySequence();

        private static async IAsyncEnumerable<IObjectOrLink> EmptySequence()
        {
            await Task.CompletedTask;
            yield break;
        }

        private static async IAsyncEnumerable<Iris.Core.Collections.CollectionPage> EmptyPages()
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose() { }
    }
}

/// <summary>
/// Shared two-host fixture for <see cref="CrossInstanceBlockedContentIntegrationTests"/> (A:
/// block-content-a.domain.local alice, B: block-content-b.domain.local bob). Seeds alice + bob with
/// keys ONCE; wires cross-wired delivery + routing fetchers via <see cref="SharedHostFixture.ServerRefFor"/>.
/// </summary>
public sealed class CrossInstanceBlockedContentSharedHost : SharedTwoHostFixture
{
    public CrossInstanceBlockedContentSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, CrossInstanceBlockedContentIntegrationTests.AHost, CrossInstanceBlockedContentIntegrationTests.Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, CrossInstanceBlockedContentIntegrationTests.BHost, CrossInstanceBlockedContentIntegrationTests.Bob);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var optionsA = new ActivityPubHostOptions
        {
            Host = CrossInstanceBlockedContentIntegrationTests.AHost,
            Handle = CrossInstanceBlockedContentIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = CrossInstanceBlockedContentIntegrationTests.BuildIdentity(aSeeded.Key, aSeeded.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = new RoutingFetcher(
                CrossInstanceBlockedContentIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                CrossInstanceBlockedContentIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = CrossInstanceBlockedContentIntegrationTests.BHost,
            Handle = CrossInstanceBlockedContentIntegrationTests.Bob,
            Persistence = bPersistence,
            IdentityKeys = CrossInstanceBlockedContentIntegrationTests.BuildIdentity(bSeeded.Key, bSeeded.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
            Fetcher = new RoutingFetcher(
                CrossInstanceBlockedContentIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                CrossInstanceBlockedContentIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                bSeeded.Key, bSeeded.ActorIri),
        };

        return (optionsA, optionsB);
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents based
    /// on the actor IRI's host (A's fetcher needs to reach A and B; B's fetcher needs to reach A and B).
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
    }
}

/// <summary>
/// xunit collection definition for the cross-instance-blocked-content shared two-host fixture.
/// </summary>
[CollectionDefinition("CrossInstanceBlockedContent")]
public sealed class CrossInstanceBlockedContentCollection : ICollectionFixture<CrossInstanceBlockedContentSharedHost>
{
}
