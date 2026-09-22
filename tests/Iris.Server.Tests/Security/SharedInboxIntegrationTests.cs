using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Integration tests for the instance-wide shared inbox (<c>POST /ap/v1/shared-inbox</c>) — the route
/// advertised in every actor document's <c>endpoints.sharedInbox</c>. A remote sender that prefers the
/// shared inbox (e.g. Mastodon, which coalesces delivery targets by <c>preferred_inbox_url</c>) delivers
/// every activity to this single route instead of to a per-actor inbox. Before this route existed, the
/// advertised <c>sharedInbox</c> fell through to the catch-all, which returned 200 and silently dropped
/// the body — so a local follower of a shared-inbox-preferring sender never received that sender's
/// posts (the "no longer receive posts after unfollow" symptom).
/// </summary>
/// <remarks>
/// Two live in-process <see cref="TestServer"/> instances: A (a.domain.local) hosts <c>alice</c>, B
/// (b.domain.local) hosts <c>bob</c> (and <c>carol</c> as a second local actor). A's fetcher routes to
/// B so B can resolve alice's key by fetching A's actor document over the wire (the same federation
/// round-trip as the per-actor inbox). Each test delivers a signed activity to B's
/// <c>/ap/v1/shared-inbox</c> and asserts B routed it to the correct local recipient.
/// </remarks>
public sealed class SharedInboxIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Carol = "carol";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _bobKey;
    private readonly Iri AliceActorIri;
    private readonly Iri BobActorIri;
    private readonly Iri CarolActorIri;
    private readonly Iri BobSharedInboxIri;

    public SharedInboxIntegrationTests()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        AliceActorIri = aSeeded.ActorIri;

        var bBob = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        var bCarol = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Carol);
        _bobKey = bBob.Key;
        BobActorIri = bBob.ActorIri;
        CarolActorIri = bCarol.ActorIri;
        BobSharedInboxIri = new Iri($"https://{BHost}/ap/v1/shared-inbox");

        // A's fetcher/delivery are lazy self-safe loops (A doesn't fetch or deliver in these tests, but
        // the host builds those clients at startup, before _b exists). B's fetcher routes to A over the
        // wire: B resolves the remote signer's (alice's) key by fetching A's actor doc — the same
        // federation round-trip as the per-actor inbox. B's delivery is a self-loop over B's own
        // TestServer (the AnnounceActivityHandler propagating to carol's inbox), deferred via a
        // LazyHandler until bRef is assigned.
        TestServer? bRef = null;
        _a = StartServer(AHost, Alice, aPersistence,
            fetcher: BuildFetcherFor(AHost, Alice, aSeeded.Key, new LazyHandler(() => bRef!.CreateHandler())),
            deliveryTransport: () => new LazyHandler(() => bRef!.CreateHandler()));
        bRef = StartServer(BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bBob.Key, _a.CreateHandler()),
            extraLocalActors: [CarolActorIri],
            deliveryTransport: () => new LazyHandler(() => bRef!.CreateHandler()));
        _b = bRef;
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- A Follow delivered to the shared inbox is routed to its object (bob) ---

    [Fact]
    public async Task Follow_DeliveredToSharedInbox_RoutesToObjectAndRecordsFollowEdge()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, follow);
        Assert.Equal(202, statusCode.StatusCode);

        // B validated alice's signature (resolving her key from A's actor doc over the wire) and stored
        // the activity under its IRI.
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out _),
            "B should have stored the Follow after validating alice's signature at the shared inbox");

        // B routed the Follow to its object (bob) and the FollowActivityHandler recorded the edge — the
        // same outcome as if the Follow had been delivered to bob's per-actor inbox.
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, BobActorIri),
            "A Follow delivered to the shared inbox should record the alice -> bob follow edge");
    }

    // --- S28 / S37: a remote Announce (boost) of a LOCAL note delivered to B's shared inbox must route
    // --- to the note's AUTHOR (the note's attributedTo), not the announcer's local followers. The note's
    // --- /shares collection + denormalized sharedCount (decision 056 (d)) live on the note's owner, so the
    // --- boost is recorded on the note's home (B). Before the fix the Announce branch fanned out to the
    // --- announcer's LOCAL followers — and when the announcer (remote) has none here, the delivery was
    // --- dropped as "no local recipient" and the note's sharedCount / shares stayed at 0 (the S28 / S37
    // --- boost-count facet). The AnnounceActivityHandler (running for the note's author) records the
    // --- announcer → note edge + refreshes sharedCount, then fans the boost out to the announcer's
    // --- followers itself (the follower-feed surface) — so the shared inbox no longer needs to.

    [Fact]
    public async Task AnnounceOfLocalNote_DeliveredToSharedInbox_RoutesToAuthorAndRecordsBoostEdge()
    {
        // bob (local, hosted by B) authored a note that is stored in B's object store.
        var noteIri = $"https://{BHost}/ap/v1/u/{Bob}/notes/{Guid.NewGuid():N}";
        await _bPersistence.Objects.PutObjectAsync(new Note
        {
            Id = noteIri,
            Content = ["a note by bob"],
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
            To = [new Link { Href = new Uri(Iri.Public.Value) }],
        });

        // alice (remote, hosted by A) boosts bob's note: an Announce whose object is the note IRI (a
        // content object, not an actor). Deliver it to B's shared inbox over the wire (B resolves alice's
        // key from A's actor doc). The shared inbox must route the Announce to the note's author (bob),
        // whose AnnounceActivityHandler records the boost edge on the note.
        var announceIri = $"https://{AHost}/activities/announce-{Guid.NewGuid():N}";
        var announce = new Announce
        {
            Id = announceIri,
            Actor = [new Link { Href = new Uri(AliceActorIri.Value) }],
            AttributedTo = [new Link { Href = new Uri(AliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(noteIri) }],
            To = [new Link { Href = new Uri(BobActorIri.Value) }],
        };

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, announce);
        Assert.Equal(202, statusCode.StatusCode);

        // B validated the signature and stored the Announce under its IRI (proving the shared inbox routed
        // it to a local recipient and processed it, rather than "no local recipient; accepting and dropping").
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(announceIri), out _),
            "An Announce of a local note delivered to the shared inbox should be stored (routed to the note's author), not dropped (S28/S37).");

        // B recorded the boost edge (alice → note) on the note's home — the note's /shares collection now
        // includes alice's boost. Routing the Announce to the announcer's local followers (the prior
        // behavior) would have dropped it (alice has no local followers on B) and left /shares / sharedCount
        // at 0.
        Assert.True(
            await _bPersistence.Announces.HasAnnouncedAsync(AliceActorIri, new Iri(noteIri)),
            "An Announce of a local note delivered to the shared inbox should record the boost edge on the note (S28/S37).");
    }

    // --- A remote Announce (boost) of a REMOTE object delivered to B's shared inbox is accepted and
    // --- dropped: the boost is recorded on the object's HOME instance (which the sender delivers it to
    // --- directly), not on the announcer's followers' instance. B neither stores the edge nor fans the
    // --- boost out to the announcer's local followers (the prior behavior) — it has no local copy of the
    // --- object to count the boost against. This is the inverse of the local-note case above.

    [Fact]
    public async Task AnnounceOfRemoteObject_DeliveredToSharedInbox_AcceptedAndDropped()
    {
        // bob follows alice (the announcer). A shared-inbox-preferring sender would previously have
        // delivered alice's boost of a remote note to B's shared inbox, fanning it out to alice's local
        // followers (bob). That was a defect: the boost belongs on the remote note's home instance, not
        // on B. The shared inbox now routes the Announce to the object's owner — unresolvable here (the
        // object is remote, not stored on B), so B accepts (202) and drops.
        await _bPersistence.Follows.RecordFollowAsync(BobActorIri, AliceActorIri);

        var objectIri = $"https://{AHost}/objects/note-{Guid.NewGuid():N}";
        var announceIri = $"https://{AHost}/activities/announce-{Guid.NewGuid():N}";
        var announce = new Announce
        {
            Id = announceIri,
            Actor = [new Link { Href = new Uri(AliceActorIri.Value) }],
            AttributedTo = [new Link { Href = new Uri(AliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(objectIri) }],
            To = [new Link { Href = new Uri(BobActorIri.Value) }],
        };

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, announce);
        Assert.Equal(202, statusCode.StatusCode);

        // No boost edge was recorded on B (the remote note's home is A, not B): alice did not boost the
        // object in B's store. The delivery was dropped (routed to the object's unresolvable owner), not
        // fanned out to the announcer's local followers.
        Assert.False(
            await _bPersistence.Announces.HasAnnouncedAsync(AliceActorIri, new Iri(objectIri)),
            "An Announce of a remote object delivered to the shared inbox should not record a boost edge on this instance (S28/S37).");
    }

    // --- A Follow addressed to a REMOTE actor (not hosted by B) is accepted and dropped ---

    [Fact]
    public async Task Follow_AddressedToRemoteActor_AcceptedAndDropped()
    {
        // A shared-inbox-preferring sender delivers a Follow whose object is a remote actor (not hosted
        // by B). The shared inbox must accept (202) and drop it rather than 4xx (a 4xx would make the
        // sender retry a delivery B can never process).
        var remoteTargetIri = new Iri($"https://c.domain.local/ap/v1/u/dave");
        var follow = BuildFollow(AliceActorIri, remoteTargetIri);

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, follow);
        Assert.Equal(202, statusCode.StatusCode);

        // No follow edge was recorded (the delivery was dropped, not processed): alice does not follow
        // the remote actor in B's store.
        Assert.False(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, remoteTargetIri),
            "A Follow to a remote actor delivered via the shared inbox should not record a follow edge");
    }

    // --- S33: an Undo of a Follow delivered to the shared inbox must route to the follow's TARGET
    // --- (the followee), not the follow's own IRI. The Undo's object is the original Follow (embedded
    // --- or by IRI); routing to the follow IRI 404s as "unknown recipient" (it is an activity, not an
    // --- actor) and the peer's followers edge is left stale. The shared inbox must resolve the follow's
    // --- target and remove the unfollower from it. (Both shapes: the embedded Follow and the bare IRI
    // --- link that the followee's activity store can resolve.) ---

    [Fact]
    public async Task UndoOfFollow_DeliveredToSharedInbox_RoutesToFollowTargetAndRemovesEdge()
    {
        // Establish the follow edge in B's store (alice → bob) and store the original Follow in B's
        // activity store (the bare-IRI Undo branch resolves against it).
        var follow = BuildFollow(AliceActorIri, BobActorIri);
        await _bPersistence.Follows.RecordFollowAsync(AliceActorIri, BobActorIri);
        await _bPersistence.Activities.PutActivityAsync(follow);
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, BobActorIri),
            "Setup: alice should follow bob in B's store before the un-follow.");

        // alice (remote) un-follows bob: an Undo whose object is the original Follow EMBEDDED (the shape
        // a real sender delivers after the Lemmy-interop embed fix). Deliver it to B's shared inbox over
        // the wire (B resolves alice's key from A's actor doc).
        var undoIri = $"https://{AHost}/activities/undo-{Guid.NewGuid():N}";
        var undo = new Undo
        {
            Id = undoIri,
            Actor = [new Link { Href = new Uri(AliceActorIri.Value) }],
            Object = [follow],
            To = [new Link { Href = new Uri(BobActorIri.Value) }],
        };

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, undo);
        Assert.Equal(202, statusCode.StatusCode);

        // B removed the alice → bob edge — the shared inbox routed the Undo to the follow's target (bob),
        // not the follow's own IRI (which would 404 as "unknown recipient" and leave the edge stale).
        Assert.False(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, BobActorIri),
            "An Undo of a Follow delivered to the shared inbox should remove the unfollower from the " +
            "followee's followers set (S33).");
    }

    [Fact]
    public async Task UndoOfFollow_BareIri_DeliveredToSharedInbox_RoutesToFollowTargetAndRemovesEdge()
    {
        // The bare-IRI shape: the Undo's object is a link to the original Follow IRI (not an embedded
        // activity). The shared inbox must resolve the follow from B's activity store and route to its
        // target (bob). This is the shape S33 reproduced on the live stack (the peer's outbox shows a bare
        // IRI, and the peer rejected it as "unknown recipient <follow-IRI>").
        var follow = BuildFollow(AliceActorIri, BobActorIri);
        await _bPersistence.Follows.RecordFollowAsync(AliceActorIri, BobActorIri);
        await _bPersistence.Activities.PutActivityAsync(follow);
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, BobActorIri),
            "Setup: alice should follow bob in B's store before the un-follow.");

        var undoIri = $"https://{AHost}/activities/undo-{Guid.NewGuid():N}";
        var undo = new Undo
        {
            Id = undoIri,
            Actor = [new Link { Href = new Uri(AliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(follow.Id!) }],
            To = [new Link { Href = new Uri(BobActorIri.Value) }],
        };

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, undo);
        Assert.Equal(202, statusCode.StatusCode);

        Assert.False(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, BobActorIri),
            "A bare-IRI Undo of a Follow delivered to the shared inbox should remove the unfollower from " +
            "the followee's followers set (S33).");
    }

    // --- S27: a Like of a LOCAL note delivered to the shared inbox must route to the note's AUTHOR
    // --- (the note's attributedTo), not the note's own IRI. The note is a content object, not an actor,
    // --- so routing to its IRI drops the activity as "no local recipient" and the note's /likes never
    // --- updates. The shared inbox must resolve the note's owner and dispatch the Like to the author,
    // --- whose LikeActivityHandler records the like edge on the note. ---

    [Fact]
    public async Task LikeOfLocalNote_DeliveredToSharedInbox_RoutesToAuthorAndRecordsLikeEdge()
    {
        // bob (local, hosted by B) authored a note that is stored in B's object store.
        var noteIri = $"https://{BHost}/ap/v1/u/{Bob}/notes/{Guid.NewGuid():N}";
        await _bPersistence.Objects.PutObjectAsync(new Note
        {
            Id = noteIri,
            Content = ["a note by bob"],
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
            To = [new Link { Href = new Uri(Iri.Public.Value) }],
        });

        // alice (remote) likes bob's note: a Like whose object is the note IRI (a content object, not an
        // actor). Deliver it to B's shared inbox over the wire (B resolves alice's key from A's actor doc).
        var like = BuildLike(AliceActorIri, new Iri(noteIri));
        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, like);
        Assert.Equal(202, statusCode.StatusCode);

        // B validated the signature and stored the Like under its IRI (proving the shared inbox routed it
        // to a local recipient and processed it, rather than "no local recipient; accepting and dropping").
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(like.Id!), out _),
            "A Like delivered to the shared inbox should be stored (routed to the note's author), not dropped (S27).");

        // B recorded the like edge (alice → note) — the note's /likes collection now includes alice. The
        // shared inbox routed the Like to the note's author (bob), whose LikeActivityHandler recorded the
        // edge; routing to the note's own IRI would have dropped it (no local recipient) and left /likes empty.
        Assert.True(
            await _bPersistence.Likes.HasLikedAsync(AliceActorIri, new Iri(noteIri)),
            "A Like of a local note delivered to the shared inbox should record the like edge on the note (S27).");
    }

    // --- S32: a Delete of a LOCAL note delivered to the shared inbox must route to the note's AUTHOR
    // --- (the note's attributedTo), not the note's own IRI. A Delete references the deleted object (a
    // --- bare Link to the note IRI — a content object, not an actor), so routing to the note IRI 404s
    // --- as "unknown recipient" and the peer's copy is never tombstoned (it keeps a stale live Note).
    // --- The shared inbox must resolve the note's owner and dispatch the Delete to the author, whose
    // --- DeleteActivityHandler tombstones the stored object (authorizing the remote owner — the delete's
    // --- actor is the note's attributedTo). ---

    [Fact]
    public async Task DeleteOfLocalNote_DeliveredToSharedInbox_RoutesToAuthorAndTombstonesNote()
    {
        // bob (local, hosted by B) authored a note that is stored in B's object store (a federated copy
        // B holds via the outbound Create federation). bob is a LOCAL actor on B (the note's home
        // instance), so the shared inbox can route the Delete to bob (the note's owner). The note IRI is
        // in B's serving namespace (the /ap/v1 route) so B serves it on a GET.
        var noteIri = $"https://{BHost}/ap/v1/u/{Bob}/notes/{Guid.NewGuid():N}";
        await _bPersistence.Objects.PutObjectAsync(new Note
        {
            Id = noteIri,
            Content = ["a note by bob"],
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
            To = [new Link { Href = new Uri(Iri.Public.Value) }],
        });

        // alice (remote, hosted by A) deletes bob's note: a Delete whose object is a bare Link to the
        // note IRI (a content object, not an actor). The Delete's actor is bob (the note's owner) —
        // in the real federation, bob's instance would deliver this Delete to B's shared inbox. For
        // the test, alice signs it (B resolves alice's key from A's actor doc over the wire). The
        // shared inbox must route the Delete to the note's author (bob), whose DeleteActivityHandler
        // tombstones the stored object.
        var deleteIri = $"https://{AHost}/activities/delete-{Guid.NewGuid():N}";
        var delete = new Delete
        {
            Id = deleteIri,
            Actor = [new Link { Href = new Uri(BobActorIri.Value) }],
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
            Object = [new Link { Href = new Uri(noteIri) }],
        };

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, delete);
        Assert.Equal(202, statusCode.StatusCode);

        // B validated the signature and stored the Delete under its IRI (proving the shared inbox routed it
        // to a local recipient and processed it, rather than 404'ing it as "unknown recipient <note-IRI>").
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(deleteIri), out _),
            "A Delete delivered to the shared inbox should be stored (routed to the note's owner), not dropped (S32).");

        // B's DeleteActivityHandler tombstoned the stored note — B no longer serves the live Note (the
        // stale-copy defect is gone): a later GET of the note IRI serves the Tombstone. Routing the Delete
        // to the note's own IRI would have 404'd it as "unknown recipient" and left the live Note stored.
        Assert.True(
            await _bPersistence.Objects.TryGetObjectAsync(new Iri(noteIri), out var tombstoned),
            "The note should still be present in B's object store after a Delete (as a tombstone).");
        Assert.True(
            tombstoned is Tombstone,
            "A Delete of a local note delivered to the shared inbox should tombstone the stored note (S32), not leave the live Note.");
    }

    // --- S32: an Update of a LOCAL note delivered to the shared inbox must route to the note's AUTHOR
    // --- (the note's attributedTo), not the note's own IRI. The Update carries the updated object
    // --- embedded (a reference-only Update is not interpreted), so routing to the note IRI 404s as
    // --- "unknown recipient" and the peer's copy is never refreshed (it keeps the stale pre-edit Note).
    // --- The shared inbox must resolve the note's owner and dispatch the Update to the author, whose
    // --- UpdateActivityHandler refreshes the stored object with the new content (authorizing the remote
    // --- owner — the update's actor is the note's attributedTo). ---

    [Fact]
    public async Task UpdateOfLocalNote_DeliveredToSharedInbox_RoutesToAuthorAndRefreshesNote()
    {
        // bob (local, hosted by B) authored a note that is stored in B's object store (a federated copy
        // B holds via the outbound Create federation). bob is a LOCAL actor on B (the note's home
        // instance), so the shared inbox can route the Update to bob (the note's owner). The note IRI is
        // in B's serving namespace (the /ap/v1 route) so B serves it on a GET.
        var noteIri = $"https://{BHost}/ap/v1/u/{Bob}/notes/{Guid.NewGuid():N}";
        await _bPersistence.Objects.PutObjectAsync(new Note
        {
            Id = noteIri,
            Content = ["a note by bob (original)"],
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
            To = [new Link { Href = new Uri(Iri.Public.Value) }],
        });

        // alice (remote, hosted by A) edits bob's note: an Update whose object is the EMBEDDED updated
        // Note (id + new content + attributedTo). The Update's actor is bob (the note's owner) — in
        // the real federation, bob's instance would deliver this Update to B's shared inbox. For the
        // test, alice signs it (B resolves alice's key from A's actor doc over the wire). The shared
        // inbox must route the Update to the note's author (bob), whose UpdateActivityHandler
        // refreshes the stored object.
        var updateIri = $"https://{AHost}/activities/update-{Guid.NewGuid():N}";
        var updatedNote = new Note
        {
            Id = noteIri,
            Content = ["a note by bob (edited)"],
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
            To = [new Link { Href = new Uri(Iri.Public.Value) }],
        };
        var update = new Update
        {
            Id = updateIri,
            Actor = [new Link { Href = new Uri(BobActorIri.Value) }],
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
            Object = [updatedNote],
        };

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, update);
        Assert.Equal(202, statusCode.StatusCode);

        // B validated the signature and stored the Update under its IRI (proving the shared inbox routed
        // it to a local recipient and processed it, rather than 404'ing it as "unknown recipient <note-IRI>").
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(updateIri), out _),
            "An Update delivered to the shared inbox should be stored (routed to the note's author), not dropped (S32).");

        // B's UpdateActivityHandler refreshed the stored note with the new content — B no longer serves
        // the stale pre-edit Note. Routing the Update to the note's own IRI would have 404'd it as
        // "unknown recipient" and left the original content stored.
        Assert.True(
            await _bPersistence.Objects.TryGetObjectAsync(new Iri(noteIri), out var refreshed),
            "The note should still be present in B's object store after an Update (with refreshed content).");
        var refreshedContent = (refreshed as KristofferStrube.ActivityStreams.Object)?.Content
            ?.Select(c => c.ToString())
            .ToArray();
        Assert.NotNull(refreshedContent);
        Assert.Contains("a note by bob (edited)", refreshedContent);
    }

    // --- A Create whose author is not local is accepted and dropped (not this instance's concern) ---

    [Fact]
    public async Task Create_AuthorNotLocal_AcceptedAndDropped()
    {
        // alice creates a note whose embedded object is attributedTo a REMOTE actor (not hosted by B).
        // The shared inbox must accept (202) and drop it rather than 4xx (a 4xx would make the sender
        // retry a delivery B can never process).
        var remoteAuthorIri = new Iri($"https://c.domain.local/ap/v1/u/carol");
        var noteIri = $"https://{AHost}/objects/note-{Guid.NewGuid():N}";
        var createIri = $"https://{AHost}/activities/create-{Guid.NewGuid():N}";
        var note = new Note
        {
            Id = noteIri,
            AttributedTo = [new Link { Href = new Uri(remoteAuthorIri.Value) }],
            To = [new Link { Href = new Uri(Iri.Public.Value) }],
        };
        var create = new Create
        {
            Id = createIri,
            Actor = [new Link { Href = new Uri(AliceActorIri.Value) }],
            Object = [note],
        };

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, create);
        Assert.Equal(202, statusCode.StatusCode);

        // Nothing was stored in bob's outbox (the delivery was dropped, not processed).
        var outbox = await _bPersistence.Activities.GetOutboxAsync(BobActorIri);
        Assert.Empty(outbox);
    }

    // --- Helpers ----------------------------------------------------------------

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null,
        IEnumerable<Iri>? extraLocalActors = null,
        Func<HttpMessageHandler>? deliveryTransport = null)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
            ExtraLocalActors = extraLocalActors,
            DeliveryTransport = deliveryTransport,
            // Advertise a shared inbox so the route is the instance's canonical sharedInbox.
            SharedInboxIri = new Iri($"https://{host}/ap/v1/shared-inbox"),
        });

    private static IActivityPubClient BuildDeliveryClient(
        Iri actorIri, KeyPair key, HttpMessageHandler handler)
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

    private static Follow BuildFollow(Iri actorIri, Iri targetIri)
    {
        var follow = new Follow
        {
            Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(targetIri.Value) }],
        };
        return follow;
    }

    private static Like BuildLike(Iri likerIri, Iri objectIri)
    {
        return new Like
        {
            Id = $"https://{AHost}/activities/like-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(likerIri.Value) }],
            AttributedTo = [new Link { Href = new Uri(likerIri.Value) }],
            Object = [new Link { Href = new Uri(objectIri.Value) }],
        };
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(await condition(), "Condition was not met within the timeout.");
    }
}
