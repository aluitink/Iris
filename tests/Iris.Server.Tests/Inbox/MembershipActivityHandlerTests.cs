using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="MembershipActivityHandler"/> — the handler for the ActivityStreams
/// community-membership primitives <see cref="Offer"/>, <see cref="Invite"/>, <see cref="Join"/>, and
/// <see cref="Leave"/> (F-16). When the recipient (the inbox the activity was delivered to) is a local
/// <see cref="Group"/> (community), the activity's <c>object</c> is added to / removed from the
/// community's member set via the <see cref="ICommunityStore"/>. Covers: adding a member via
/// <c>Offer</c>, <c>Invite</c>, and <c>Join</c>; removing a member via <c>Leave</c>; the
/// person-recipient no-op; the remote-recipient no-op; the malformed (missing object) no-op; the
/// idempotency of a re-delivered activity; and the null/programming-error guards.
/// </summary>
public sealed class MembershipActivityHandlerTests
{
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemoteRecipient = new("https://c.domain.local/ap/v1/c/other");
    private static readonly Iri Member = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri OtherMember = new("https://a.domain.local/ap/v1/u/dave");

    // --- Offer: an invited actor is added to a local community -------------------------------

    [Fact]
    public async Task HandleAsync_OfferToLocalCommunity_AddsMember()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        var offer = BuildMembership(OfferType.Offer, Community, Member);
        await sut.DispatchAsync(new InboxDelivery(Community, offer), offer);

        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    // --- Invite: an invited actor is added to a local community ------------------------------

    [Fact]
    public async Task HandleAsync_InviteToLocalCommunity_AddsMember()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        var invite = BuildMembership(OfferType.Invite, Community, Member);
        await sut.DispatchAsync(new InboxDelivery(Community, invite), invite);

        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    // --- Join: a joining actor is added to a local community ---------------------------------

    [Fact]
    public async Task HandleAsync_JoinToLocalCommunity_AddsMember()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        var join = BuildMembership(OfferType.Join, Community, Member);
        await sut.DispatchAsync(new InboxDelivery(Community, join), join);

        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    [Fact]
    public async Task HandleAsync_JoinMultipleMembers_AllAdded()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        await sut.DispatchAsync(
            new InboxDelivery(Community, BuildMembership(OfferType.Join, Community, Member)),
            BuildMembership(OfferType.Join, Community, Member));
        await sut.DispatchAsync(
            new InboxDelivery(Community, BuildMembership(OfferType.Join, Community, OtherMember)),
            BuildMembership(OfferType.Join, Community, OtherMember));

