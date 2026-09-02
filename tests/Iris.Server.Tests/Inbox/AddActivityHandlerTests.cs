using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="AddActivityHandler"/> — the handler for the ActivityStreams
/// <see cref="Add"/> collection-modification primitive (F-09). When the recipient (the inbox the
/// activity was delivered to) is a local <see cref="Group"/> (community), the activity's <c>object</c> is
/// added to the community's member set via the <see cref="ICommunityStore"/>. Covers: adding a member via
/// <c>Add</c>, the person-recipient no-op, the remote-recipient no-op, the malformed (missing object)
/// no-op, and the null-guard contract.
/// </summary>
public sealed class AddActivityHandlerTests
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
        await sut.HandleAsync(new InboxDelivery(Community, add), add);

        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    [Fact]
    public async Task HandleAsync_AddMultipleMembers_AllAdded()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        await sut.HandleAsync(new InboxDelivery(Community, BuildAdd(Community, Member)), BuildAdd(Community, Member));
        await sut.HandleAsync(new InboxDelivery(Community, BuildAdd(Community, OtherMember)), BuildAdd(Community, OtherMember));

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
        await sut.HandleAsync(new InboxDelivery(Community, BuildAdd(Community, Member)), BuildAdd(Community, Member));

        var members = await persistence.Communities.GetMembersAsync(Community);
        var matches = members.Count(m => m == Member);
        Assert.Equal(1, matches);
    }

    // --- Recipient guards ------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_AddToPerson_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // A person's followers are maintained by the follow lifecycle, not Add: an Add to a person is a
        // no-op (no community to add to).
        var add = BuildAdd(LocalPerson, Member);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, add), add);

        Assert.Empty(await persistence.Communities.GetAllCommunityIrisAsync());
    }

    [Fact]
    public async Task HandleAsync_AddToUnknownRecipient_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        var sut = BuildHandler(persistence);

        // A recipient that is neither a local community nor person is not this instance's concern.
        var add = BuildAdd(RemoteRecipient, Member);
        await sut.HandleAsync(new InboxDelivery(RemoteRecipient, add), add);

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
        await sut.HandleAsync(new InboxDelivery(Community, add), add);

        Assert.Empty(await persistence.Communities.GetMembersAsync(Community));
    }

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public void Dispatch_NonAddActivity_Throws()
    {
        var sut = BuildHandler(new InMemoryPersistenceProvider());
        // A non-Add activity reaching this dispatch is a programming error (the processor only dispatches
        // Add here by exact type match). The base's cast guard throws synchronously (before any await).
        var remove = new Remove
        {
            Id = $"{Community}/remove-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(Member.Value) }],
            Object = [new Link { Href = new Uri(Community.Value) }],
        };
        Assert.Throws<InvalidOperationException>(
            () => RunSync(() => sut.DispatchAsync(new InboxDelivery(Community, remove), remove)));
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

    private static AddActivityHandler BuildHandler(IPersistenceProvider persistence)
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
}
