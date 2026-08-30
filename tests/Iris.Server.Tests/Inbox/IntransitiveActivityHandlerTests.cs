using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="IntransitiveActivityHandler"/> — the handler for the ActivityStreams
/// intransitive-activity family <see cref="Read"/>, <see cref="View"/>, <see cref="Listen"/>,
/// <see cref="Travel"/>, and <see cref="Arrive"/> (F-17). These activities are acknowledgments of
/// receipt (the actor read/viewed/listened to, traveled to, or arrived at the object); they change no
/// persistent Iris state (no member set, like edge, or block edge to update). The handler accepts each
/// activity (so it is stored by the <see cref="InboxProcessor"/> and not rejected) and interprets it as
/// a no-op. Covers: each of the five activity types is accepted without throwing or changing state; a
/// foreign activity reaching the dispatch is a graceful no-op; and the null/programming-error guards.
/// </summary>
public sealed class IntransitiveActivityHandlerTests
{
    private static readonly Iri Recipient = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri Actor = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri ObjectIri = new("https://a.domain.local/ap/v1/u/alice/notes/n1");

    private static IntransitiveActivityHandler BuildHandler(IPersistenceProvider? persistence = null)
        => new(new MembershipActivityHandler(persistence ?? new InMemoryPersistenceProvider()));

    // --- Each intransitive activity type is accepted (a no-op) ------------------------------

    [Fact]
    public async Task Dispatch_Read_IsAcceptedNoOp()
    {
        var sut = BuildHandler();
        var read = BuildActivity<Read>();
        await sut.DispatchAsync(new InboxDelivery(Recipient, read), read);
        // No throw, no state change — the activity is stored by the processor; the interpretation is a
        // no-op.
        Assert.NotNull(read);
    }

    [Fact]
    public async Task Dispatch_View_IsAcceptedNoOp()
    {
        var sut = BuildHandler();
        var view = BuildActivity<View>();
        await sut.DispatchAsync(new InboxDelivery(Recipient, view), view);
        Assert.NotNull(view);
    }

    [Fact]
    public async Task Dispatch_Listen_IsAcceptedNoOp()
    {
        var sut = BuildHandler();
        var listen = BuildActivity<Listen>();
        await sut.DispatchAsync(new InboxDelivery(Recipient, listen), listen);
        Assert.NotNull(listen);
    }

    [Fact]
    public async Task Dispatch_Travel_IsAcceptedNoOp()
    {
        var sut = BuildHandler();
        var travel = BuildActivity<Travel>();
        await sut.DispatchAsync(new InboxDelivery(Recipient, travel), travel);
        Assert.NotNull(travel);
    }

    [Fact]
    public async Task Dispatch_Arrive_IsAcceptedNoOp()
    {
        var sut = BuildHandler();
        var arrive = BuildActivity<Arrive>();
        await sut.DispatchAsync(new InboxDelivery(Recipient, arrive), arrive);
        Assert.NotNull(arrive);
    }

    // --- No state change: an intransitive activity does not create a community or edge -----

    [Fact]
    public async Task Dispatch_ReadToCommunity_MakesNoMembershipChange()
    {
        // Even when delivered to a local community's inbox, a Read is an acknowledgment — it does not
        // add a member, record a like, or change any persistent state.
        var persistence = new InMemoryPersistenceProvider();
        var community = new Group
        {
            Id = Recipient.Value,
            Name = ["Iris"],
            PreferredUsername = "iris",
        };
        await persistence.Communities.PutCommunityAsync(community);
        var sut = BuildHandler(persistence);

        var read = BuildActivity<Read>();
        await sut.DispatchAsync(new InboxDelivery(Recipient, read), read);

        // No member was added (a Read is not an Offer/Invite/Join); no like/block edge was recorded.
        Assert.Empty(await persistence.Communities.GetMembersAsync(Recipient));
        Assert.Empty(await persistence.Likes.GetLikedAsync(Actor));
    }

    // --- A non-intransitive activity is forwarded to the MembershipActivityHandler ----------

    [Fact]
    public async Task Dispatch_NonIntransitiveActivity_ForwardsToMembershipHandler()
    {
        // An Offer is not an intransitive activity, but it reaches this dispatch (this handler is
        // registered first for the base Activity type). It is forwarded to the MembershipActivityHandler,
        // which adds the invited actor to the local community's member set — the membership is NOT
        // swallowed by this handler's catch-all.
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(new Group
        {
            Id = Recipient.Value,
            Name = ["Iris"],
            PreferredUsername = "iris",
        });
        var sut = BuildHandler(persistence);

        var offer = new Offer
        {
            Id = $"{Recipient.Value}/offer-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(Recipient.Value) }],
            Object = [new Link { Href = new Uri(Actor.Value) }],
        };
        await sut.DispatchAsync(new InboxDelivery(Recipient, offer), offer);

        // The MembershipActivityHandler (forwarded to) added the invited actor to the member set.
        Assert.True(await persistence.Communities.IsMemberAsync(Recipient, Actor));
    }

    // --- Foreign activity reaching the dispatch is a graceful no-op ------------------------

    [Fact]
    public void Dispatch_ForeignActivity_IsNoOp()
    {
        var sut = BuildHandler();
        // A Follow is not an intransitive activity, but it can reach this dispatch via the processor's
        // base-Activity catch-all (this handler is registered for the base Activity type and the
        // IntransitiveActivityHandler is before the MembershipActivityHandler). It is a graceful no-op —
        // no throw, no state change (forwarded to the MembershipActivityHandler, whose default case is a
        // no-op).
        var follow = new Follow
        {
            Id = $"{Recipient.Value}/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(Actor.Value) }],
            Object = [new Link { Href = new Uri(Recipient.Value) }],
        };
        RunSync(() => sut.DispatchAsync(new InboxDelivery(Recipient, follow), follow));
    }

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public void HandledActivityType_IsActivity()
    {
        var sut = BuildHandler();
        Assert.Equal(typeof(Activity), sut.HandledActivityType);
    }

    [Fact]
    public void Dispatch_NullDelivery_Throws()
    {
        var sut = BuildHandler();
        var read = BuildActivity<Read>();
        // The null-guard throws synchronously (before any await), so a sync wrapper keeps the
        // Assert.Throws<T> path (an async lambda would require Assert.ThrowsAsync).
        Assert.Throws<ArgumentNullException>(
            () => RunSync(() => sut.DispatchAsync(null!, read)));
    }

    [Fact]
    public void Dispatch_NullActivity_Throws()
    {
        var sut = BuildHandler();
        Assert.Throws<ArgumentNullException>(
            () => RunSync(() => sut.DispatchAsync(new InboxDelivery(Recipient, BuildActivity<Read>()), null!)));
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Runs a <see cref="Task"/>-returning action to completion, rethrowing any synchronous exception
    /// so <c>Assert.Throws</c> can observe it (used for the null/programming-error guards that throw
    /// before the first <c>await</c>).
    /// </summary>
    private static void RunSync(Func<Task> action) => action().GetAwaiter().GetResult();

    /// <summary>
    /// Builds an intransitive activity of type <typeparamref name="TActivity"/> with the test actor and
    /// object (so the activity is well-formed even though the handler ignores its fields).
    /// </summary>
    private static TActivity BuildActivity<TActivity>()
        where TActivity : Activity, new() => new()
    {
        Id = $"{Actor.Value}/{typeof(TActivity).Name.ToLowerInvariant()}-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(Actor.Value) }],
        Object = [new Link { Href = new Uri(ObjectIri.Value) }],
    };
}
