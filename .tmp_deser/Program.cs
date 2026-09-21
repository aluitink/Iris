using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Client.Collections;
using Iris.Client.Pipeline;
using Iris.Server.Stores;
using Iris.Server.Caching;
using Iris.Core.Collections;

using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Services;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;

var persistence = new InMemoryPersistenceProvider();
var host = "a.test";

// alice = local viewer; bob = remote followed actor
var aliceIri = $"https://{host}/ap/v1/u/alice";
var bobIri = $"https://b.test/ap/v1/u/bob";
await persistence.Actors.PutActorAsync(new Person { Id = aliceIri, Name = ["Alice"] });
await persistence.Follows.RecordFollowAsync(new Iri(aliceIri), new Iri(bobIri));

// alice's OWN local post (public, 2h ago)
var aliceNoteIri = $"https://{host}/n/alice1";
await persistence.Activities.AddToOutboxAsync(new Iri(aliceIri), new Create
{
    Id = "https://a.test/c/alice1",
    Actor = [new Link { Href = new Uri(aliceIri) }],
    Object = [new Note
    {
        Id = aliceNoteIri,
        Content = ["alice own post"],
        To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
        AttributedTo = [new Link { Href = new Uri(aliceIri) }],
    }],
    Published = DateTime.UtcNow.AddHours(-2),
});

// bob's remote posts
var bobNotes = new List<IObject>();
for (var i = 1; i <= 30; i++)
{
    var noteIri = "https://b.test/n/bob" + i;
    bobNotes.Add(new Note
    {
        Id = noteIri,
        Content = [$"bob post {i}"],
        To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
        AttributedTo = [new Link { Href = new Uri(bobIri) }],
    });
}

// OrderedCollectionPage documents for bob's outbox
string PageDoc(Iri pageIri, IEnumerable<IObjectOrLink> items, Iri? next) => ActivityJson.Serialize(new OrderedCollectionPage
{
    Id = pageIri.ToString(),
    PartOf = new Link { Href = new Uri("https://b.test/ap/v1/u/bob/outbox") },
    OrderedItems = items,
    Next = next is null ? null : new Link { Href = new Uri(next.ToString()!) },
});

var page1Iri = new Iri("https://b.test/ap/v1/u/bob/outbox?page=1");
var page2Iri = new Iri("https://b.test/ap/v1/u/bob/outbox?page=2");
var page1 = PageDoc(page1Iri, bobNotes.Take(20), page2Iri);
var page2 = PageDoc(page2Iri, bobNotes.Skip(20), null);

var collection = ActivityJson.Serialize(new Collection
{
    Id = "https://b.test/ap/v1/u/bob/outbox",
    First = new Link { Href = new Uri(page1Iri.Value) },
});

var bobActor = ActivityJson.Serialize(new Person
{
    Id = bobIri,
    Name = ["Bob"],
    Outbox = new Link { Href = new Uri("https://b.test/ap/v1/u/bob/outbox") },
});

foreach (var n in bobNotes)
{
    await persistence.Objects.PutObjectAsync((IObject)n);
}

var client = new StubClient(
    (new Iri("https://b.test/ap/v1/u/bob/outbox"), collection),
    (page1Iri, page1),
    (page2Iri, page2),
    (new Iri(bobIri), bobActor));

var actorDocs = new IrisActorDocumentFetcher(client, new RemoteActorCache());
var localActors = new LocalOnlyResolver(persistence);
var service = new FeedService(persistence, localActors, actorDocs, client, Options.Create(new FeedOptions()), null, NullLogger<FeedService>.Instance);

Iri AliceIri() => new Iri(aliceIri);

// --- manual merge trace ---
var follows = await persistence.Follows.GetFollowingAsync(AliceIri());
Console.WriteLine($"follows={follows.Count}");
var ownItems = await persistence.Activities.GetOutboxAsync(AliceIri());
Console.WriteLine($"own outbox items={ownItems.Count}");
var delivered = await persistence.Objects.ListByActorAsync(new Iri(bobIri));
Console.WriteLine($"delivered bob objects={delivered.Count}");
var modBlocks = await persistence.Moderation.GetBlocksAsync(AliceIri());
var modMutes = await persistence.Moderation.GetMutesAsync(AliceIri());
Console.WriteLine($"blocks={modBlocks.Count} mutes={modMutes.Count}");


// 1) full feed (no visibility filter)
var feed = await service.GetFeedAsync(AliceIri());
Console.WriteLine($"unfiltered total={feed.Count}");
foreach (var item in feed.Take(4))
{
    var obj = item switch
    {
        Create cc => cc.Object?.FirstOrDefault() as IObject,
        IObject o => o,
        _ => null,
    };
    var id = item is IObject io ? io.Id : "?";
    var tos = string.Join(",", (obj?.To ?? []).Select(t => { var tl = (IObjectOrLink?)t; return tl?.ResolveObjectIri()?.Value ?? "?"; }));
    var inReplyObj = obj?.InReplyTo is { } r ? (IObjectOrLink?)r : null;
    var inReply = inReplyObj?.ResolveObjectIri()?.Value ?? "null";
    Console.WriteLine($"  [{item.GetType().Name}] id={id} to={tos} inReplyTo={inReply}");
}




