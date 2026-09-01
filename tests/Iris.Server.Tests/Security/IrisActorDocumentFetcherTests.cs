using Iris.Client;
using Iris.Core;
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
        Assert.Equal(1, client.GetActorCalls);
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
        Assert.Equal(1, client.GetActorCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task GetActor_Absent_IsNotCached()
    {
        var client = new StubActivityPubClient(null); // the remote actor does not exist
        var cache = new RemoteActorCache();
        var sut = new IrisActorDocumentFetcher(client, cache);
        var actorIri = new Iri($"https://{AHost}/ap/v1/u/nobody");

        var first = await sut.GetActorAsync(actorIri);
        var second = await sut.GetActorAsync(actorIri);

        // Absent results are never cached, so the second call retries the fetch.
        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, client.GetActorCalls);
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
        Assert.Equal(2, client.GetActorCalls);
        Assert.Equal(2, cache.Count);
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
        /// The number of times <see cref="IActivityPubClient.GetActorAsync"/> has been invoked.
        /// </summary>
        public int GetActorCalls { get; private set; }

        /// <inheritdoc/>
        public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default)
            => Task.FromResult<IObject?>(null);

        /// <inheritdoc/>
        public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
        {
            GetActorCalls++;
            if (Documents is { } docs && docs.TryGetValue(actorId, out var doc))
            {
                return Task.FromResult<Actor?>(doc);
            }

            return Task.FromResult(_actor);
        }

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
        public Task<DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        /// <inheritdoc/>
        public Task<DeliveryResult> PostReplyAsync(
            Iri actorId,
            Iri parentIri,
            string content,
            IEnumerable<Iri>? mentions = null,
            IEnumerable<Iri>? to = null,
            CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

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
