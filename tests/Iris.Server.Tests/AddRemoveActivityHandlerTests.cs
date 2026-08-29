using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for the <see cref="AddRemoveActivityHandler"/> — the handler for the ActivityStreams
/// collection-modification primitives <see cref="Add"/> and <see cref="Remove"/> (F-09). When the
/// recipient (the inbox the activity was delivered to) is a local <see cref="Group"/> (community), the
/// activity's <c>object</c> is added to / removed from the community's member set via the
/// <see cref="ICommunityStore"/>. Covers: adding a member via <c>Add</c>, removing a member via
/// <c>Remove</c>, the person-recipient no-op, the remote-recipient no-op, the malformed (missing object)
/// no-op, and the null-guard contract.
/// </summary>
public sealed class AddRemoveActivityHandlerTests
{
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemoteRecipient = new("https://c.domain.local/ap/v1/c/other");
    private static readonly Iri Member = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri OtherMember = new("https://a.domain.local/ap/v1/u/dave");

    // --- Add: a member is added to a local community ---------------------------------------

    [Fact]
    public async Task HandleAsync_AddToLocalCommunity_AddsMember()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        var add = BuildAdd(Community, Member);
        await sut.DispatchAsync(new InboxDelivery(Community, add), add);

        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    [Fact]
    public async Task HandleAsync_AddMultipleMembers_AllAdded()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        await sut.DispatchAsync(new InboxDelivery(Community, BuildAdd(Community, Member)), BuildAdd(Community, Member));
        await sut.DispatchAsync(new InboxDelivery(Community, BuildAdd(Community, OtherMember)), BuildAdd(Community, OtherMember));

        var members = await persistence.Communities.GetMembersAsync(Community);
        Assert.Contains(Member, members);
        Assert.Contains(OtherMember, members);
    }

    [Fact]
    public async Task HandleAsync_AddExistingMember_IsIdempotent()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddMemberAsync(Community, Member);
        var sut = BuildHandler(persistence);

        // A re-delivered Add (at-least-once, C-07) must not fail or duplicate the membership.
        await sut.DispatchAsync(new InboxDelivery(Community, BuildAdd(Community, Member)), BuildAdd(Community, Member));

        var members = await persistence.Communities.GetMembersAsync(Community);
        var matches = members.Count(m => m == Member);
        Assert.Equal(1, matches);
    }

    // --- Remove: a member is removed from a local community --------------------------------

    [Fact]
    public async Task HandleAsync_RemoveFromLocalCommunity_RemovesMember()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddMemberAsync(Community, Member);
        var sut = BuildHandler(persistence);

        var remove = BuildRemove(Community, Member);
        await sut.DispatchAsync(new InboxDelivery(Community, remove), remove);

        Assert.False(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    [Fact]
    public async Task HandleAsync_RemoveNonMember_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddMemberAsync(Community, Member);
        var sut = BuildHandler(persistence);

        // Removing an actor that is not a member is a no-op; the existing member is untouched.
        var remove = BuildRemove(Community, OtherMember);
        await sut.DispatchAsync(new InboxDelivery(Community, remove), remove);

        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
        Assert.False(await persistence.Communities.IsMemberAsync(Community, OtherMember));
    }

    // --- Recipient guards ------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_AddToPerson_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // A person's followers are maintained by the follow lifecycle, not Add/Remove: an Add to a
        // person is a no-op (no community to add to).
        var add = BuildAdd(LocalPerson, Member);
        await sut.DispatchAsync(new InboxDelivery(LocalPerson, add), add);

        Assert.Empty(await persistence.Communities.GetAllCommunityIrisAsync());
    }

    [Fact]
    public async Task HandleAsync_AddToUnknownRecipient_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        var sut = BuildHandler(persistence);

        // A recipient that is neither a local community nor person is not this instance's concern.
        var add = BuildAdd(RemoteRecipient, Member);
        await sut.DispatchAsync(new InboxDelivery(RemoteRecipient, add), add);

        Assert.Empty(await persistence.Communities.GetAllCommunityIrisAsync());
    }

    [Fact]
    public async Task HandleAsync_RemoveToPerson_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        var remove = BuildRemove(LocalPerson, Member);
        await sut.DispatchAsync(new InboxDelivery(LocalPerson, remove), remove);

        Assert.Empty(await persistence.Communities.GetAllCommunityIrisAsync());
    }

    // --- Malformed activity guards ---------------------------------------------------------

    [Fact]
    public async Task HandleAsync_AddWithNoObject_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        // An Add with no resolvable object (the member is unknown) is malformed; nothing is added.
        var add = new Add
        {
            Id = $"{Community}/add-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(Community.Value) }],
        };
        await sut.DispatchAsync(new InboxDelivery(Community, add), add);

        Assert.Empty(await persistence.Communities.GetMembersAsync(Community));
    }

    [Fact]
    public async Task HandleAsync_RemoveWithNoObject_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddMemberAsync(Community, Member);
        var sut = BuildHandler(persistence);

        var remove = new Remove
        {
            Id = $"{Community}/remove-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(Community.Value) }],
        };
        await sut.DispatchAsync(new InboxDelivery(Community, remove), remove);

        // The existing member is untouched (the malformed Remove is ignored).
        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AddRemoveActivityHandler(null!));
    }

    [Fact]
    public void Dispatch_NullDelivery_Throws()
    {
        var sut = BuildHandler(new InMemoryPersistenceProvider());
        // The null-guard throws synchronously (before any await), so a sync wrapper keeps the
        // Assert.Throws<T> path (an async lambda would require Assert.ThrowsAsync).
        Assert.Throws<ArgumentNullException>(
            () => RunSync(() => sut.DispatchAsync(null!, BuildAdd(Community, Member))));
    }

    [Fact]
    public void Dispatch_UnsupportedActivity_Throws()
    {
        var sut = BuildHandler(new InMemoryPersistenceProvider());
        var follow = new Follow
        {
            Id = $"{Community}/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(Member.Value) }],
            Object = [new Link { Href = new Uri(Community.Value) }],
        };
        // A non-Add/Remove activity reaching this dispatch is a programming error (the processor only
        // dispatches Add/Remove here). The guard throws synchronously (before any await).
        Assert.Throws<InvalidOperationException>(
            () => RunSync(() => sut.DispatchAsync(new InboxDelivery(Community, follow), follow)));
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Runs a <see cref="Task"/>-returning action to completion, rethrowing any synchronous exception
    /// so <c>Assert.Throws</c> can observe it (used for the null/programming-error guards that throw
    /// before the first <c>await</c>).
    /// </summary>
    private static void RunSync(Func<Task> action) => action().GetAwaiter().GetResult();

    private static Group BuildCommunity() => new()
    {
        Id = Community.Value,
        Name = ["Iris"],
        PreferredUsername = "iris",
    };

    private static AddRemoveActivityHandler BuildHandler(IPersistenceProvider persistence)
        => new(persistence);

    private static Task SeedLocalPersonAsync(IPersistenceProvider persistence, Iri actorIri)
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

    private static Add BuildAdd(Iri communityIri, Iri memberIri) => new()
    {
        Id = $"{communityIri.Value}/add-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(communityIri.Value) }],
        Object = [new Link { Href = new Uri(memberIri.Value) }],
    };

    private static Remove BuildRemove(Iri communityIri, Iri memberIri) => new()
    {
        Id = $"{communityIri.Value}/remove-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(communityIri.Value) }],
        Object = [new Link { Href = new Uri(memberIri.Value) }],
    };
}