sealed class LocalOnlyResolver(IPersistenceProvider p) : ILocalActorResolver
{
    public Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default)
        => Task.FromResult(p.Actors.TryGetActorAsync(actorIri, out _, ct).GetAwaiter().GetResult());
}

sealed class StubClient(params (Iri Iri, string Json)[] docs) : IActivityPubClient
{
    private readonly Dictionary<Iri, string> _docs = docs.ToDictionary(d => d.Iri, d => d.Json);

    public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default)
        => Task.FromResult<IObject?>(null);

    public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
    {
        if (_docs.TryGetValue(actorId, out var json) && ActivityJson.Deserialize<Actor>(json) is { } a)
            return Task.FromResult<Actor?>(a);
        return Task.FromResult<Actor?>(null);
    }

    public Task<NodeInfo?> GetNodeInfoAsync(Iri instanceBase, CancellationToken ct = default)
        => Task.FromResult<NodeInfo?>(null);

    public Task<LemmyPostScore?> GetLemmyPostScoreAsync(Iri postIri, CancellationToken ct = default)
        => Task.FromResult<LemmyPostScore?>(null);

    public Task<DeliveryResult> DeliverAsync(Iri targetId, IObject activity, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));

    public Task<DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> UndoFollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> RejectAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> RequestJoinAsync(Iri actorId, Iri communityIri, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> RequestLeaveAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> AcceptJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> RejectJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> SetManuallyApprovesMembersAsync(Iri communityIri, bool enabled, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> SetManuallyApprovesFollowersAsync(Iri actorIri, bool enabled, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> UnlikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> DislikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> UndislikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> UnannounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> UpdateActorAsync(Iri actorId, Actor updatedActor, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> UpdateNoteAsync(Iri actorId, Note updatedNote, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public Task<DeliveryResult> UnblockAsync(Iri actorId, Iri originalBlockId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> UnflagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> CreateCommunityAsync(Iri actorId, string name, string displayName, string? summary = null, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public IAsyncEnumerable<IObjectOrLink> GetRelaysAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public Task<DeliveryResult> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> PostNoteAsync(Iri actorId, Note note, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> PostQuestionAsync(Iri actorId, string content, IEnumerable<string>? audience = null, DateTime? endsAt = null, bool multiple = false, IEnumerable<Iri>? acceptedAnsweringSpeakableActivityTypes = null, IEnumerable<Iri>? acceptedAnsweringSpeakableRepresentativeType = null, IEnumerable<Iri>? acceptedAnsweringTypes = null, IEnumerable<string>? answers = null, Func<string, string?>? moveId = null, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public Task<DeliveryResult> PostReplyAsync(Iri actorId, Iri parentIri, string content, IEnumerable<Iri>? cc = null, IEnumerable<Iri>? to = null, IEnumerable<string>? audience = null, Iri? conversationIri = null, CancellationToken ct = default)
        => Task.FromResult(new DeliveryResult(202, true, ""));
    public IAsyncEnumerable<IObjectOrLink> GetRepliesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public IAsyncEnumerable<IObjectOrLink> GetLikesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public IAsyncEnumerable<IObjectOrLink> GetSharesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public IAsyncEnumerable<IObjectOrLink> GetInboxItemsAsync(Iri actorId, ProxyCredentials credentials, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        var iri = request.RequestUri is { } uri ? new Iri(uri) : default;
        return Task.FromResult(_docs.TryGetValue(iri, out var json)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) }
            : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) });
    }
    public async IAsyncEnumerable<Iris.Core.Collections.CollectionPage> GetCollectionAsync(Iri collectionId, CollectionQuery? query = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_docs.TryGetValue(collectionId, out var collectionJson)) yield break;
        var coll = ActivityJson.Deserialize<Collection>(collectionJson);
        Iri? pageIri = coll?.First?.ResolveCollectionIri();
        while (pageIri is { } current)
        {
            if (!_docs.TryGetValue(current, out var pageJson)) yield break;
            var pageDoc = ActivityJson.Deserialize<IObjectOrLink>(pageJson);
            if (pageDoc is not IObject obj) yield break;
            var page = Iris.Core.Collections.CollectionPageFactory.FromOrderedCollectionPage(obj);
            if (page is null) yield break;
            yield return page;
            pageIri = page.NextPage;
            ct.ThrowIfCancellationRequested();
        }
    }
    public IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(Iri communityId, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public IAsyncEnumerable<IObjectOrLink> SearchAsync(Iri instanceBase, string? query = null, SearchOptions? options = null, CancellationToken ct = default)
        => Empty<IObjectOrLink>();
    public void Dispose() { }

    private static async IAsyncEnumerable<T> Empty<T>() { await Task.Yield(); yield break; }
}
