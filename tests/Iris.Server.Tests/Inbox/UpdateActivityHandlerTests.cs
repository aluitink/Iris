using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Media;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="UpdateActivityHandler"/> — the handler for an inbound
/// <see cref="Update"/> (an actor editing one of their objects). When the updating actor is a local
/// actor and the referenced object is one this instance stores in the <see cref="IObjectStore"/>, the
/// stored object is refreshed with the updated content (F-02) <em>and</em> the <see cref="Update"/> is
/// propagated to the author's remote followers (the federated half of F-02, via
/// <see cref="IDeletePropagationService"/>). Covers: a local owner updating a stored object (the stored
/// content is replaced and the Update is propagated to the remote follower), updating an object this
/// instance does not store (no-op, no propagation), a reference-only update (no content to apply →
/// no-op, no propagation), a remote (non-local) updating actor (no-op — the owner guard), an update
/// with no embedded object, an update with only local followers (no propagation), and the null-guard
/// contract.
/// </summary>
public sealed class UpdateActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemotePerson = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri NoteIri = new("https://b.domain.local/ap/v1/u/bob/notes/n1");
    private static readonly Iri LocalFollower = new("https://b.domain.local/ap/v1/u/carol");

    // --- Local owner updates a stored object ------------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalOwnerUpdatesStoredObject_ReplacesStoredContent()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        // The original object (as a Create would have stored it).
        await persistence.Objects.PutObjectAsync(BuildNote("original body"));

        // The owner updates it (embedded updated object).
        var update = BuildUpdate(LocalPerson, BuildNote("edited body"));
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        // The stored object now reflects the edit.
        Assert.True(await persistence.Objects.TryGetObjectAsync(NoteIri, out var stored));
        Assert.Equal("edited body", stored!.Content?.FirstOrDefault());

        // No followers recorded → nothing to propagate.
        Assert.Empty(delivery.Delivered);
    }

    // --- Federated propagation (F-02) ------------------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalOwnerUpdates_PropagatesToRemoteFollower()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemotePerson, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        await persistence.Objects.PutObjectAsync(BuildNote("original body"));

        var update = BuildUpdate(LocalPerson, BuildNote("edited body"));
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        // The Update is propagated to the remote follower's inbox, signed as the local owner.
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(RemotePerson.InboxOf(), job.InboxIri);
        Assert.Same(update, job.Activity);
        Assert.Equal(LocalPerson, job.ActorIri);
    }

    [Fact]
    public async Task HandleAsync_LocalOwnerUpdates_OnlyLocalFollowers_NoPropagation()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await SeedLocalActorAsync(persistence, LocalFollower);
        await persistence.Follows.RecordFollowAsync(LocalFollower, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        await persistence.Objects.PutObjectAsync(BuildNote("original body"));

        var update = BuildUpdate(LocalPerson, BuildNote("edited body"));
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        // A local follower's copy is on this instance (refreshed locally) → no cross-instance
        // delivery.
        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task HandleAsync_UpdateObjectNotStored_NoPropagation()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemotePerson, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        // The update references an object this instance never stored → no-op, and nothing is
        // propagated (the guard fires before the propagation).
        var update = BuildUpdate(LocalPerson, BuildNote("new body", NoteIri));
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        Assert.False(await persistence.Objects.TryGetObjectAsync(NoteIri, out _));
        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task HandleAsync_ReferenceOnlyUpdate_NoPropagation()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemotePerson, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        await persistence.Objects.PutObjectAsync(BuildNote("original body"));

        var update = new Update
        {
            Id = $"{LocalPerson}/updates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(LocalPerson.Value) }],
            Object = [new Link { Href = new Uri(NoteIri.Value) }],
        };
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        Assert.Equal("original body", (await GetAsync(persistence, NoteIri))!.Content?.FirstOrDefault());
        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task HandleAsync_RemoteActorUpdates_NoPropagation()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemotePerson, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        await persistence.Objects.PutObjectAsync(BuildNote("original body"));

        var update = BuildUpdate(RemotePerson, BuildNote("hijacked body", NoteIri));
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        // The owner guard fires before the propagation → nothing is delivered.
        Assert.Equal("original body", (await GetAsync(persistence, NoteIri))!.Content?.FirstOrDefault());
        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task HandleAsync_LocalOwnerUpdates_OtherStoredObjectsUntouched()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        var otherIri = new Iri($"{LocalPerson}/notes/n2");
        await persistence.Objects.PutObjectAsync(BuildNote("original body", NoteIri));
        await persistence.Objects.PutObjectAsync(BuildNote("other body", otherIri));

        var update = BuildUpdate(LocalPerson, BuildNote("edited body", NoteIri));
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        Assert.Equal("edited body", (await GetAsync(persistence, NoteIri))!.Content?.FirstOrDefault());
        Assert.Equal("other body", (await GetAsync(persistence, otherIri))!.Content?.FirstOrDefault());
    }

    // --- Not stored / reference-only: no-op -------------------------------------------------

    [Fact]
    public async Task HandleAsync_ObjectNotStored_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // The update references an object this instance never stored → no-op (no tombstone, no store).
        var update = BuildUpdate(LocalPerson, BuildNote("new body", NoteIri));
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        Assert.False(await persistence.Objects.TryGetObjectAsync(NoteIri, out _));
    }

    [Fact]
    public async Task HandleAsync_ReferenceOnlyUpdate_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Objects.PutObjectAsync(BuildNote("original body"));

        // A reference-only update (a Link, not an embedded object) has no new content to apply.
        var update = new Update
        {
            Id = $"{LocalPerson}/updates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(LocalPerson.Value) }],
            Object = [new Link { Href = new Uri(NoteIri.Value) }],
        };
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        // The stored object is unchanged.
        Assert.Equal("original body", (await GetAsync(persistence, NoteIri))!.Content?.FirstOrDefault());
    }

    // --- Remote updating actor: owner guard -------------------------------------------------

    [Fact]
    public async Task HandleAsync_RemoteActorUpdatesStoredObject_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Objects.PutObjectAsync(BuildNote("original body"));

        // A remote actor purporting to update a stored object → no-op (the owner guard).
        var update = BuildUpdate(RemotePerson, BuildNote("hijacked body", NoteIri));
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        Assert.Equal("original body", (await GetAsync(persistence, NoteIri))!.Content?.FirstOrDefault());
    }

    [Fact]
    public async Task HandleAsync_NoEmbeddedObject_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Objects.PutObjectAsync(BuildNote("original body"));

        // An update with no object → nothing to apply → no-op.
        var update = new Update
        {
            Id = $"{LocalPerson}/updates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(LocalPerson.Value) }],
        };
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        Assert.Equal("original body", (await GetAsync(persistence, NoteIri))!.Content?.FirstOrDefault());
    }

    // --- Actor self-update (profile editing) -------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalActorUpdatesOwnProfile_UpdatesStoredActor()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // The actor sends an Update with their own (updated) Person document.
        var updatedActor = new Person
        {
            Id = LocalPerson.Value,
            PreferredUsername = "bob",
            Name = ["Bobby"],
            Summary = ["A test summary"],
        };
        var update = BuildUpdate(LocalPerson, updatedActor);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        // The stored actor now reflects the edit.
        Assert.True(await persistence.Actors.TryGetActorAsync(LocalPerson, out var stored));
        Assert.Equal(["Bobby"], stored!.Name);
        Assert.Equal(["A test summary"], stored.Summary);
    }

    [Fact]
    public async Task HandleAsync_LocalActorUpdatesOwnProfile_PropagatesToRemoteFollower()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Follows.RecordFollowAsync(RemotePerson, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        var updatedActor = new Person
        {
            Id = LocalPerson.Value,
            PreferredUsername = "bob",
            Name = ["Bobby"],
            Summary = ["New bio"],
        };
        var update = BuildUpdate(LocalPerson, updatedActor);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        // The Update is propagated to the remote follower's inbox.
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(RemotePerson.InboxOf(), job.InboxIri);
        Assert.Same(update, job.Activity);
        Assert.Equal(LocalPerson, job.ActorIri);
    }

    [Fact]
    public async Task HandleAsync_LocalActorUpdatesOwnProfile_OnlyLocalFollowers_NoPropagation()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await SeedLocalActorAsync(persistence, LocalFollower);
        await persistence.Follows.RecordFollowAsync(LocalFollower, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        var updatedActor = new Person
        {
            Id = LocalPerson.Value,
            PreferredUsername = "bob",
            Name = ["Bobby"],
        };
        var update = BuildUpdate(LocalPerson, updatedActor);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task HandleAsync_RemoteActorUpdatesLocalActorProfile_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // A remote actor purporting to update a local actor's profile → the object IRI
        // (LocalPerson) does not match the updating actor's IRI (RemotePerson), so the
        // actor-update branch does not fire; the object-store path also doesn't fire
        // (no object stored at that IRI). No-op.
        var updatedActor = new Person
        {
            Id = LocalPerson.Value,
            PreferredUsername = "bob",
            Name = ["Hijacked"],
        };
        var update = BuildUpdate(RemotePerson, updatedActor);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        Assert.True(await persistence.Actors.TryGetActorAsync(LocalPerson, out var stored));
        Assert.NotEqual(["Hijacked"], stored!.Name);
    }

    [Fact]
    public async Task HandleAsync_ActorNotStored_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        var sut = BuildHandler(persistence);

        // The update references an actor this instance does not store → no-op.
        var updatedActor = new Person
        {
            Id = LocalPerson.Value,
            PreferredUsername = "bob",
            Name = ["New Name"],
        };
        var update = BuildUpdate(LocalPerson, updatedActor);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        Assert.False(await persistence.Actors.TryGetActorAsync(LocalPerson, out _));
    }

    // --- Helpers --------------------------------------------------------------------------

    private static UpdateActivityHandler BuildHandler(
        IPersistenceProvider persistence,
        IDeliveryService? delivery = null)
        => new(
            persistence,
            new DefaultLocalActorResolver(persistence),
            new DeletePropagationService(persistence, delivery ?? new NoopDeliveryService(), new DefaultLocalActorResolver(persistence)),
            new NoOpMediaWarmer(),
            Options.Create(new ActivityPubServerOptions()));

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

    private static Note BuildNote(string content, Iri? iri = null) => new()
    {
        Id = (iri ?? NoteIri).Value,
        Content = [content],
        AttributedTo = [new Link { Href = new Uri(LocalPerson.Value) }],
    };

    private static Update BuildUpdate(Iri actorIri, IObject objectToUpdate) => new()
    {
        Id = $"{actorIri}/updates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [objectToUpdate],
    };

    private static async Task<IObject?> GetAsync(IPersistenceProvider persistence, Iri objectIri)
    {
        if (await persistence.Objects.TryGetObjectAsync(objectIri, out var obj))
        {
            return obj;
        }

        return null;
    }

    [Fact]
    public async Task HandleAsync_LocalOwnerUpdatesNote_StampsUpdated()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);

        var originalNote = BuildNote("original body");
        originalNote.Published = DateTime.UtcNow.AddDays(-1);
        await persistence.Objects.PutObjectAsync(originalNote);

        var beforeEdit = DateTime.UtcNow;
        var update = BuildUpdate(LocalPerson, BuildNote("edited body"));
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        Assert.True(await persistence.Objects.TryGetObjectAsync(NoteIri, out var stored));
        var note = Assert.IsType<Note>(stored);
        Assert.Equal("edited body", note.Content?.FirstOrDefault());
        Assert.NotNull(note.Updated);
        Assert.True(note.Updated! >= beforeEdit, $"Updated ({note.Updated}) should be >= {beforeEdit}");
        Assert.True(note.Updated! >= originalNote.Published!, $"Updated ({note.Updated}) should be >= published ({originalNote.Published})");
    }

    /// <summary>
    /// A no-op <see cref="IMediaWarmer"/> for the unit tests (they do not exercise media warming).
    /// </summary>
    private sealed class NoOpMediaWarmer : IMediaWarmer
    {
        public Task WarmAsync(IObject? obj, Iri instanceBase, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
