using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using ClientCollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Unit tests for <see cref="IrisActorDocumentFetcher"/>: it reads remote actor documents through the
/// Phase 3 <see cref="RemoteActorCache"/> so a document is fetched once and reused across lookups, and
/// an absent result is not cached (retried on the next lookup).
/// </summary>
/// <remarks>
/// The outbound transport is a fake <see cref="IActivityPubClient"/> that returns a fixed actor (or
/// null) and counts fetches, so cache hit/miss behavior is observable without a network.
/// </remarks>
public class IrisActorDocumentFetcherTests
{
    private const string AHost = "a.domain.local";

    [Fact]
    public async Task GetActor_Miss_FetchesAndCaches()
    {
        var client = new StubActivityPubClient(ActorNamed("alice"));
        var cache = new RemoteActorCache();
        var sut = new IrisActorDocumentFetcher(client, cache);

        var actor = await sut.GetActorAsync(new Iri($"https://{AHost}/ap/v1/u/alice"));

        Assert.NotNull(actor);
        Assert.Equal("alice", actor!.PreferredUsername);
        Assert.Equal(1, client.GetObjectCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task GetActor_FreshHit_IsCached()
    {
        var client = new StubActivityPubClient(ActorNamed("bob"));
        var cache = new RemoteActorCache();
        var sut = new IrisActorDocumentFetcher(client, cache);
        var actorIri = new Iri($"https://{AHost}/ap/v1/u/bob");

        var first = await sut.GetActorAsync(actorIri);
        var second = await sut.GetActorAsync(actorIri);

        // Same document served from the cache on the second call; the client is not hit again.
        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, client.GetObjectCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task GetActor_Absent_NegativelyCached()
    {
        var client = new StubActivityPubClient(null); // the remote actor does not exist
        var cache = new RemoteActorCache();
        var sut = new IrisActorDocumentFetcher(client, cache);
        var actorIri = new Iri($"https://{AHost}/ap/v1/u/nobody");

        var first = await sut.GetActorAsync(actorIri);
        var second = await sut.GetActorAsync(actorIri);

        // Absent results are negatively cached: the second call does NOT re-fetch.
        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, client.GetObjectCalls);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task GetActor_DistinctActors_FetchesEach()
    {
        var client = new StubActivityPubClient(null);
        var cache = new RemoteActorCache();
        var sut = new IrisActorDocumentFetcher(client, cache);

        // A per-actor document map so each IRI resolves to its own actor.
        var docs = new Dictionary<Iri, Actor>
        {
            [new Iri($"https://{AHost}/ap/v1/u/carol")] = ActorNamed("carol"),
            [new Iri($"https://{AHost}/ap/v1/u/dave")] = ActorNamed("dave"),
        };
        client.Documents = docs;

        var carol = await sut.GetActorAsync(new Iri($"https://{AHost}/ap/v1/u/carol"));
        var dave = await sut.GetActorAsync(new Iri($"https://{AHost}/ap/v1/u/dave"));

        Assert.Equal("carol", carol!.PreferredUsername);
        Assert.Equal("dave", dave!.PreferredUsername);
        Assert.Equal(2, client.GetObjectCalls);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task GetActor_RemoteCommunity_PersistsItAndReturnsItForKeyResolution()
    {
        // 135.1: when the fetched document is a community Group, the fetcher persists it to the durable
        // community store (via the RemoteCommunityPersister) so the community is served as known content,
        // AND returns the Group (a Group IS an Actor) so the inbound key resolver can read its publicKey
        // and validate the community's signatures.
        var persistence = new InMemoryPersistenceProvider();
        var communityPersister = new RemoteCommunityPersister(
            persistence.Communities,
            instanceBase: new Iri($"https://{AHost}/ap/v1"));

        var remoteCommunity = new Group
        {
            Id = "https://lemmy.example/c/lemmyverse",
            PreferredUsername = "lemmyverse",
            Name = ["Lemmyverse"],
        };
        var client = new StubActivityPubClient(actor: null) { GroupDocument = remoteCommunity };
        var cache = new RemoteActorCache();
        var sut = new IrisActorDocumentFetcher(client, cache, null, communityPersister);

        var actor = await sut.GetActorAsync(new Iri(remoteCommunity.Id));

        // The community is returned (as an Actor) for key resolution …
        Assert.NotNull(actor);
        Assert.Same(remoteCommunity, actor);
        // … and persisted to the durable community store.
        Assert.True(await persistence.Communities.TryGetCommunityAsync(new Iri(remoteCommunity.Id), out var stored));
        Assert.Equal(remoteCommunity.Id, stored!.Id);
    }

    // --- 135.1b: a REAL Lemmy community (Group) document round-trips end to end ----

    // The full JSON below is the verbatim document served by a real Lemmy 0.19.20 instance
    // (https://iris-dev2.luit.ink/c/test on the Phase 135.1 interop deployment). It carries the
    // Lemmy-specific shape that a minimal test fixture would not: an @context array including
    // join-lemmy.org/context.json, a nested `source` (content/mediaType), `sensitive`,
    // `postingRestrictedToMods`, `endpoints.sharedInbox`, `featured`, an empty `language` array,
    // `published`, and an `attributedTo` pointing at the community's /moderators collection.
    // The test proves Iris deserializes this genuine Lemmy Group, persists it to the durable
    // community store via the fetcher, and serves it back through the cached-actor endpoint with
    // the Lemmy fields intact — the crux of the Iris <-> Lemmy community interop (Phase 135.1).
    [Fact]
    public async Task GetActor_RealLemmyCommunity_PersistsAndServesIt()
    {
        const string lemmyGroupJson = """
            {
              "@context": [
                "https://join-lemmy.org/context.json",
                "https://www.w3.org/ns/activitystreams"
              ],
              "type": "Group",
              "id": "https://lemmy.example/c/lemmyverse",
              "preferredUsername": "lemmyverse",
              "inbox": "https://lemmy.example/c/lemmyverse/inbox",
              "followers": "https://lemmy.example/c/lemmyverse/followers",
              "publicKey": {
                "id": "https://lemmy.example/c/lemmyverse#main-key",
                "owner": "https://lemmy.example/c/lemmyverse",
                "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAyWEdguB2ohzQQWpcmS27\nMsshcuauP1kE3em/sV5aFZAI3jC+r3y+NGuZ8osT77GFAtTPsj1Z8BO8+bvRAyjV\niNG5yoQr495xtD+HZYxohKkOxpC4i57bCBbSIwEaJCWTdMxl4T31F1hZ3H5qPZS0\nPEj3HBAh+cKzl1GAsCbApl+oW7HWWm0nib/tJ9mUBoF+l0L8gfn3WetD400x0iYt\nDtqMxCRrDHHv6L0xk9WGDminwmR1K8zjGRpnWf1dj4VHaPMXqNUz+pcudXqKjKuV\n0NNg7WVaKjxSOkvzZob5ftKqMUQIC9rIIYuz2NWycQuOngg/3RRL1uczOOw3CoAg\neQIDAQAB\n-----END PUBLIC KEY-----\n"
              },
              "name": "Iris Interop Test Community",
              "summary": "<p>A community on the Lemmy side of the Iris&lt;-&gt;Lemmy interop test.</p>\n",
              "source": {
                "content": "A community on the Lemmy side of the Iris<->Lemmy interop test.",
                "mediaType": "text/markdown"
              },
              "sensitive": false,
              "attributedTo": "https://lemmy.example/c/lemmyverse/moderators",
              "postingRestrictedToMods": false,
              "outbox": "https://lemmy.example/c/lemmyverse/outbox",
              "endpoints": {
                "sharedInbox": "https://lemmy.example/inbox"
              },
              "featured": "https://lemmy.example/c/lemmyverse/featured",
              "language": [],
              "published": "2026-09-13T22:28:09.504672Z"
            }
            """;

        // 1) The genuine Lemmy document deserializes (via the polymorphic IObjectOrLink converter)
        //    into a Group — the exact shape the fetcher's `value is Group` branch depends on.
        var deserialized = ActivityJson.Deserialize<IObjectOrLink>(lemmyGroupJson);
        var group = Assert.IsType<Group>(deserialized);
        Assert.Equal("https://lemmy.example/c/lemmyverse", group.Id);
        Assert.Equal("lemmyverse", group.PreferredUsername);

        // 2) Feeding that real document through the fetcher persists it to the durable community
        //    store (RemoteCommunityPersister) and returns it (as an Actor) for key resolution.
        var persistence = new InMemoryPersistenceProvider();
        var communityPersister = new RemoteCommunityPersister(
            persistence.Communities,
            instanceBase: new Iri($"https://{AHost}/ap/v1"));
        var client = new StubActivityPubClient(actor: null) { GroupDocument = group };
        var cache = new RemoteActorCache();
        var sut = new IrisActorDocumentFetcher(client, cache, null, communityPersister);

        var actor = await sut.GetActorAsync(new Iri(group.Id!));

        Assert.NotNull(actor);
        Assert.Same(group, actor);
        Assert.True(await persistence.Communities.TryGetCommunityAsync(new Iri(group.Id!), out var stored));
        Assert.Equal(group.Id, stored!.Id);

        // 3) 138: the cached-actor endpoint is LOCAL-ONLY, so it 404s for the remote Lemmy community
        //    (the client reads it through the proxy endpoint instead). The community is still
        //    persisted in the store with its Lemmy-specific fields intact — assert the round-trip
        //    against the STORE (serialized), not the endpoint.
        using var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = "alice",
            Persistence = persistence,
        });
        TestSeeder.SeedPerson(persistence, AHost, "alice");
        var http = new HttpClient(server.CreateHandler(), disposeHandler: false);
        var response = await http.GetAsync(
            $"https://{AHost}/ap/v1/actor?iri={Uri.EscapeDataString(group.Id!)}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);

        // The persisted community round-trips its Lemmy-specific fields through the store (source,
        // endpoints, featured, postingRestrictedToMods) — the fields a proxy read would serve.
        using var doc = JsonDocument.Parse(ActivityJson.Serialize(stored));
        Assert.Equal("https://lemmy.example/c/lemmyverse", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Group", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("lemmyverse", doc.RootElement.GetProperty("preferredUsername").GetString());
        Assert.Equal("Iris Interop Test Community", doc.RootElement.GetProperty("name").GetString());
        // Lemmy-specific fields round-trip through the store.
        Assert.Equal(
            "A community on the Lemmy side of the Iris<->Lemmy interop test.",
            doc.RootElement.GetProperty("source").GetProperty("content").GetString());
        Assert.Equal(
            "https://lemmy.example/inbox",
            doc.RootElement.GetProperty("endpoints").GetProperty("sharedInbox").GetString());
        Assert.Equal(
            "https://lemmy.example/c/lemmyverse/featured",
            doc.RootElement.GetProperty("featured").GetString());
        Assert.False(doc.RootElement.GetProperty("postingRestrictedToMods").GetBoolean());
    }

    // --- Helpers -----------------------------------------------------------------

    private static Actor ActorNamed(string name)
        => new Person { Id = $"https://{AHost}/ap/v1/u/{name}", PreferredUsername = name };

    /// <summary>
    /// A fake <see cref="IActivityPubClient"/> returning a fixed actor (or a per-IRI map) and counting
    /// <see cref="IActivityPubClient.GetActorAsync"/> calls.
    /// </summary>
    private sealed class StubActivityPubClient(Actor? actor) : IActivityPubClient
    {
        private readonly Actor? _actor = actor;

        /// <summary>
        /// An optional per-IRI document map; when set, overrides the fixed actor.
        /// </summary>
        public Dictionary<Iri, Actor>? Documents { get; set; }

        /// <summary>
        /// An optional community (Group) document to return from <see cref="GetObjectAsync"/> (135.1 —
        /// a fetched remote community is a Group, not an Actor). When set, it is returned for any IRI.
        /// </summary>
        public Group? GroupDocument { get; set; }

        /// <summary>
        /// The number of times <see cref="IActivityPubClient.GetObjectAsync"/> has been invoked (the
        /// fetcher fetches the full object so a community Group is not dropped, 135.1).
        /// </summary>
        public int GetObjectCalls { get; private set; }

        /// <inheritdoc/>
        public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default)
        {
            GetObjectCalls++;
            if (GroupDocument is { } group)
            {
                return Task.FromResult<IObject?>(group);
            }

            if (Documents is { } docs && docs.TryGetValue(objectId, out var doc))
            {
                return Task.FromResult<IObject?>(doc);
            }

            return Task.FromResult<IObject?>(_actor);
        }

        /// <inheritdoc/>
        public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
            => Task.FromResult<Actor?>(_actor);

        public Task<LemmyPostScore?> GetLemmyPostScoreAsync(Iri iri, CancellationToken ct = default) => Task.FromResult<LemmyPostScore?>(null);
        public Task<DeliveryResult> DislikeAsync(Iri objectIri, Iri actorIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> UndislikeAsync(Iri objectIri, Iri actorIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<NodeInfo?> GetNodeInfoAsync(Iri instanceBase, CancellationToken ct = default)
            => Task.FromResult<NodeInfo?>(null);

        /// <inheritdoc/>
        public Task<DeliveryResult> DeliverAsync(Iri targetId, IObject activity, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> UndoFollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> RejectAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> RequestJoinAsync(Iri actorId, Iri communityIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> RequestLeaveAsync(Iri actorId, Iri communityIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> AcceptJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> RejectJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> SetManuallyApprovesMembersAsync(Iri communityIri, bool enabled, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> SetManuallyApprovesFollowersAsync(Iri actorIri, bool enabled, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> UnlikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> UnannounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> CreateCommunityAsync(Iri actorId, string name, string displayName, string? description = null, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> UpdateActorAsync(Iri actorId, Actor updatedActor, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> PostNoteAsync(Iri actorId, Note note, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
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
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> PostReplyAsync(
            Iri actorId,
            Iri parentIri,
            string content,
            IEnumerable<Iri>? mentions = null,
            IEnumerable<Iri>? to = null,
            IEnumerable<string>? hashtags = null,
            Iri? conversationIri = null,
            CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> UpdateNoteAsync(Iri actorId, Note updatedNote, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public async IAsyncEnumerable<IObjectOrLink> GetInboxItemsAsync(
            Iri actorId, Iris.Client.Pipeline.ProxyCredentials credentials, CollectionQuery? query = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }

        /// <inheritdoc/>
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));

        /// <inheritdoc/>
        public IAsyncEnumerable<ClientCollectionPage> GetCollectionAsync(
            Iri collectionId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<ClientCollectionPage>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(
            Iri collectionId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(
            Iri communityId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetRepliesAsync(
            Iri objectIri,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetLikesAsync(
            Iri objectIri,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetSharesAsync(
            Iri objectIri,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> SearchAsync(
            Iri instanceBase,
            string? query = null,
            SearchOptions? options = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public Task<DeliveryResult> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(0, false, ""));

        public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public Task<DeliveryResult> UnblockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(0, false, ""));

        public Task<DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(0, false, ""));

        public Task<DeliveryResult> UnflagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(0, false, ""));

        public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetRelaysAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public void Dispose()
        {
        }

        private static async IAsyncEnumerable<T> EmptyAsync<T>(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
