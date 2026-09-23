using Iris.Server.InMemory;
using Iris.Server.Media;
using Iris.Server.Security;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Inbox;

public sealed class S67MentionNotificationTests
{
    private static readonly Iri LocalAlice = new("https://b.domain.local/ap/v1/u/alice");
    private static readonly Iri LocalBob = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemoteCaro = new("https://a.domain.local/ap/v1/u/caro");
    private static readonly Iri RemoteDave = new("https://remote2.domain/ap/v1/u/dave");

    [Fact]
    public async Task HandleAsync_RemotePostMentioningLocalActor_RecordsInMentionedInbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalAlice);
        await SeedLocalActorAsync(persistence, LocalBob);
        var sut = BuildHandler(persistence);

        var note = new Note
        {
            Id = $"{RemoteCaro.Value}/notes/{Guid.NewGuid():N}",
            Content = ["Hey @bob, check this out"],
            AttributedTo = [new Link { Href = new Uri(RemoteCaro.Value) }],
            Tag = [new Mention { Href = new Uri(LocalBob.Value) }]
        };
        var create = new Create
        {
            Id = $"{RemoteCaro.Value}/creates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(RemoteCaro.Value) }],
            Object = [note]
        };

        await sut.HandleAsync(new InboxDelivery(LocalAlice, create), create);

        var inbox = await persistence.Activities.GetInboxAsync(LocalBob);
        Assert.Contains(create.Id, InboxIds(inbox));
    }

    [Fact]
    public async Task HandleAsync_RemotePostMentioningRemoteActor_DoesNotRecordLocally()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalAlice);
        var sut = BuildHandler(persistence);

        var note = new Note
        {
            Id = $"{RemoteCaro.Value}/notes/{Guid.NewGuid():N}",
            Content = ["Hey @dave, check this out"],
            AttributedTo = [new Link { Href = new Uri(RemoteCaro.Value) }],
            Tag = [new Mention { Href = new Uri(RemoteDave.Value) }]
        };
        var create = new Create
        {
            Id = $"{RemoteCaro.Value}/creates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(RemoteCaro.Value) }],
            Object = [note]
        };

        await sut.HandleAsync(new InboxDelivery(LocalAlice, create), create);

        var inbox = await persistence.Activities.GetInboxAsync(LocalAlice);
        Assert.DoesNotContain(create.Id, InboxIds(inbox));
    }

    [Fact]
    public async Task HandleAsync_RemotePostMentioningRecipient_DoesNotDoubleRecord()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalAlice);
        var sut = BuildHandler(persistence);

        var note = new Note
        {
            Id = $"{RemoteCaro.Value}/notes/{Guid.NewGuid():N}",
            Content = ["Hey @alice, check this out"],
            AttributedTo = [new Link { Href = new Uri(RemoteCaro.Value) }],
            Tag = [new Mention { Href = new Uri(LocalAlice.Value) }]
        };
        var create = new Create
        {
            Id = $"{RemoteCaro.Value}/creates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(RemoteCaro.Value) }],
            Object = [note]
        };

        await sut.HandleAsync(new InboxDelivery(LocalAlice, create), create);

        // The recipient's own post is recorded in the outbox (J-8), NOT the inbox. The S67 mention
        // block skips the recipient (mentionedIri == recipient), so the inbox must be empty.
        var outbox = await persistence.Activities.GetOutboxAsync(LocalAlice);
        Assert.Contains(create.Id, OutboxIds(outbox));
        var inbox = await persistence.Activities.GetInboxAsync(LocalAlice);
        Assert.Empty(InboxIds(inbox));
    }

    [Fact]
    public async Task HandleAsync_RemotePostWithMultipleLocalMentions_RecordsInAllInboxes()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalAlice);
        await SeedLocalActorAsync(persistence, LocalBob);
        var sut = BuildHandler(persistence);

        var note = new Note
        {
            Id = $"{RemoteCaro.Value}/notes/{Guid.NewGuid():N}",
            Content = ["Hey @alice and @bob, check this out"],
            AttributedTo = [new Link { Href = new Uri(RemoteCaro.Value) }],
            Tag =
            [
                new Mention { Href = new Uri(LocalAlice.Value) },
                new Mention { Href = new Uri(LocalBob.Value) }
            ]
        };
        var create = new Create
        {
            Id = $"{RemoteCaro.Value}/creates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(RemoteCaro.Value) }],
            Object = [note]
        };

        await sut.HandleAsync(new InboxDelivery(LocalAlice, create), create);

        // Alice (recipient) sees her own post via the outbox; the mention block skips her.
        var aliceOutbox = await persistence.Activities.GetOutboxAsync(LocalAlice);
        Assert.Contains(create.Id, OutboxIds(aliceOutbox));
        var aliceInbox = await persistence.Activities.GetInboxAsync(LocalAlice);
        Assert.Empty(InboxIds(aliceInbox));

        // Bob (mentioned, not recipient) gets the Create in his inbox (the S67 notification).
        var bobInbox = await persistence.Activities.GetInboxAsync(LocalBob);
        Assert.Contains(create.Id, InboxIds(bobInbox));
    }

    // --- helpers (mirror CreateActivityHandlerTests) --------------------------------------

    private static CreateActivityHandler BuildHandler(
        IPersistenceProvider persistence, IDeliveryService? delivery = null)
        => new(
            persistence,
            delivery ?? new RecordingDeliveryService(),
            new DefaultLocalActorResolver(persistence),
            new NoOpMediaWarmer(),
            Options.Create(new ActivityPubServerOptions()));

    private sealed class NoOpMediaWarmer : IMediaWarmer
    {
        public Task WarmAsync(IObject? obj, Iri instanceBase, CancellationToken ct = default)
            => Task.CompletedTask;
    }

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

    private static List<string> InboxIds(IReadOnlyList<IObjectOrLink> inbox)
        => inbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();

    private static List<string> OutboxIds(IReadOnlyList<IObjectOrLink> outbox)
        => outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();

    private sealed class RecordingDeliveryService : IDeliveryService
    {
        public Task DeliverAsync(Iri inboxIri, Activity activity, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeliverAsync(Iri inboxIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeliverToActorAsync(Iri recipientIri, Activity activity, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeliverToActorAsync(Iri recipientIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
