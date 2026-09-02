using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="DeleteActivityHandler"/> — the handler for an inbound
/// <see cref="Delete"/> (an actor deleting one of their objects). When the deleting actor is a local
/// actor and the referenced object is one this instance stores in the <see cref="IObjectStore"/>, the
/// stored object is replaced by an AS2.0 <see cref="Tombstone"/> (F-03/F-10) so a later <c>GET</c> of the
/// object's IRI serves the "deleted" marker, not a <c>404</c>; the <see cref="Delete"/> is propagated to
/// the remote actors that hold a copy (the federated half of F-03, via
/// <see cref="IDeletePropagationService"/>); and, when the deleted object is a reply, its local
/// parent → child reply edge is removed from the <see cref="IReplyStore"/> (F-12). Covers: a local owner
/// deleting a stored object (Tombstone + propagation to the remote follower), deleting a reply (the
/// reply edge is removed and the remote parent's owner is told), deleting an object this instance does
/// not store (no-op, no propagation), a remote (non-local) deleting actor (no-op — the owner guard), a
/// delete with no resolvable object, and the null-guard contract.
/// </summary>
public sealed class DeleteActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemotePerson = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri NoteIri = new("https://b.domain.local/ap/v1/u/bob/notes/n1");
    private static readonly Iri ParentNoteIri = new("https://b.domain.local/ap/v1/u/bob/notes/n0");
    private static readonly Iri ReplyIri = new("https://b.domain.local/ap/v1/u/bob/notes/r1");

    // --- Local owner deletes a stored object ------------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalOwnerDeletesStoredObject_StoresTombstone()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // The object exists (as a Create would have stored it).
        await persistence.Objects.PutObjectAsync(BuildNote("doomed body"));

        // The owner deletes it (a bare link reference — the common Delete shape).
        var delete = BuildDelete(LocalPerson, NoteIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, delete), delete);

        // The stored object is now a Tombstone with the original id and formerType.
        Assert.True(await persistence.Objects.TryGetObjectAsync(NoteIri, out var stored));
        Assert.NotNull(stored);
        Assert.IsType<Tombstone>(stored);
        var tomb = Assert.IsType<Tombstone>(stored);
        Assert.Equal(NoteIri.Value, tomb.Id);
        Assert.Contains("Note", tomb.FormerType!);
    }

    // --- Federated propagation (F-03) ------------------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalOwnerDeletes_PropagatesToRemoteFollower()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemotePerson, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        await persistence.Objects.PutObjectAsync(BuildNote("doomed body"));

        var delete = BuildDelete(LocalPerson, NoteIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, delete), delete);

        // The Delete is propagated to the remote follower's inbox, signed as the local owner.
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(RemotePerson.InboxOf(), job.InboxIri);
        Assert.Same(delete, job.Activity);
        Assert.Equal(LocalPerson, job.ActorIri);
    }

    [Fact]
    public async Task HandleAsync_LocalOwnerDeletes_OnlyLocalFollowers_NoPropagation()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await SeedLocalActorAsync(persistence, new Iri("https://b.domain.local/ap/v1/u/carol"));
        await persistence.Follows.RecordFollowAsync(new Iri("https://b.domain.local/ap/v1/u/carol"), LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        await persistence.Objects.PutObjectAsync(BuildNote("doomed body"));

        var delete = BuildDelete(LocalPerson, NoteIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, delete), delete);

        // A local follower's copy is on this instance (tombstoned locally) → no cross-instance delivery.
        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task HandleAsync_LocalOwnerDeletesReply_RemovesReplyEdge_AndTellsRemoteParentOwner()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var remoteParentOwner = new Iri("https://a.domain.local/ap/v1/u/remoteowner");
        // The parent is remote-owned (it lives on a.domain.local, not this instance b.domain.local), so
        // the propagation targets the remote parent's owner. The parent IRI is derived from the owner's
        // own IRI so it is unambiguously remote.
        var remoteParentIri = new Iri($"{remoteParentOwner}/notes/p0");
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        // The remote parent object (stored as a copy from the outbound Create federation) is
        // attributed to the remote owner.
        await persistence.Objects.PutObjectAsync(BuildNote("parent body", remoteParentIri, attributedTo: remoteParentOwner));
        // A reply to the remote-owned parent (the edge was recorded by the Create handler).
        await persistence.Objects.PutObjectAsync(BuildNote("reply body", ReplyIri, remoteParentIri));
        await persistence.Replies.RecordReplyAsync(remoteParentIri, ReplyIri);

        var delete = BuildDelete(LocalPerson, ReplyIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, delete), delete);

        // The local parent → child reply edge is removed (F-12 cleanup).
        Assert.False(await persistence.Replies.HasReplyAsync(remoteParentIri, ReplyIri));

        // The remote parent's owner is told (the parent's replies collection, F-12).
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(remoteParentOwner.InboxOf(), job.InboxIri);
        Assert.Same(delete, job.Activity);
        Assert.Equal(LocalPerson, job.ActorIri);
    }

    [Fact]
    public async Task HandleAsync_LocalOwnerDeletesReply_LocalParent_OnlyLocalEdgeRemoval()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        // A reply to a local parent (the parent is owned by the local actor).
        await persistence.Objects.PutObjectAsync(BuildNote("reply body", ReplyIri, ParentNoteIri));
        await persistence.Replies.RecordReplyAsync(ParentNoteIri, ReplyIri);

        var delete = BuildDelete(LocalPerson, ReplyIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, delete), delete);

        // The local reply edge is removed ...
        Assert.False(await persistence.Replies.HasReplyAsync(ParentNoteIri, ReplyIri));
        // ... and no cross-instance delivery (the local parent's edge is local state; no remote
        // followers).
        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task HandleAsync_DeleteObjectNotStored_NoPropagation()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemotePerson, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        // The delete references an object this instance never stored → no-op, no propagation.
        var delete = BuildDelete(LocalPerson, NoteIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, delete), delete);

        Assert.False(await persistence.Objects.TryGetObjectAsync(NoteIri, out _));
        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task HandleAsync_LocalOwnerDeletes_OtherStoredObjectsUntouched()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        var otherIri = new Iri($"{LocalPerson}/notes/n2");
        await persistence.Objects.PutObjectAsync(BuildNote("doomed body", NoteIri));
        await persistence.Objects.PutObjectAsync(BuildNote("survivor body", otherIri));

        var delete = BuildDelete(LocalPerson, NoteIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, delete), delete);

        // Only the deleted object is tombstoned; the other remains a Note.
        Assert.IsType<Tombstone>(await GetAsync(persistence, NoteIri)!);
        Assert.IsType<Note>(await GetAsync(persistence, otherIri)!);
    }

    // --- Not stored / no object: no-op ------------------------------------------------------

    [Fact]
    public async Task HandleAsync_ObjectNotStored_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // The delete references an object this instance never stored → no-op (no tombstone created).
        var delete = BuildDelete(LocalPerson, NoteIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, delete), delete);

        Assert.False(await persistence.Objects.TryGetObjectAsync(NoteIri, out _));
    }

    [Fact]
    public async Task HandleAsync_NoObject_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Objects.PutObjectAsync(BuildNote("doomed body"));

        // A delete with no object → nothing to delete → no-op.
        var delete = new Delete
        {
            Id = $"{LocalPerson}/deletes/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(LocalPerson.Value) }],
        };
        await sut.HandleAsync(new InboxDelivery(LocalPerson, delete), delete);

        // The object is untouched (still a Note, not a tombstone).
        Assert.IsType<Note>(await GetAsync(persistence, NoteIri)!);
    }

    // --- Remote deleting actor: owner guard -------------------------------------------------

    [Fact]
    public async Task HandleAsync_RemoteActorDeletesStoredObject_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Objects.PutObjectAsync(BuildNote("doomed body"));

        // A remote actor purporting to delete a stored object → no-op (the owner guard).
        var delete = BuildDelete(RemotePerson, NoteIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, delete), delete);

        // The object is untouched (still a Note, not a tombstone).
        Assert.IsType<Note>(await GetAsync(persistence, NoteIri)!);
    }

    // --- Helpers --------------------------------------------------------------------------

    private static DeleteActivityHandler BuildHandler(
        IPersistenceProvider persistence,
        IDeliveryService? delivery = null)
        => new(
            persistence,
            new DefaultLocalActorResolver(persistence),
            new DeletePropagationService(persistence, delivery ?? new NoopDeliveryService(), new DefaultLocalActorResolver(persistence)));

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

    private static Note BuildNote(
        string content,
        Iri? iri = null,
        Iri? parentIri = null,
        Iri? attributedTo = null) => new()
    {
        Id = (iri ?? NoteIri).Value,
        Content = [content],
        AttributedTo = [new Link { Href = new Uri((attributedTo ?? LocalPerson).Value) }],
        InReplyTo = parentIri is not null ? [new Link { Href = new Uri(parentIri.Value.Value) }] : null,
    };

    private static Delete BuildDelete(Iri actorIri, Iri objectIri) => new()
    {
        Id = $"{actorIri}/deletes/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    private static async Task<IObject?> GetAsync(IPersistenceProvider persistence, Iri objectIri)
    {
        if (await persistence.Objects.TryGetObjectAsync(objectIri, out var obj))
        {
            return obj;
        }

        return null;
    }

    /// <summary>
    /// An <see cref="IDeliveryService"/> that records every scheduled delivery (instead of enqueuing) so
    /// a test can assert on <see cref="Delivered"/> — the target inbox, the activity, and the signing
    /// actor.
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

    private sealed class NoopDeliveryService : IDeliveryService
    {
        public Task DeliverAsync(Iri inboxIri, Activity activity, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeliverAsync(Iri inboxIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeliverToActorAsync(Iri recipientIri, Activity activity, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeliverToActorAsync(Iri recipientIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoopPropagationService : IDeletePropagationService
    {
        public Task PropagateUpdateAsync(Iri authorIri, Iri objectIri, Update activity, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task PropagateDeleteAsync(Iri authorIri, Iri objectIri, Delete activity, IObject? parentObject = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