        var members = await persistence.Communities.GetMembersAsync(Community);
        Assert.Contains(Member, members);
        Assert.Contains(OtherMember, members);
    }

    // --- Leave: a leaving actor is removed from a local community ----------------------------

    [Fact]
    public async Task HandleAsync_LeaveFromLocalCommunity_RemovesMember()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddMemberAsync(Community, Member);
        var sut = BuildHandler(persistence);

        var leave = BuildMembership(OfferType.Leave, Community, Member);
        await sut.DispatchAsync(new InboxDelivery(Community, leave), leave);

        Assert.False(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    [Fact]
    public async Task HandleAsync_LeaveNonMember_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddMemberAsync(Community, Member);
        var sut = BuildHandler(persistence);

        // Leaving an actor that is not a member is a no-op; the existing member is untouched.
        var leave = BuildMembership(OfferType.Leave, Community, OtherMember);
        await sut.DispatchAsync(new InboxDelivery(Community, leave), leave);

        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
        Assert.False(await persistence.Communities.IsMemberAsync(Community, OtherMember));
    }

    // --- Idempotency: a re-delivered activity is safe to re-apply (C-07) --------------------

    [Fact]
    public async Task HandleAsync_OfferExistingMember_IsIdempotent()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddMemberAsync(Community, Member);
        var sut = BuildHandler(persistence);

        // A re-delivered Offer (at-least-once, C-07) must not fail or duplicate the membership.
        var offer = BuildMembership(OfferType.Offer, Community, Member);
        await sut.DispatchAsync(new InboxDelivery(Community, offer), offer);

        var members = await persistence.Communities.GetMembersAsync(Community);
        Assert.Equal(1, members.Count(m => m == Member));
    }

    [Fact]
    public async Task HandleAsync_LeaveExistingMemberTwice_SecondLeaveIsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddMemberAsync(Community, Member);
        var sut = BuildHandler(persistence);

        // A re-delivered Leave (at-least-once, C-07) is a no-op after the membership is removed.
        var leave = BuildMembership(OfferType.Leave, Community, Member);
        await sut.DispatchAsync(new InboxDelivery(Community, leave), leave);
        await sut.DispatchAsync(new InboxDelivery(Community, leave), leave);

        Assert.False(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    // --- Recipient guards --------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_OfferToPerson_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // A person has no member set to add to: an Offer to a person is a no-op.
        var offer = BuildMembership(OfferType.Offer, LocalPerson, Member);
        await sut.DispatchAsync(new InboxDelivery(LocalPerson, offer), offer);

        Assert.Empty(await persistence.Communities.GetAllCommunityIrisAsync());
    }

    [Fact]
    public async Task HandleAsync_LeaveToPerson_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        var leave = BuildMembership(OfferType.Leave, LocalPerson, Member);
        await sut.DispatchAsync(new InboxDelivery(LocalPerson, leave), leave);

        Assert.Empty(await persistence.Communities.GetAllCommunityIrisAsync());
    }

    [Fact]
    public async Task HandleAsync_OfferToUnknownRecipient_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        var sut = BuildHandler(persistence);

        // A recipient that is neither a local community nor person is not this instance's concern.
        var offer = BuildMembership(OfferType.Offer, RemoteRecipient, Member);
        await sut.DispatchAsync(new InboxDelivery(RemoteRecipient, offer), offer);

        Assert.Empty(await persistence.Communities.GetAllCommunityIrisAsync());
    }

    // --- Malformed activity guards -----------------------------------------------------------

    [Fact]
    public async Task HandleAsync_OfferWithNoObject_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        // An Offer with no resolvable object (the invited actor is unknown) is malformed; nothing is
        // added.
        var offer = new Offer
        {
            Id = $"{Community}/offer-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(Community.Value) }],
        };
        await sut.DispatchAsync(new InboxDelivery(Community, offer), offer);

        Assert.Empty(await persistence.Communities.GetMembersAsync(Community));
    }

    [Fact]
    public async Task HandleAsync_LeaveWithNoObject_IsNoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddMemberAsync(Community, Member);
        var sut = BuildHandler(persistence);

        var leave = new Leave
        {
            Id = $"{Community}/leave-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(Community.Value) }],
        };
        await sut.DispatchAsync(new InboxDelivery(Community, leave), leave);

        // The existing member is untouched (the malformed Leave is ignored).
        Assert.True(await persistence.Communities.IsMemberAsync(Community, Member));
    }

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MembershipActivityHandler(null!));
    }

    [Fact]
    public void Dispatch_NullDelivery_Throws()
    {
        var sut = BuildHandler(new InMemoryPersistenceProvider());
        // The null-guard throws synchronously (before any await), so a sync wrapper keeps the
        // Assert.Throws<T> path (an async lambda would require Assert.ThrowsAsync).
        Assert.Throws<ArgumentNullException>(
            () => RunSync(() => sut.DispatchAsync(null!, BuildMembership(OfferType.Join, Community, Member))));
    }

    [Fact]
    public void Dispatch_ForeignActivity_IsNoOp()
    {
        var sut = BuildHandler(new InMemoryPersistenceProvider());
        var follow = new Follow
        {
            Id = $"{Community}/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(Member.Value) }],
            Object = [new Link { Href = new Uri(Community.Value) }],
        };
        // A foreign activity can reach this dispatch via the processor's catch-all (this handler is
        // registered for the base Activity type and interprets any activity no more specific handler
        // covers). It is a graceful no-op — no throw, no membership change.
        RunSync(() => sut.DispatchAsync(new InboxDelivery(Community, follow), follow));
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// The four membership activity types this handler interprets (used to build the test activities).
    /// </summary>
    private enum OfferType { Offer, Invite, Join, Leave }

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

    private static MembershipActivityHandler BuildHandler(IPersistenceProvider persistence)
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

    private static Activity BuildMembership(OfferType type, Iri communityIri, Iri memberIri) => type switch
    {
        OfferType.Offer => new Offer
        {
            Id = $"{communityIri.Value}/offer-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(communityIri.Value) }],
            Object = [new Link { Href = new Uri(memberIri.Value) }],
        },
        OfferType.Invite => new Invite
        {
            Id = $"{communityIri.Value}/invite-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(communityIri.Value) }],
            Object = [new Link { Href = new Uri(memberIri.Value) }],
        },
        OfferType.Join => new Join
        {
            Id = $"{communityIri.Value}/join-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(memberIri.Value) }],
            Object = [new Link { Href = new Uri(memberIri.Value) }],
        },
        OfferType.Leave => new Leave
        {
            Id = $"{communityIri.Value}/leave-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(memberIri.Value) }],
            Object = [new Link { Href = new Uri(memberIri.Value) }],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
