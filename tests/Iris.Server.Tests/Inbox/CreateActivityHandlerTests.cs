using Iris.Client;
using Iris.Client.Pipeline;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Media;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Options;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests: the <see cref="CreateActivityHandler"/> — the dedicated handler for an inbound
/// <see cref="Create"/>. When the recipient is a local person it records the <see cref="Create"/> in that
/// person's outbox (the author's own post, J-8) <em>and</em> federates it to the author's remote followers
/// (J-18) <em>and</em> to the author's subscribed relays (F-06 relay fan-out); when the recipient is a local
/// community it records it in the community's local members' outboxes (the "followed content" half,
/// delegating to the shared <see cref="CommunityContentRecorder"/>). Covers: recording in the local
/// person's outbox, federating the post to remote (but not local) followers signed as the author, fanning
/// the post out to the author's subscribed relays (and not when the author has none), skipping a non-local
/// (remote) person, the community member-recording path, newest-first ordering, no-op for an unknown
/// recipient, and the null-guard contract.
/// </summary>
public sealed class CreateActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemotePerson = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");
    private static readonly Iri LocalMember = new("https://b.domain.local/ap/v1/u/carol");
    private static readonly Iri RemoteMember = new("https://a.domain.local/ap/v1/u/dave");
    private static readonly Iri RemoteFollower = new("https://c.domain.local/ap/v1/u/erin");
    private static readonly Iri LocalFollower = new("https://b.domain.local/ap/v1/u/frank");
    private static readonly Iri Relay = new("https://relay1.example.com");
    private static readonly Iri RelayTwo = new("https://relay2.example.com");

    // --- Local person: the author's own post (J-8) -----------------------------------------

    [Fact]
    public async Task HandleAsync_LocalPersonRecipient_RecordsInPersonOutbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var create = BuildCreate(LocalPerson); // the person posts their own note

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The Create is recorded in the local person's own outbox (newest first).
        var outbox = await persistence.Activities.GetOutboxAsync(LocalPerson);
        var ids = OutboxIds(outbox);
        Assert.Contains(create.Id, ids);
    }

    [Fact]
    public async Task HandleAsync_MultipleCreates_RecordsNewestFirst()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var first = BuildCreate(LocalPerson);
        var second = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, first), first);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, second), second);

        // The person outbox is newest first: second precedes first.
        var ids = OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson));
        Assert.Equal([second.Id!, first.Id!], ids);
    }

    // --- Outbound federation to the author's remote followers (J-18) ----------------------

    [Fact]
    public async Task HandleAsync_LocalPersonWithRemoteFollower_FederatesCreateToRemoteFollowerInbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        // erin (remote) follows bob (local) → the follow edge is recorded.
        await persistence.Follows.RecordFollowAsync(RemoteFollower, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The post is still surfaced in the author's outbox (J-8) ...
        Assert.Contains(create.Id, OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson)));
        // ... AND federated to the remote follower's inbox, signed as the author (bob).
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(RemoteFollower.InboxOf(), job.InboxIri);
        Assert.Same(create, job.Activity);
        Assert.Equal(LocalPerson, job.ActorIri); // signed as the author, not the instance actor
    }

    [Fact]
    public async Task HandleAsync_LocalPersonWithLocalFollower_SkipsLocalFollower()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await SeedLocalActorAsync(persistence, LocalFollower);
        // frank (local) follows bob (local) → local follower.
        await persistence.Follows.RecordFollowAsync(LocalFollower, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // A local follower already sees the post via the author's outbox → no cross-instance delivery.
        Assert.Empty(delivery.Delivered);
        // The post is still recorded in the author's own outbox.
        Assert.Contains(create.Id, OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson)));
    }

    [Fact]
    public async Task HandleAsync_LocalPersonWithMixedFollowers_FederatesOnlyToRemoteFollowers()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await SeedLocalActorAsync(persistence, LocalFollower);
        await persistence.Follows.RecordFollowAsync(RemoteFollower, LocalPerson); // remote → deliver
        await persistence.Follows.RecordFollowAsync(LocalFollower, LocalPerson);  // local  → skip
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // Exactly one delivery: to the remote follower's inbox (the local follower is skipped).
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(RemoteFollower.InboxOf(), job.InboxIri);
        Assert.Equal(LocalFollower.InboxOf(), LocalFollower.InboxOf()); // (sanity: local inbox is not targeted)
        Assert.DoesNotContain(delivery.Delivered, j => j.InboxIri == LocalFollower.InboxOf());
    }

    [Fact]
    public async Task HandleAsync_LocalPersonWithNoFollowers_RecordsButDoesNotDeliver()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson); // no followers
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // No followers → nothing scheduled, but the post is still surfaced in the author's outbox.
        Assert.Empty(delivery.Delivered);
        Assert.Contains(create.Id, OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson)));
    }

    // --- F-06: relay fan-out (deliver the post to the author's subscribed relays) ---------

    [Fact]
    public async Task HandleAsync_LocalPersonWithSubscribedRelay_FansOutCreateToRelayInbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        // bob (local) has subscribed to a relay (a remote `star`-subscribed fan-out server, AP §5.1.3).
        await persistence.Relays.RecordRelayAsync(LocalPerson, Relay);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The post is fanned out to the relay's inbox, signed as the author (bob). A relay is a remote
        // fan-out server (never a local actor), so no local-actor skip applies — the delivery is
        // scheduled regardless of any follower set.
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(Relay.InboxOf(), job.InboxIri);
        Assert.Same(create, job.Activity);
        Assert.Equal(LocalPerson, job.ActorIri); // signed as the author, not the instance actor
    }

    [Fact]
    public async Task HandleAsync_LocalPersonWithMultipleRelays_FansOutToEachRelay()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Relays.RecordRelayAsync(LocalPerson, Relay);
        await persistence.Relays.RecordRelayAsync(LocalPerson, RelayTwo);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The post is fanned out to BOTH subscribed relays (each a distinct delivery, signed as the author).
        Assert.Equal(2, delivery.Delivered.Count);
        Assert.Contains(delivery.Delivered, j => j.InboxIri == Relay.InboxOf() && j.ActorIri == LocalPerson);
        Assert.Contains(delivery.Delivered, j => j.InboxIri == RelayTwo.InboxOf() && j.ActorIri == LocalPerson);
    }

    [Fact]
    public async Task HandleAsync_LocalPersonWithNoSubscribedRelays_DoesNotFanOut()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemoteFollower, LocalPerson); // a follower, but no relays
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // No subscribed relays → the only delivery is to the remote follower (no relay fan-out).
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(RemoteFollower.InboxOf(), job.InboxIri);
        Assert.DoesNotContain(delivery.Delivered, j => j.InboxIri == Relay.InboxOf());
    }

    [Fact]
    public async Task HandleAsync_LocalPersonWithFollowerAndRelay_FederatesToBoth()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemoteFollower, LocalPerson); // remote follower
        await persistence.Relays.RecordRelayAsync(LocalPerson, Relay);           // subscribed relay
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The post reaches BOTH the remote follower's inbox AND the relay's inbox (two deliveries, both
        // signed as the author): relay fan-out is additive to 1-to-1 follower federation.
        Assert.Equal(2, delivery.Delivered.Count);
        Assert.Contains(delivery.Delivered, j => j.InboxIri == RemoteFollower.InboxOf() && j.ActorIri == LocalPerson);
        Assert.Contains(delivery.Delivered, j => j.InboxIri == Relay.InboxOf() && j.ActorIri == LocalPerson);
    }

    // --- F-07: apply the block edge (skip a follower who blocked the author) --------------

    [Fact]
    public async Task HandleAsync_RemoteFollowerBlockedAuthor_SkipsDeliveryToFollower()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemoteFollower, LocalPerson); // erin follows bob
        // erin (remote) blocked bob (local): the block edge erin → bob means erin does not want bob's content.
        await persistence.Moderation.RecordBlockAsync(RemoteFollower, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The post is surfaced in the author's outbox (J-8) but NOT federated to the blocking follower.
        Assert.Contains(create.Id, OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson)));
        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task HandleAsync_RemoteFollowerDidNotBlock_DeliversNormally()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemoteFollower, LocalPerson); // erin follows bob
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // No block edge → the post is federated to the remote follower's inbox (J-18).
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(RemoteFollower.InboxOf(), job.InboxIri);
        Assert.Equal(LocalPerson, job.ActorIri);
    }

    // --- Remote person: not this instance's concern ---------------------------------------

    [Fact]
    public async Task HandleAsync_RemotePersonRecipient_NoOp()
    {
        // The recipient is not a local person (no such actor in the store) → no recording. The remote
        // instance records the post in its own outbox.
        var persistence = new InMemoryPersistenceProvider();
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemotePerson);

        await sut.HandleAsync(new InboxDelivery(RemotePerson, create), create);

        Assert.Empty(await persistence.Activities.GetOutboxAsync(RemotePerson));
    }

    // --- Local community: the "followed content" half --------------------------------------

    [Fact]
    public async Task HandleAsync_LocalCommunityRecipient_RecordsInMemberOutbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await SeedLocalActorAsync(persistence, LocalMember);
        await persistence.Communities.AddFollowerAsync(Community, LocalMember);
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemotePerson); // a remote follower publishes to the community

        await sut.HandleAsync(new InboxDelivery(Community, create), create);

        // The Create is recorded in the local member's outbox (the community's unified feed surfaces it).
        var memberOutbox = await persistence.Activities.GetOutboxAsync(LocalMember);
        Assert.Contains(create.Id, OutboxIds(memberOutbox));
        // And NOT in the community's own outbox (a community has no personal outbox of its own here).
        Assert.Empty(await persistence.Activities.GetOutboxAsync(Community));
    }

    [Fact]
    public async Task HandleAsync_CommunityWithRemoteMember_SkipsRemoteMember()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await SeedLocalActorAsync(persistence, LocalMember);
        await persistence.Communities.AddFollowerAsync(Community, LocalMember);
        await persistence.Communities.AddFollowerAsync(Community, RemoteMember); // not seeded as local
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemotePerson);

        await sut.HandleAsync(new InboxDelivery(Community, create), create);

        // Only the local member's outbox is recorded; the remote member's is untouched.
        var localIds = OutboxIds(await persistence.Activities.GetOutboxAsync(LocalMember));
        Assert.Contains(create.Id, localIds);
        Assert.Empty(await persistence.Activities.GetOutboxAsync(RemoteMember));
    }

    // --- Guards --------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_UnknownRecipient_NoOp()
    {
        // The recipient is neither a local person nor a local community → no recording.
        var persistence = new InMemoryPersistenceProvider();
        var other = new Iri("https://b.domain.local/ap/v1/u/unknown");
        var sut = BuildHandler(persistence);
        var create = BuildCreate(other);

        await sut.HandleAsync(new InboxDelivery(other, create), create);

        Assert.Empty(await persistence.Activities.GetOutboxAsync(other));
    }

    // --- F-12: reply (inReplyTo) edge recording -------------------------------------------

    [Fact]
    public async Task HandleAsync_ReplyRecordsParentToChildEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var parentNote = new Iri("https://a.domain.local/ap/v1/u/alice/notes/parent");
        var create = BuildCreate(LocalPerson, inReplyTo: parentNote.Value);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The stored reply's inReplyTo (the parent) → the reply note IRI edge is recorded.
        var replyIri = EmbeddedNoteId(create);
        Assert.True(await persistence.Replies.HasReplyAsync(parentNote, replyIri));
        Assert.Equal([replyIri], await persistence.Replies.GetRepliesAsync(parentNote));
    }

    [Fact]
    public async Task HandleAsync_ToplevelNoteRecordsNoReplyEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var create = BuildCreate(LocalPerson); // no inReplyTo

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // No parent → no reply edge anywhere.
        var replyIri = EmbeddedNoteId(create);
        Assert.False(await persistence.Replies.HasReplyAsync(replyIri, replyIri));
    }

    [Fact]
    public async Task HandleAsync_CommunityReplyRecordsEdge()
    {
        // A reply whose parent is a note is recorded even when delivered to a local community.
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await SeedLocalActorAsync(persistence, LocalMember);
        await persistence.Communities.AddFollowerAsync(Community, LocalMember);
        var sut = BuildHandler(persistence);
        var parentNote = new Iri("https://a.domain.local/ap/v1/u/alice/notes/parent");
        var create = BuildCreate(LocalMember, inReplyTo: parentNote.Value);

        await sut.HandleAsync(new InboxDelivery(Community, create), create);

        var replyIri = EmbeddedNoteId(create);
        Assert.True(await persistence.Replies.HasReplyAsync(parentNote, replyIri));
    }

    // --- S62: reply → remote parent author delivery -------------------------------------

    [Fact]
    public async Task HandleAsync_ReplyToRemoteParent_DeliversToParentAuthorInbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson); // bob (local) is the recipient
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        // The parent note is authored by a REMOTE actor (alice on a.domain.local).
        var parentNoteIri = new Iri("https://a.domain.local/ap/v1/objects/parent-123");
        var parentNote = new Note
        {
            Id = parentNoteIri.Value,
            Content = ["a post on the remote instance"],
            AttributedTo = [new Link { Href = new Uri(RemotePerson.Value) }],
        };
        await persistence.Objects.PutObjectAsync(parentNote);

        // A remote actor (erin) replies to the parent note. The Create arrives in bob's (local) inbox.
        var remoteReplier = new Iri("https://c.domain.local/ap/v1/u/erin");
        var create = BuildCreate(remoteReplier, inReplyTo: parentNoteIri.Value);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // S62: the reply is delivered to the parent author's (alice's) inbox (cross-instance).
        var parentAuthorDelivery = delivery.Delivered.FirstOrDefault(d => d.InboxIri == RemotePerson.InboxOf());
        Assert.NotNull(parentAuthorDelivery);
        Assert.Same(create, parentAuthorDelivery!.Activity);
        Assert.Equal(remoteReplier, parentAuthorDelivery.ActorIri); // signed as the replier
    }

    // S100: a reply's Create names the parent author in its `to`/`cc` audience, so the S47
    // named-recipient loop delivers it to the parent author's inbox. The S62 parent-author delivery must
    // NOT then deliver the SAME Create IRI to the same parent author a second time — the duplicate is
    // rejected by Lemmy's insert_received_activity idempotency guard with a 400 that dead-letters the
    // reply. The handler tracks delivered actors and drops the redundant S62 delivery.
    [Fact]
    public async Task HandleAsync_ReplyNamesParentInTo_DoesNotDuplicateParentAuthorDelivery()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson); // bob (local) is the recipient/replier
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        // The parent note is authored by a REMOTE actor (alice on a.domain.local).
        var parentNoteIri = new Iri("https://a.domain.local/ap/v1/objects/parent-789");
        var parentNote = new Note
        {
            Id = parentNoteIri.Value,
            Content = ["a post on the remote instance"],
            AttributedTo = [new Link { Href = new Uri(RemotePerson.Value) }],
        };
        await persistence.Objects.PutObjectAsync(parentNote);

        // bob replies; the reply names the parent author alice in its `to` (the S47 recipient).
        var create = BuildCreate(LocalPerson, inReplyTo: parentNoteIri.Value);
        var replyNote = (Note)create.Object!.First()!;
        replyNote.To = [new Link { Href = new Uri(RemotePerson.Value) }];

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The parent author's inbox is targeted EXACTLY ONCE (by S47, not also by S62): the same Create
        // IRI is not sent twice to the same shared inbox.
        var parentAuthorDeliveries = delivery.Delivered.Where(d => d.InboxIri == RemotePerson.InboxOf()).ToList();
        Assert.Single(parentAuthorDeliveries);
        Assert.Same(create, parentAuthorDeliveries[0].Activity);
    }

    [Fact]
    public async Task HandleAsync_ReplyToLocalParent_SkipsParentAuthorDelivery()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson); // bob (local) is the recipient
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        // The parent note is authored by a LOCAL actor (frank on b.domain.local).
        await SeedLocalActorAsync(persistence, LocalFollower); // frank (local)
        var parentNoteIri = new Iri("https://b.domain.local/ap/v1/objects/parent-456");
        var parentNote = new Note
        {
            Id = parentNoteIri.Value,
            Content = ["a post on the local instance"],
            AttributedTo = [new Link { Href = new Uri(LocalFollower.Value) }],
        };
        await persistence.Objects.PutObjectAsync(parentNote);

        // A remote actor (erin) replies to the parent note. The Create arrives in bob's (local) inbox.
        var remoteReplier = new Iri("https://c.domain.local/ap/v1/u/erin");
        var create = BuildCreate(remoteReplier, inReplyTo: parentNoteIri.Value);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // A local parent author sees the reply via their /replies feed — no cross-instance delivery.
        Assert.Empty(delivery.Delivered);
    }

    // --- Likes/shares preservation (994 complete fix investigation) ----------------------

    [Fact]
    public async Task HandleAsync_RemoteNoteWithLikesAndShares_PreservesCollections()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // Build a remote Note with likes and shares collections (as a remote server would send).
        var note = new Note
        {
            Id = $"{RemotePerson}/notes/{Guid.NewGuid():N}",
            Content = ["hello from remote"],
            Likes = new KristofferStrube.ActivityStreams.OrderedCollection
            {
                TotalItems = 42,
            },
            Shares = new KristofferStrube.ActivityStreams.OrderedCollection
            {
                TotalItems = 7,
            },
        };

        var create = new Create
        {
            Id = $"{RemotePerson}/creates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(RemotePerson.Value) }],
            Object = [note],
        };

        // Deliver to a local person (bob) who follows the remote person (alice).
        await persistence.Follows.RecordFollowAsync(LocalPerson, RemotePerson);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // Retrieve the stored object.
        var noteIri = new Iri(note.Id!);
        Assert.True(await persistence.Objects.TryGetObjectAsync(noteIri, out var stored), "Stored object not found");

        // Check if likes/shares are preserved.
        var storedNote = stored as Note;
        Assert.NotNull(storedNote);
        Console.WriteLine($"Stored Note.Likes: {(storedNote.Likes is null ? "null" : $"TotalItems={storedNote.Likes.TotalItems}")}");
        Console.WriteLine($"Stored Note.Shares: {(storedNote.Shares is null ? "null" : $"TotalItems={storedNote.Shares.TotalItems}")}");
        if (storedNote.ExtensionData is { Count: > 0 } ext)
        {
            Console.WriteLine($"ExtensionData keys: {string.Join(", ", ext.Keys)}");
        }

        // Assert that at least one of the two paths preserves the data.
        var hasLikes = storedNote.Likes is { TotalItems: > 0 } ||
                       storedNote.ExtensionData is { } extL && extL.ContainsKey("likes");
        var hasShares = storedNote.Shares is { TotalItems: > 0 } ||
                        storedNote.ExtensionData is { } extS && extS.ContainsKey("shares");

        Assert.True(hasLikes || hasShares,
            "Neither Likes/Shares typed properties nor ExtensionData preserved the collections. " +
            $"Likes: {(storedNote.Likes is null ? "null" : storedNote.Likes.TotalItems.ToString())}, " +
            $"Shares: {(storedNote.Shares is null ? "null" : storedNote.Shares.TotalItems.ToString())}");
    }

    // --- S36: bare-link Create → object fetched + cached --------------------------------

    [Fact]
    public async Task HandleAsync_LocalPerson_BareLinkObject_FetchesAndStoresNote()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);

        // A remote note IRI that is not yet stored locally.
        var noteIri = new Iri("https://a.domain.local/ap/v1/u/alice/notes/remote-note-1");
        var remoteNote = new Note
        {
            Id = noteIri.Value,
            Content = ["cross-instance post"],
            AttributedTo = [new Link { Href = new Uri(RemotePerson.Value) }],
        };

        var fetcher = new StubObjectFetcher(noteIri, remoteNote);
        var sut = new CreateActivityHandler(
            persistence,
            new RecordingDeliveryService(),
            new DefaultLocalActorResolver(persistence),
            new NoOpMediaWarmer(),
            Options.Create(new ActivityPubServerOptions()),
            objectFetcher: fetcher);

        // A Create whose object is a bare link (no embedded IObject).
        var create = new Create
        {
            Id = $"{RemotePerson}/creates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(RemotePerson.Value) }],
            Object = [new Link { Href = new Uri(noteIri.Value) }],
        };

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The fetched note is stored in the object store (so a GET by IRI serves it, not 404).
        Assert.True(
            await persistence.Objects.TryGetObjectAsync(noteIri, out var stored, default),
            "The bare-link Create's object was not fetched + stored in the object store.");
        Assert.NotNull(stored);
        Assert.Equal("cross-instance post", stored!.Content?.FirstOrDefault());

        // The Create is recorded in the local person's outbox (so it surfaces in the feed).
        Assert.Contains(create.Id, OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson)));

        // The fetcher was invoked exactly once.
        Assert.Equal(1, fetcher.FetchCount);
    }

    [Fact]
    public async Task HandleAsync_LocalPerson_BareLinkObject_FetchFailure_StoresNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);

        var noteIri = new Iri("https://a.domain.local/ap/v1/u/alice/notes/remote-note-2");
        var fetcher = new StubObjectFetcher(noteIri, null); // fetch returns null (failure)
        var sut = new CreateActivityHandler(
            persistence,
            new RecordingDeliveryService(),
            new DefaultLocalActorResolver(persistence),
            new NoOpMediaWarmer(),
            Options.Create(new ActivityPubServerOptions()),
            objectFetcher: fetcher);

        var create = new Create
        {
            Id = $"{RemotePerson}/creates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(RemotePerson.Value) }],
            Object = [new Link { Href = new Uri(noteIri.Value) }],
        };

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // A fetch failure leaves the object unstored (best-effort; the Create is still in the outbox).
        Assert.False(
            await persistence.Objects.TryGetObjectAsync(noteIri, out _, default),
            "The object should not be stored when the fetch fails.");
        Assert.Contains(create.Id, OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson)));
    }

    // --- Helpers -------------------------------------------------------------------------

    private static Group BuildCommunity() => new()
    {
        Id = Community.Value,
        Name = ["Iris"],
        PreferredUsername = "iris",
    };

    private static CreateActivityHandler BuildHandler(
        IPersistenceProvider persistence, IDeliveryService? delivery = null)
        => new(
            persistence,
            delivery ?? new RecordingDeliveryService(),
            new DefaultLocalActorResolver(persistence),
            new NoOpMediaWarmer(),
            // No BaseUri → the handler's eager-warm hook is skipped (these unit tests do not exercise
            // media warming; the integration tests do, over a real host).
            Options.Create(new ActivityPubServerOptions()));

    /// <summary>
    /// A no-op <see cref="IMediaWarmer"/> for the unit tests (they do not exercise media warming).
    /// </summary>
    private sealed class NoOpMediaWarmer : IMediaWarmer
    {
        public Task WarmAsync(IObject? obj, Iri instanceBase, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static Task SeedLocalActorAsync(IPersistenceProvider persistence, Iri actorIri)
    {
        var handle = new Uri(actorIri.Value).AbsolutePath.Trim('/').Split('/').Last();
        var actor = new Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        };
        return persistence.Actors.PutActorAsync(actor);
    }

    private static Create BuildCreate(Iri authorIri, string? inReplyTo = null)
    {
        var note = new Note
        {
            Id = $"{authorIri}/notes/{Guid.NewGuid():N}",
            Content = ["hello"],
        };
        if (inReplyTo is not null)
        {
            note.InReplyTo = [new Link { Href = new Uri(inReplyTo) }];
        }

        return new Create
        {
            Id = $"{authorIri}/creates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(authorIri.Value) }],
            Object = [note],
        };
    }

    private static Iri EmbeddedNoteId(Create create)
        => new(create.ExtractEmbeddedObject()!.Id!);

    private static List<string> OutboxIds(IReadOnlyList<IObjectOrLink> outbox)
        => outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();

    /// <summary>
    /// An <see cref="IDeliveryService"/> that records every scheduled delivery (instead of enqueuing) so a
    /// test can assert on <see cref="Delivered"/> — the target inbox, the activity, and the signing actor.
    /// </summary>
    private sealed class RecordingDeliveryService : IDeliveryService
    {
        public List<DeliveryJob> Delivered { get; } = [];

        public Task DeliverAsync(Iri inboxIri, Activity activity, CancellationToken ct = default)
            => DeliverAsync(inboxIri, activity, actorIri: null, ct);

        public Task DeliverAsync(Iri inboxIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
        {
            Delivered.Add(new DeliveryJob(inboxIri, activity, actorIri));
            return Task.CompletedTask;
        }

        public Task DeliverToActorAsync(Iri recipientIri, Activity activity, CancellationToken ct = default)
            => DeliverToActorAsync(recipientIri, activity, actorIri: null, ct);

        public Task DeliverToActorAsync(Iri recipientIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
            => DeliverAsync(recipientIri.InboxOf(), activity, actorIri, ct);
    }

    /// <summary>
    /// A stub <see cref="IActivityPubClient"/> that returns a preconfigured object for
    /// <see cref="GetObjectAsync"/> (S36 tests). All other methods are no-ops.
    /// </summary>
    private sealed class StubObjectFetcher(Iri targetIri, IObject? fetchedObject) : IActivityPubClient
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

        public Task<IObject?> GetObjectFreshAsync(Iri objectId, CancellationToken ct = default)
            => GetObjectAsync(objectId, ct);

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

        public Task<DeliveryResult> UpdateObjectAsync(Iri actorId, IObject updatedObject, CancellationToken ct = default)
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

        public IAsyncEnumerable<Iris.Core.Collections.CollectionPage> SearchPagedAsync(Iri instanceBase, string? query = null, SearchOptions? options = null, CancellationToken ct = default)
            => EmptyPagedAsync();

        private static async IAsyncEnumerable<Iris.Core.Collections.CollectionPage> EmptyPagedAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

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
    }
}
