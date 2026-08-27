using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for the <see cref="InboxProcessor"/> dispatch logic and the <see cref="FollowActivityHandler"/>.
/// These exercise the pure interpretation layer (no HTTP): the processor stores the delivered activity
/// and dispatches it to the registered <see cref="IActivityHandler"/> for its type.
/// </summary>
public sealed class InboxProcessorTests
{
    private readonly Iri RecipientIri = new("https://b.domain.local/ap/v1/u/bob");
    private readonly Iri FollowerIri = new("https://a.domain.local/ap/v1/u/alice");

    // --- Dispatch: a Follow is stored and recorded as a follow edge ----------------

    [Fact]
    public async Task ProcessAsync_Follow_WithFollowHandler_StoresActivityAndRecordsFollowEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        var processor = new InboxProcessor(persistence, [new FollowActivityHandler(persistence)]);
        var follow = BuildFollow(FollowerIri, RecipientIri);

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, follow));

        // The activity is stored under its IRI.
        Assert.True(await persistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out var stored));
        Assert.Equal(follow.Id, stored!.Id);

        // The FollowActivityHandler recorded the follow edge: alice follows bob.
        Assert.True(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
        var followers = await persistence.Follows.GetFollowersAsync(RecipientIri);
        Assert.Contains(FollowerIri, followers);
    }

    // --- Dispatch: an activity with no registered handler is stored, not dispatched --

    [Fact]
    public async Task ProcessAsync_ActivityWithoutMatchingHandler_StoresActivityAndDispatchesNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        var processor = new InboxProcessor(persistence, [new FollowActivityHandler(persistence)]);
        // A Like has no registered handler (only Follow is registered).
        var like = new Like
        {
            Id = "https://a.domain.local/activities/like-1",
            Actor = [new Link { Href = new Uri(FollowerIri.Value) }],
            Object = [new Link { Href = new Uri("https://a.domain.local/objects/note-1") }],
        };

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, like));

        // The activity is still stored (unknown activity types are preserved, not dropped).
        Assert.True(await persistence.Activities.TryGetActivityAsync(new Iri(like.Id!), out var stored));
        Assert.NotNull(stored);

        // Nothing was dispatched (no handler for Like); no follow edge recorded.
        Assert.False(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
    }

    // --- Dispatch: exact match wins over a base-type handler -------------------------

    [Fact]
    public async Task ProcessAsync_Activity_WithExactAndBaseHandlers_DispatchesToExactMatch()
    {
        var persistence = new InMemoryPersistenceProvider();
        var followHandler = new FollowActivityHandler(persistence);
        var baseHandler = new RecordingActivityHandler();
        var processor = new InboxProcessor(persistence, [baseHandler, followHandler]);
        var follow = BuildFollow(FollowerIri, RecipientIri);

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, follow));

        // The exact-match Follow handler ran (recorded the edge); the base Activity handler did not.
        Assert.True(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
        Assert.Empty(baseHandler.Handled);
    }

    // --- Dispatch: a base-type handler catches activities with no more specific match --

    [Fact]
    public async Task ProcessAsync_Activity_WithOnlyBaseHandler_DispatchesToClosestBase()
    {
        var persistence = new InMemoryPersistenceProvider();
        var baseHandler = new RecordingActivityHandler();
        var processor = new InboxProcessor(persistence, [baseHandler]);
        // A Like has no exact handler; the base Activity handler should catch it.
        var like = new Like
        {
            Id = "https://a.domain.local/activities/like-2",
            Actor = [new Link { Href = new Uri(FollowerIri.Value) }],
            Object = [new Link { Href = new Uri("https://a.domain.local/objects/note-2") }],
        };

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, like));

        Assert.Single(baseHandler.Handled);
        Assert.Equal(like, baseHandler.Handled[0].Activity);
    }

    // --- Handler: a Follow with no resolvable actor records nothing --------------------

    [Fact]
    public async Task ProcessAsync_FollowWithNoActor_StoresActivityAndRecordsNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        var processor = new InboxProcessor(persistence, [new FollowActivityHandler(persistence)]);
        // A Follow with no actor (malformed) — the handler records nothing.
        var follow = new Follow
        {
            Id = "https://a.domain.local/activities/follow-noactor",
            Object = [new Link { Href = new Uri(RecipientIri.Value) }],
        };

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, follow));

        // The activity is stored, but no follow edge is recorded.
        Assert.True(await persistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out _));
        Assert.False(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
    }

    // --- Handlers property exposes the registered handlers ----------------------------

    [Fact]
    public void Ctor_WithHandlers_ExposesThemViaHandlers()
    {
        var persistence = new InMemoryPersistenceProvider();
        var followHandler = new FollowActivityHandler(persistence);
        var processor = new InboxProcessor(persistence, [followHandler]);

        var handlers = processor.Handlers;
        Assert.Single(handlers);
        Assert.Same(followHandler, handlers[0]);
        Assert.Equal(typeof(Follow), handlers[0].HandledActivityType);
    }

    // --- Guards -------------------------------------------------------------------------

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new InboxProcessor(null!, [new FollowActivityHandler(new InMemoryPersistenceProvider())]));
    }

    [Fact]
    public async Task ProcessAsync_NullDelivery_Throws()
    {
        var persistence = new InMemoryPersistenceProvider();
        var processor = new InboxProcessor(persistence, [new FollowActivityHandler(persistence)]);

        await Assert.ThrowsAsync<ArgumentNullException>(() => processor.ProcessAsync(null!));
    }

    // --- Helpers ------------------------------------------------------------------------

    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Id = $"https://{new Uri(followerIri.Value).Host}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// A recording handler for the base <see cref="Activity"/> type, used to verify which handler
    /// the processor dispatches to (it catches any activity that has no more specific registered
    /// handler, because the processor walks the type hierarchy).
    /// </summary>
    private sealed class RecordingActivityHandler : ActivityHandlerBase<Activity>
    {
        public List<InboxDelivery> Handled { get; } = [];

        public override Task HandleAsync(InboxDelivery delivery, Activity activity, CancellationToken ct = default)
        {
            Handled.Add(delivery);
            return Task.CompletedTask;
        }
    }
}
