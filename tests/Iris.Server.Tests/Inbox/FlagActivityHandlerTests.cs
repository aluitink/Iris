using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="FlagActivityHandler"/> (F-07 moderation): a <c>Flag</c> records the
/// directed flag edge <c>flagger → flagged</c> when <em>either</em> party is a local actor (a local
/// flagger's <c>flags</c> collection lists the flagged actor; a local flagged actor is known to have
/// been flagged). Unlike a <c>Block</c>, a <c>Flag</c> is a report (it does not sever the relationship),
/// so there is no feed/delivery application — only the edge and the <c>flags</c> collection. Covers the
/// no-op guards (a flag with no resolvable actor/object, a flag between two remote actors) and the
/// null-guard contract.
/// </summary>
public sealed class FlagActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri LocalPerson2 = new("https://b.domain.local/ap/v1/u/carol");
    private static readonly Iri RemoteActor = new("https://a.domain.local/ap/v1/u/alice");

    // --- Local flagger: the flag edge is recorded -------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalFlagger_RecordsFlagEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var flag = BuildFlag(LocalPerson, RemoteActor);

        await sut.HandleAsync(new InboxDelivery(RemoteActor, flag), flag);

        // The flag edge is recorded (bob flagged alice) — the flags collection lists it.
        Assert.True(await persistence.Moderation.HasFlaggedAsync(LocalPerson, RemoteActor));
        Assert.Contains(RemoteActor, await persistence.Moderation.GetFlagsAsync(LocalPerson));
    }

    [Fact]
    public async Task HandleAsync_LocalFlagger_TwoFlags_BothRecorded()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var flag1 = BuildFlag(LocalPerson, RemoteActor);
        var flag2 = BuildFlag(LocalPerson, new Iri("https://a.domain.local/ap/v1/u/dave"));

        await sut.HandleAsync(new InboxDelivery(RemoteActor, flag1), flag1);
        await sut.HandleAsync(new InboxDelivery(RemoteActor, flag2), flag2);

        var flags = await persistence.Moderation.GetFlagsAsync(LocalPerson);
        Assert.Equal(2, flags.Count);
        Assert.Contains(RemoteActor, flags);
        Assert.Contains(new Iri("https://a.domain.local/ap/v1/u/dave"), flags);
    }

    [Fact]
    public async Task HandleAsync_LocalFlaggerOfLocalActor_RecordsFlagEdge()
    {
        // A local actor flagging another local actor is recorded the same way.
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        await SeedPersonAsync(persistence, LocalPerson2);
        var sut = BuildHandler(persistence);
        var flag = BuildFlag(LocalPerson, LocalPerson2);

        await sut.HandleAsync(new InboxDelivery(LocalPerson2, flag), flag);

        Assert.True(await persistence.Moderation.HasFlaggedAsync(LocalPerson, LocalPerson2));
    }

    [Fact]
    public async Task HandleAsync_LocalFlagger_RepeatedFlag_IsIdempotent()
    {
        // A repeated Flag (a retry) does not duplicate the edge.
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var flag = BuildFlag(LocalPerson, RemoteActor);

        await sut.HandleAsync(new InboxDelivery(RemoteActor, flag), flag);
        await sut.HandleAsync(new InboxDelivery(RemoteActor, flag), flag);

        var flags = await persistence.Moderation.GetFlagsAsync(LocalPerson);
        Assert.Single(flags);
    }

    // --- Local flagged (remote flagger): the edge is recorded -------------------------------

    [Fact]
    public async Task HandleAsync_RemoteFlaggerOfLocalActor_RecordsFlagEdge()
    {
        // A remote actor flags a local actor (delivered to the local actor's inbox): the edge is
        // recorded so the instance knows the local actor was flagged (a moderation signal). The
        // local actor's forward flags collection stays empty (it flagged no one); the edge is visible
        // via the directed predicate.
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var flag = BuildFlag(RemoteActor, LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, flag), flag);

        Assert.Empty(await persistence.Moderation.GetFlagsAsync(LocalPerson));
        Assert.True(await persistence.Moderation.HasFlaggedAsync(RemoteActor, LocalPerson));
    }

    // --- Remote flagger AND remote flagged: no edge is recorded -----------------------------

    [Fact]
    public async Task HandleAsync_BothRemote_DoesNotRecordEdge()
    {
        // A flag between two remote actors is not this instance's concern: no edge is recorded.
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson); // a local actor exists, but is not a party
        var sut = BuildHandler(persistence);
        var remoteOther = new Iri("https://a.domain.local/ap/v1/u/dave");
        var flag = BuildFlag(RemoteActor, remoteOther);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, flag), flag);

        Assert.False(await persistence.Moderation.HasFlaggedAsync(RemoteActor, remoteOther));
        Assert.Empty(await persistence.Moderation.GetFlagsAsync(RemoteActor));
    }

    // --- Guards -----------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_FlagWithNoActor_RecordsNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var flag = new Flag
        {
            Id = "https://a.domain.local/activities/flag-noactor",
            Object = [new Link { Href = new Uri(RemoteActor.Value) }],
        };

        await sut.HandleAsync(new InboxDelivery(RemoteActor, flag), flag);

        Assert.Empty(await persistence.Moderation.GetFlagsAsync(LocalPerson));
    }

    [Fact]
    public async Task HandleAsync_FlagWithNoObject_RecordsNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var flag = new Flag
        {
            Id = "https://a.domain.local/activities/flag-noobject",
            Actor = [new Link { Href = new Uri(LocalPerson.Value) }],
        };

        await sut.HandleAsync(new InboxDelivery(LocalPerson, flag), flag);

        Assert.Empty(await persistence.Moderation.GetFlagsAsync(LocalPerson));
    }

    // --- Null guards ------------------------------------------------------------------------

    // --- Helpers ----------------------------------------------------------------------------

    private static FlagActivityHandler BuildHandler(IPersistenceProvider persistence)
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

    private static Flag BuildFlag(Iri flaggerIri, Iri flaggedIri) => new()
    {
        Id = $"{flaggerIri}/flags/{flaggedIri.Value}",
        Actor = [new Link { Href = new Uri(flaggerIri.Value) }],
        Object = [new Link { Href = new Uri(flaggedIri.Value) }],
    };
}
