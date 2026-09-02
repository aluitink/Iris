using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="BlockActivityHandler"/> (F-07 moderation): a <c>Block</c> records the
/// directed block edge <c>blocker → blocked</c> when <em>either</em> party is a local actor (a local
/// blocker's <c>blocks</c> collection lists the blocked actor; a local blocked actor is known to be
/// blocked). Covers the no-op guards (a block with no resolvable actor/object, a block between two
/// remote actors) and the null-guard contract.
/// </summary>
public sealed class BlockActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri LocalPerson2 = new("https://b.domain.local/ap/v1/u/carol");
    private static readonly Iri RemoteActor = new("https://a.domain.local/ap/v1/u/alice");

    // --- Local blocker: the block edge is recorded ----------------------------------------

    [Fact]
    public async Task HandleAsync_LocalBlocker_RecordsBlockEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var block = BuildBlock(LocalPerson, RemoteActor);

        await sut.HandleAsync(new InboxDelivery(RemoteActor, block), block);

        // The block edge is recorded (bob blocked alice) — the blocks collection lists it.
        Assert.True(await persistence.Moderation.IsBlockedAsync(LocalPerson, RemoteActor));
        Assert.Contains(RemoteActor, await persistence.Moderation.GetBlocksAsync(LocalPerson));
    }

    [Fact]
    public async Task HandleAsync_LocalBlocker_TwoBlocks_BothRecorded()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var block1 = BuildBlock(LocalPerson, RemoteActor);
        var block2 = BuildBlock(LocalPerson, new Iri("https://a.domain.local/ap/v1/u/dave"));

        await sut.HandleAsync(new InboxDelivery(RemoteActor, block1), block1);
        await sut.HandleAsync(new InboxDelivery(RemoteActor, block2), block2);

        var blocks = await persistence.Moderation.GetBlocksAsync(LocalPerson);
        Assert.Equal(2, blocks.Count);
        Assert.Contains(RemoteActor, blocks);
        Assert.Contains(new Iri("https://a.domain.local/ap/v1/u/dave"), blocks);
    }

    [Fact]
    public async Task HandleAsync_LocalBlockerOfLocalActor_RecordsBlockEdge()
    {
        // A local actor blocking another local actor is recorded the same way.
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        await SeedPersonAsync(persistence, LocalPerson2);
        var sut = BuildHandler(persistence);
        var block = BuildBlock(LocalPerson, LocalPerson2);

        await sut.HandleAsync(new InboxDelivery(LocalPerson2, block), block);

        Assert.True(await persistence.Moderation.IsBlockedAsync(LocalPerson, LocalPerson2));
    }

    [Fact]
    public async Task HandleAsync_LocalBlocker_RepeatedBlock_IsIdempotent()
    {
        // A repeated Block (a retry) does not duplicate the edge.
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var block = BuildBlock(LocalPerson, RemoteActor);

        await sut.HandleAsync(new InboxDelivery(RemoteActor, block), block);
        await sut.HandleAsync(new InboxDelivery(RemoteActor, block), block);

        var blocks = await persistence.Moderation.GetBlocksAsync(LocalPerson);
        Assert.Single(blocks);
    }

    // --- Local blocked (remote blocker): the edge is recorded (inverse query) --------------

    [Fact]
    public async Task HandleAsync_RemoteBlockerOfLocalActor_RecordsBlockEdge()
    {
        // A remote actor blocks a local actor (delivered to the local actor's inbox): the edge is
        // recorded so the instance knows the local actor is blocked (the inverse query lists the
        // blocker).
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var block = BuildBlock(RemoteActor, LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, block), block);

        // The forward query (LocalPerson's blocks) is empty (LocalPerson did not block anyone) ...
        Assert.Empty(await persistence.Moderation.GetBlocksAsync(LocalPerson));
        // ... but the inverse query (who blocked LocalPerson) lists the remote blocker.
        Assert.Contains(RemoteActor, await persistence.Moderation.GetBlockersAsync(LocalPerson));
        Assert.True(await persistence.Moderation.IsBlockedAsync(RemoteActor, LocalPerson));
    }

    // --- Remote blocker AND remote blocked: no edge is recorded ---------------------------

    [Fact]
    public async Task HandleAsync_BothRemote_DoesNotRecordEdge()
    {
        // A block between two remote actors is not this instance's concern: no edge is recorded.
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson); // a local actor exists, but is not a party
        var sut = BuildHandler(persistence);
        var remoteOther = new Iri("https://a.domain.local/ap/v1/u/dave");
        var block = BuildBlock(RemoteActor, remoteOther);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, block), block);

        Assert.False(await persistence.Moderation.IsBlockedAsync(RemoteActor, remoteOther));
        Assert.Empty(await persistence.Moderation.GetBlocksAsync(RemoteActor));
        Assert.Empty(await persistence.Moderation.GetBlockersAsync(remoteOther));
    }

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_BlockWithNoActor_RecordsNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var block = new Block
        {
            Id = "https://a.domain.local/activities/block-noactor",
            Object = [new Link { Href = new Uri(RemoteActor.Value) }],
        };

        await sut.HandleAsync(new InboxDelivery(RemoteActor, block), block);

        Assert.Empty(await persistence.Moderation.GetBlocksAsync(LocalPerson));
        Assert.Empty(await persistence.Moderation.GetBlockersAsync(RemoteActor));
    }

    [Fact]
    public async Task HandleAsync_BlockWithNoObject_RecordsNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var block = new Block
        {
            Id = "https://a.domain.local/activities/block-noobject",
            Actor = [new Link { Href = new Uri(LocalPerson.Value) }],
        };

        await sut.HandleAsync(new InboxDelivery(LocalPerson, block), block);

        Assert.Empty(await persistence.Moderation.GetBlocksAsync(LocalPerson));
    }

    // --- Null guards ----------------------------------------------------------------------

    // --- Helpers --------------------------------------------------------------------------

    private static BlockActivityHandler BuildHandler(IPersistenceProvider persistence)
        => new(persistence, new DefaultLocalActorResolver(persistence));

    private static Task SeedPersonAsync(IPersistenceProvider persistence, Iri actorIri)
    {
        var handle = new Uri(actorIri.Value).AbsolutePath.Trim('/').Split('/').Last();
        return persistence.Actors.PutActorAsync(new Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        });
    }

    private static Block BuildBlock(Iri blockerIri, Iri blockedIri) => new()
    {
        Id = $"{blockerIri}/blocks/{blockedIri.Value}",
        Actor = [new Link { Href = new Uri(blockerIri.Value) }],
        Object = [new Link { Href = new Uri(blockedIri.Value) }],
    };
}
