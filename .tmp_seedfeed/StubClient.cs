namespace S36
{

using Iris.Client;
using Iris.Client.Collections;
using Iris.Client.Pipeline;
using Iris.Core;
using Iris.Core.Collections;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using CollectionPage = Iris.Core.Collections.CollectionPage;


    internal sealed class StubObjectFetcher(Iri targetIri, IObject? fetchedObject) : IActivityPubClient
    {
        private readonly Iri _targetIri = targetIri;
        private readonly IObject? _fetchedObject = fetchedObject;

        public int FetchCount { get; private set; }

        public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default)
        {
            if (objectId == _targetIri)
            {
                FetchCount++;
            }
            return Task.FromResult(_fetchedObject);
        }

        public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
            => Task.FromResult<Actor?>(null);

        public Task<NodeInfo?> GetNodeInfoAsync(Iri instanceBase, CancellationToken ct = default)
            => Task.FromResult<NodeInfo?>(null);

        public Task<LemmyPostScore?> GetLemmyPostScoreAsync(Iri postIri, CancellationToken ct = default)
            => Task.FromResult<LemmyPostScore?>(null);

        private static Task<DeliveryResult> StubDelivery()
            => Task.FromResult(new DeliveryResult(500, false, "stub"));

        public Task<DeliveryResult> DeliverAsync(Iri targetId, IObject activity, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> UndoFollowAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> RejectAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> RequestJoinAsync(Iri actorId, Iri communityIri, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> RequestLeaveAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> AcceptJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> RejectJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> SetManuallyApprovesMembersAsync(Iri communityIri, bool enabled, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> SetManuallyApprovesFollowersAsync(Iri actorIri, bool enabled, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> UnlikeAsync(Iri actorId, Iri originalLikeId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> DislikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> UndislikeAsync(Iri actorId, Iri originalDislikeId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> UnannounceAsync(Iri actorId, Iri originalAnnounceId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> UpdateActorAsync(Iri actorId, Actor updatedActor, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> UpdateNoteAsync(Iri actorId, Note updatedNote, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => StubDelivery();

        public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyAsync();

        public Task<DeliveryResult> UnblockAsync(Iri actorId, Iri originalBlockId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => StubDelivery();

        public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyAsync();

        public Task<DeliveryResult> UnflagAsync(Iri actorId, Iri originalFlagId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> CreateCommunityAsync(Iri actorId, string name, string displayName, string? description = null, CancellationToken ct = default)
            => StubDelivery();

        public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyAsync();

        public IAsyncEnumerable<IObjectOrLink> GetRelaysAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyAsync();

        public Task<DeliveryResult> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
            => StubDelivery();

        public Task<DeliveryResult> PostNoteAsync(Iri actorId, Note note, CancellationToken ct = default)
            => StubDelivery();

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
            => StubDelivery();

        public Task<DeliveryResult> PostReplyAsync(
            Iri actorId,
            Iri parentIri,
            string content,
            IEnumerable<Iri>? mentions = null,
            IEnumerable<Iri>? to = null,
            IEnumerable<string>? hashtags = null,
            Iri? conversationIri = null,
            CancellationToken ct = default)
            => StubDelivery();

        public IAsyncEnumerable<IObjectOrLink> GetRepliesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyAsync();

        public IAsyncEnumerable<IObjectOrLink> GetLikesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyAsync();

        public IAsyncEnumerable<IObjectOrLink> GetSharesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyAsync();

        public IAsyncEnumerable<IObjectOrLink> GetInboxItemsAsync(
            Iri actorId,
            ProxyCredentials credentials,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync();

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

        public IAsyncEnumerable<CollectionPage> GetCollectionAsync(Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyPagesAsync();

        public IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyAsync();

        public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(Iri communityId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyAsync();

        public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => EmptyAsync();

        public IAsyncEnumerable<IObjectOrLink> SearchAsync(Iri instanceBase, string? query = null, SearchOptions? options = null, CancellationToken ct = default)
            => EmptyAsync();

        private static async IAsyncEnumerable<IObjectOrLink> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

        private static async IAsyncEnumerable<CollectionPage> EmptyPagesAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose() { }
    }}