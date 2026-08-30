using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="RemoveActivityHandler"/> — the handler for the ActivityStreams
/// <see cref="Remove"/> collection-modification primitive (F-09). When the recipient (the inbox the
/// activity was delivered to) is a local <see cref="Group"/> (community), the activity's <c>object</c> is
/// removed from the community's member set via the <see cref="ICommunityStore"/>. Covers: removing a
/// member via <c>Remove</c>, the person-recipient no-op, the malformed (missing object) no-op, and the
/// null-guard contract.
/// </summary>
public sealed class RemoveActivityHandlerTests
{
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri Member = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri OtherMember = new("https://a.domain.local/ap/v1/u/dave");

    // --- Remove: a member is removed from a local community --------------------------------

    [Fact]
    public async Task HandleAsync_RemoveFromLocalCommunity_RemovesMember()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddMemberAsync(Community, Member);
        var sut = BuildHandler(persistence);

        var remove = BuildRemove(Community, Member);
        await sut.HandleAsync(new InboxDelivery(Community, remove), remove);

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
        await sut.HandleAsync(new InboxDelivery(Community, remove), remove);

        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
        Assert.False(await persistence.Communities.IsMemberAsync(Community, OtherMember));
    }

    // --- Recipient guards ------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_RemoveToPerson_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // A person's followers are maintained by the follow lifecycle, not Remove: a Remove to a person
        // is a no-op (no community to remove from).
        var remove = BuildRemove(LocalPerson, Member);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, remove), remove);

        Assert.Empty(await persistence.Communities.GetAllCommunityIrisAsync());
    }

    // --- Malformed activity guards ---------------------------------------------------------

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
        await sut.HandleAsync(new InboxDelivery(Community, remove), remove);

        // The existing member is untouched (the malformed Remove is ignored).
        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RemoveActivityHandler(null!));
    }

    [Fact]
    public void Dispatch_NullDelivery_Throws()
    {
        var sut = BuildHandler(new InMemoryPersistenceProvider());
        // The null-guard throws synchronously (before any await), so a sync wrapper keeps the
        // Assert.Throws<T> path (an async lambda would require Assert.ThrowsAsync).
        Assert.Throws<ArgumentNullException>(
            () => RunSync(() => sut.DispatchAsync(null!, BuildRemove(Community, Member))));
    }

    [Fact]
    public void Dispatch_NonRemoveActivity_Throws()
    {
        var sut = BuildHandler(new InMemoryPersistenceProvider());
        // A non-Remove activity reaching this dispatch is a programming error (the processor only
        // dispatches Remove here by exact type match). The base's cast guard throws synchronously (before
        // any await).
        var add = new Add
        {
            Id = $"{Community}/add-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(Member.Value) }],
            Object = [new Link { Href = new Uri(Community.Value) }],
        };
        Assert.Throws<InvalidOperationException>(
            () => RunSync(() => sut.DispatchAsync(new InboxDelivery(Community, add), add)));
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

    private static RemoveActivityHandler BuildHandler(IPersistenceProvider persistence)
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

    private static Remove BuildRemove(Iri communityIri, Iri memberIri) => new()
    {
        Id = $"{communityIri.Value}/remove-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(communityIri.Value) }],
        Object = [new Link { Href = new Uri(memberIri.Value) }],
    };
}
