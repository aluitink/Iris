using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for the <see cref="UpdateActivityHandler"/> — the handler for an inbound
/// <see cref="Update"/> (an actor editing one of their objects). When the updating actor is a local
/// actor and the referenced object is one this instance stores in the <see cref="IObjectStore"/>, the
/// stored object is refreshed with the updated content (F-02). Covers: a local owner updating a stored
/// object (the stored content is replaced), updating an object this instance does not store (no-op), a
/// reference-only update (no content to apply → no-op), a remote (non-local) updating actor (no-op —
/// the owner guard), an update with no embedded object, and the null-guard contract.
/// </summary>
public sealed class UpdateActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemotePerson = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri NoteIri = new("https://b.domain.local/ap/v1/u/bob/notes/n1");

    // --- Local owner updates a stored object ------------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalOwnerUpdatesStoredObject_ReplacesStoredContent()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // The original object (as a Create would have stored it).
        await persistence.Objects.PutObjectAsync(BuildNote("original body"));

        // The owner updates it (embedded updated object).
        var update = BuildUpdate(LocalPerson, BuildNote("edited body"));
        await sut.HandleAsync(new InboxDelivery(LocalPerson, update), update);

        // The stored object now reflects the edit.
        Assert.True(await persistence.Objects.TryGetObjectAsync(NoteIri, out var stored));
        Assert.Equal("edited body", stored!.Content?.FirstOrDefault());
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

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UpdateActivityHandler(
            null!, new DefaultLocalActorResolver(new InMemoryPersistenceProvider())));
    }

    [Fact]
    public void Ctor_NullLocalActors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UpdateActivityHandler(
            new InMemoryPersistenceProvider(), null!));
    }

    // --- Helpers --------------------------------------------------------------------------

    private static UpdateActivityHandler BuildHandler(IPersistenceProvider persistence)
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

    private static Update BuildUpdate(Iri actorIri, Note objectToUpdate) => new()
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
}
