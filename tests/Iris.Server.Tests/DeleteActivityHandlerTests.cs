using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for the <see cref="DeleteActivityHandler"/> — the handler for an inbound
/// <see cref="Delete"/> (an actor deleting one of their objects). When the deleting actor is a local
/// actor and the referenced object is one this instance stores in the <see cref="IObjectStore"/>, the
/// stored object is replaced by an AS2.0 <see cref="Tombstone"/> (F-03/F-10) so a later <c>GET</c> of the
/// object's IRI serves the "deleted" marker, not a <c>404</c>. Covers: a local owner deleting a stored
/// object (the stored object becomes a Tombstone with the original <c>id</c> + <c>formerType</c>),
/// deleting an object this instance does not store (no-op), a remote (non-local) deleting actor (no-op
/// — the owner guard), a delete with no resolvable object, and the null-guard contract.
/// </summary>
public sealed class DeleteActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemotePerson = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri NoteIri = new("https://b.domain.local/ap/v1/u/bob/notes/n1");

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

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DeleteActivityHandler(
            null!, new DefaultLocalActorResolver(new InMemoryPersistenceProvider())));
    }

    [Fact]
    public void Ctor_NullLocalActors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DeleteActivityHandler(
            new InMemoryPersistenceProvider(), null!));
    }

    // --- Helpers --------------------------------------------------------------------------

    private static DeleteActivityHandler BuildHandler(IPersistenceProvider persistence)
        => new(persistence, new DefaultLocalActorResolver(persistence));

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
}
