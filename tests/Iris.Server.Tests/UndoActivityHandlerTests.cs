using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for the <see cref="UndoActivityHandler"/> — the handler for an inbound
/// <see cref="Undo"/> (an un-follow). When the recipient (the follower) is a local person it removes the
/// <c>follower → target</c> edge from the <see cref="IFollowStore"/>; when the recipient is a local
/// community it removes the follow from the community's follows set. The target is resolved from the
/// original <see cref="Follow"/> (referenced by IRI in the Undo's object, fetched from the activity
/// store). Covers: person follow removal, community follow-set removal, a missing (unstored) follow, an
/// unknown follow IRI, a non-Follow referenced object, a remote (non-local) recipient, and the null-guard
/// contract.
/// </summary>
public sealed class UndoActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemoteTarget = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri RemotePerson = new("https://a.domain.local/ap/v1/u/remote");
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");

    // --- Person follower: remove the follow edge ---------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalPersonUndoesFollow_RemovesFollowEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // bob (local) follows alice (remote): the edge is recorded, then the follow is stored so the
        // Undo can resolve the target from it.
        await persistence.Follows.RecordFollowAsync(LocalPerson, RemoteTarget);
        var follow = BuildFollow(LocalPerson, RemoteTarget);
        await persistence.Activities.PutActivityAsync(follow);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, BuildUndo(follow)), BuildUndo(follow));

        // The follow edge is removed.
        Assert.False(await persistence.Follows.IsFollowingAsync(LocalPerson, RemoteTarget));
    }

    [Fact]
    public async Task HandleAsync_PersonUndoesFollow_OtherEdgesUntouched()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var other = new Iri("https://a.domain.local/ap/v1/u/carol");

        await persistence.Follows.RecordFollowAsync(LocalPerson, RemoteTarget);
        await persistence.Follows.RecordFollowAsync(LocalPerson, other);
        var follow = BuildFollow(LocalPerson, RemoteTarget);
        await persistence.Activities.PutActivityAsync(follow);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, BuildUndo(follow)), BuildUndo(follow));

        // Only the undone follow is removed; the other follow remains.
        Assert.False(await persistence.Follows.IsFollowingAsync(LocalPerson, RemoteTarget));
        Assert.True(await persistence.Follows.IsFollowingAsync(LocalPerson, other));
    }

    // --- Community follower: remove from the community's follows set -------------------------

    [Fact]
    public async Task HandleAsync_LocalCommunityUndoesFollow_RemovesFromCommunityFollows()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        // The community follows a remote actor (recorded in the community's follows set), then undoes it.
        await persistence.Communities.AddFollowAsync(Community, RemoteTarget);
        var follow = BuildFollow(Community, RemoteTarget);
        await persistence.Activities.PutActivityAsync(follow);

        await sut.HandleAsync(new InboxDelivery(Community, BuildUndo(follow)), BuildUndo(follow));

        // The follow is removed from the community's follows set.
        var follows = await persistence.Communities.GetFollowsAsync(Community);
        Assert.DoesNotContain(RemoteTarget, follows);
    }

    // --- Local person un-follows a local community: remove from the community's followers + follows sets (F-24)

    [Fact]
    public async Task HandleAsync_LocalPersonUndoesFollowOfLocalCommunity_RemovesCommunityFollowerAndFollow()
    {
        // F-24 inverse: when a local person follows a local community, the FollowActivityHandler records
        // BOTH the community's follows edge (community → follower) and the community's followers edge
        // (follower → community). The Undo (the person un-following the community) must remove BOTH, so
        // the community's `followers` collection no longer lists the person and the community no longer
        // follows the person.
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);

        // The person follows the community: both edges are recorded (as the FollowActivityHandler would).
        await persistence.Communities.AddFollowAsync(Community, LocalPerson);
        await persistence.Communities.AddFollowerAsync(Community, LocalPerson);
        var follow = BuildFollow(LocalPerson, Community);
        await persistence.Activities.PutActivityAsync(follow);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, BuildUndo(follow)), BuildUndo(follow));

        // The person is removed from BOTH the community's followers set and the community's follows set.
        var followers = await persistence.Communities.GetFollowersAsync(Community);
        Assert.DoesNotContain(LocalPerson, followers);
        var follows = await persistence.Communities.GetFollowsAsync(Community);
        Assert.DoesNotContain(LocalPerson, follows);
    }

    // --- Malformed / unresolvable: no-op ----------------------------------------------------

    [Fact]
    public async Task HandleAsync_FollowNotStored_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Follows.RecordFollowAsync(LocalPerson, RemoteTarget);

        // The Undo references a follow that was never stored → the target cannot be resolved → no-op.
        var follow = BuildFollow(LocalPerson, RemoteTarget);
        // (deliberately not PutActivityAsync)
        await sut.HandleAsync(new InboxDelivery(LocalPerson, BuildUndo(follow)), BuildUndo(follow));

        // The edge is untouched.
        Assert.True(await persistence.Follows.IsFollowingAsync(LocalPerson, RemoteTarget));
    }

    [Fact]
    public async Task HandleAsync_UnknownFollowIri_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Follows.RecordFollowAsync(LocalPerson, RemoteTarget);

        // The Undo references a follow IRI that does not exist in the store → no-op.
        var unknownFollowIri = new Iri($"{LocalPerson}/follows/{RemoteTarget}");
        var undo = BuildUndoReferencing(unknownFollowIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, undo), undo);

        Assert.True(await persistence.Follows.IsFollowingAsync(LocalPerson, RemoteTarget));
    }

    [Fact]
    public async Task HandleAsync_ObjectIsNotAFollow_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Follows.RecordFollowAsync(LocalPerson, RemoteTarget);

        // The Undo's object references a stored activity that is NOT a Follow (a Note) → no-op.
        var noteIri = new Iri($"{LocalPerson}/notes/{Guid.NewGuid():N}");
        await persistence.Activities.PutActivityAsync(new Note { Id = noteIri.Value, Content = ["hi"] });
        var undo = BuildUndoReferencing(noteIri);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, undo), undo);

        Assert.True(await persistence.Follows.IsFollowingAsync(LocalPerson, RemoteTarget));
    }

    [Fact]
    public async Task HandleAsync_NoObject_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Follows.RecordFollowAsync(LocalPerson, RemoteTarget);

        // The Undo has no object → nothing to undo → no-op.
        var undo = new Undo { Id = $"{LocalPerson}/undoes/{Guid.NewGuid():N}", Actor = [new Link { Href = new Uri(LocalPerson.Value) }] };
        await sut.HandleAsync(new InboxDelivery(LocalPerson, undo), undo);

        Assert.True(await persistence.Follows.IsFollowingAsync(LocalPerson, RemoteTarget));
    }

    // --- Un-block (F-07): Undo of a Block removes the block edge ---------------------------

    [Fact]
    public async Task HandleAsync_LocalBlockerUndoesBlock_RemovesBlockEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // bob (local) blocks alice (remote): the edge is recorded and the Block is stored, then the
        // Block is undone (an un-block).
        var block = BuildBlock(LocalPerson, RemoteTarget);
        await persistence.Moderation.RecordBlockAsync(LocalPerson, RemoteTarget);
        await persistence.Activities.PutActivityAsync(block);

        await sut.HandleAsync(new InboxDelivery(RemoteTarget, BuildUndo(block)), BuildUndo(block));

        // The block edge is removed (bob no longer has alice in his blocks).
        Assert.False(await persistence.Moderation.IsBlockedAsync(LocalPerson, RemoteTarget));
    }

    [Fact]
    public async Task HandleAsync_BlockOfLocalUndone_RemovesBlockEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // alice (remote) blocked bob (local) — the edge is recorded (bob is blocked). bob (the local
        // recipient) undoes the block he made of alice is the symmetric case; here the *remote* blocker
        // is undone by a remote Undo, but the removal is still scoped to the recorded edge. We assert the
        // inverse query (who blocked bob) is cleared when the edge is removed.
        var block = BuildBlock(RemoteTarget, LocalPerson);
        await persistence.Moderation.RecordBlockAsync(RemoteTarget, LocalPerson);
        await persistence.Activities.PutActivityAsync(block);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, BuildUndo(block)), BuildUndo(block));

        // The edge is removed: bob is no longer blocked by alice.
        Assert.False(await persistence.Moderation.IsBlockedAsync(RemoteTarget, LocalPerson));
        Assert.Empty(await persistence.Moderation.GetBlockersAsync(LocalPerson));
    }

    [Fact]
    public async Task HandleAsync_BlockNotStored_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Moderation.RecordBlockAsync(LocalPerson, RemoteTarget);

        // The Undo references a Block that was never stored → the edge cannot be resolved → no-op.
        var block = BuildBlock(LocalPerson, RemoteTarget);
        // (deliberately not PutActivityAsync)
        await sut.HandleAsync(new InboxDelivery(RemoteTarget, BuildUndo(block)), BuildUndo(block));

        // The edge is untouched.
        Assert.True(await persistence.Moderation.IsBlockedAsync(LocalPerson, RemoteTarget));
    }

    [Fact]
    public async Task HandleAsync_UnknownBlockIri_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Moderation.RecordBlockAsync(LocalPerson, RemoteTarget);

        // The Undo references a block IRI that does not exist in the store → no-op.
        var unknownBlockIri = new Iri($"{LocalPerson}/blocks/{RemoteTarget}");
        var undo = BuildUndoReferencing(unknownBlockIri);
        await sut.HandleAsync(new InboxDelivery(RemoteTarget, undo), undo);

        Assert.True(await persistence.Moderation.IsBlockedAsync(LocalPerson, RemoteTarget));
    }

    [Fact]
    public async Task HandleAsync_UndoOfBlock_DoesNotTouchFollowEdges()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Follows.RecordFollowAsync(LocalPerson, RemoteTarget);
        await persistence.Moderation.RecordBlockAsync(LocalPerson, RemoteTarget);

        // An un-block must only clear the block edge, leaving the follow edge intact.
        var block = BuildBlock(LocalPerson, RemoteTarget);
        await persistence.Activities.PutActivityAsync(block);
        await sut.HandleAsync(new InboxDelivery(RemoteTarget, BuildUndo(block)), BuildUndo(block));

        Assert.False(await persistence.Moderation.IsBlockedAsync(LocalPerson, RemoteTarget));
        Assert.True(await persistence.Follows.IsFollowingAsync(LocalPerson, RemoteTarget));
    }

    // --- Un-flag (F-07): Undo of a Flag removes the flag edge ------------------------------

    [Fact]
    public async Task HandleAsync_LocalFlaggerUndoesFlag_RemovesFlagEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // bob (local) flags alice (remote): the edge is recorded and the Flag is stored, then the
        // Flag is undone (an un-flag).
        var flag = BuildFlag(LocalPerson, RemoteTarget);
        await persistence.Moderation.RecordFlagAsync(LocalPerson, RemoteTarget);
        await persistence.Activities.PutActivityAsync(flag);

        await sut.HandleAsync(new InboxDelivery(RemoteTarget, BuildUndo(flag)), BuildUndo(flag));

        // The flag edge is removed (bob no longer has alice in his flags).
        Assert.False(await persistence.Moderation.HasFlaggedAsync(LocalPerson, RemoteTarget));
    }

    [Fact]
    public async Task HandleAsync_FlagOfLocalUndone_RemovesFlagEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);

        // alice (remote) flagged bob (local) — the edge is recorded (bob was flagged). The Undo (from
        // alice, the remote flagger) removes the recorded edge. We assert the directed predicate is
        // cleared when the edge is removed.
        var flag = BuildFlag(RemoteTarget, LocalPerson);
        await persistence.Moderation.RecordFlagAsync(RemoteTarget, LocalPerson);
        await persistence.Activities.PutActivityAsync(flag);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, BuildUndo(flag)), BuildUndo(flag));

        // The edge is removed: bob is no longer flagged by alice.
        Assert.False(await persistence.Moderation.HasFlaggedAsync(RemoteTarget, LocalPerson));
    }

    [Fact]
    public async Task HandleAsync_FlagNotStored_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Moderation.RecordFlagAsync(LocalPerson, RemoteTarget);

        // The Undo references a Flag that was never stored → the edge cannot be resolved → no-op.
        var flag = BuildFlag(LocalPerson, RemoteTarget);
        // (deliberately not PutActivityAsync)
        await sut.HandleAsync(new InboxDelivery(RemoteTarget, BuildUndo(flag)), BuildUndo(flag));

        // The edge is untouched.
        Assert.True(await persistence.Moderation.HasFlaggedAsync(LocalPerson, RemoteTarget));
    }

    [Fact]
    public async Task HandleAsync_UnknownFlagIri_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Moderation.RecordFlagAsync(LocalPerson, RemoteTarget);

        // The Undo references a flag IRI that does not exist in the store → no-op.
        var unknownFlagIri = new Iri($"{LocalPerson}/flags/{RemoteTarget}");
        var undo = BuildUndoReferencing(unknownFlagIri);
        await sut.HandleAsync(new InboxDelivery(RemoteTarget, undo), undo);

        Assert.True(await persistence.Moderation.HasFlaggedAsync(LocalPerson, RemoteTarget));
    }

    [Fact]
    public async Task HandleAsync_UndoOfFlag_DoesNotTouchBlockEdges()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        await persistence.Moderation.RecordFlagAsync(LocalPerson, RemoteTarget);
        await persistence.Moderation.RecordBlockAsync(LocalPerson, RemoteTarget);

        // An un-flag must only clear the flag edge, leaving the block edge intact.
        var flag = BuildFlag(LocalPerson, RemoteTarget);
        await persistence.Activities.PutActivityAsync(flag);
        await sut.HandleAsync(new InboxDelivery(RemoteTarget, BuildUndo(flag)), BuildUndo(flag));

        Assert.False(await persistence.Moderation.HasFlaggedAsync(LocalPerson, RemoteTarget));
        Assert.True(await persistence.Moderation.IsBlockedAsync(LocalPerson, RemoteTarget));
    }

    // --- Remote recipient: not this instance's concern --------------------------------------

    [Fact]
    public async Task HandleAsync_RemoteRecipient_NoOp()
    {
        // The recipient (follower) is a remote actor → the remote instance owns its follow state.
        var persistence = new InMemoryPersistenceProvider();
        var sut = BuildHandler(persistence);
        var follow = BuildFollow(RemotePerson, RemoteTarget);
        await persistence.Activities.PutActivityAsync(follow);

        await sut.HandleAsync(new InboxDelivery(RemotePerson, BuildUndo(follow)), BuildUndo(follow));

        // Nothing on this instance changes (no edge was ever recorded here).
        Assert.Empty(await persistence.Follows.GetFollowingAsync(RemotePerson));
    }

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UndoActivityHandler(
            null!, new DefaultLocalActorResolver(new InMemoryPersistenceProvider())));
    }

    [Fact]
    public void Ctor_NullLocalActors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UndoActivityHandler(
            new InMemoryPersistenceProvider(), null!));
    }

    // --- Helpers --------------------------------------------------------------------------

    private static Group BuildCommunity() => new()
    {
        Id = Community.Value,
        Name = ["Iris"],
        PreferredUsername = "iris",
    };

    private static UndoActivityHandler BuildHandler(IPersistenceProvider persistence)
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

    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Id = $"{followerIri}/follows/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    private static Block BuildBlock(Iri blockerIri, Iri blockedIri) => new()
    {
        Id = $"{blockerIri}/blocks/{blockedIri.Value}",
        Actor = [new Link { Href = new Uri(blockerIri.Value) }],
        Object = [new Link { Href = new Uri(blockedIri.Value) }],
    };

    private static Flag BuildFlag(Iri flaggerIri, Iri flaggedIri) => new()
    {
        Id = $"{flaggerIri}/flags/{flaggedIri.Value}",
        Actor = [new Link { Href = new Uri(flaggerIri.Value) }],
        Object = [new Link { Href = new Uri(flaggedIri.Value) }],
    };

    private static Undo BuildUndo(Activity activity)
    {
        var actorIri = activity.Actor!.First().ResolveObjectIri()!.Value;
        return new Undo
        {
            Id = $"{actorIri}/undoes/{activity.Id}",
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(activity.Id!) }],
        };
    }

    private static Undo BuildUndoReferencing(Iri objectIri) => new()
    {
        Id = $"{LocalPerson}/undoes/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(LocalPerson.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };
}
