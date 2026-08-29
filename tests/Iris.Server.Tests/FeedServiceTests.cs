using System.Net;
using System.Runtime.CompilerServices;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Options;
using CollectionPage = Iris.Core.CollectionPage;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for <see cref="FeedService"/> (F-14, the followed feed / home timeline): the merge of an
/// actor's local follows' outboxes (read from the local activity store) with the remote follows' outboxes
/// (fetched over the wire, walking each outbox's pages), de-duplicated by item IRI and capped by
/// <see cref="FeedOptions.MaxItems"/>.
/// </summary>
/// <remarks>
/// The local half is exercised against the real in-memory stores (follows + activities); the remote half
/// is exercised against a stub <see cref="IActivityPubClient"/> (the outbox collection document via
/// <see cref="IActivityPubClient.GetObjectAsync"/>, then each page via <see cref="IActivityPubClient.SendAsync"/>)
/// and a stub <see cref="IActorDocumentFetcher"/> (outbox-IRI resolution from the remote actor document).
/// A broken remote (404, non-page document) contributes nothing — it must not fail the whole feed.
/// </remarks>
public sealed class FeedServiceTests
{
    private const string LocalHost = "a.test";
    private const string RemoteHost = "b.test";

    private static Iri Actor(string host, string handle) => new($"https://{host}/ap/v1/u/{handle}");

    // --- Local follows ---------------------------------------------------------------

    [Fact]
    public async Task Feed_LocalOnlyFollows_ReturnsLocalOutboxItems()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob 1");
            AddPost(persistence, bob, "b-2", "bob 2");
        }));

        var alice = Actor(LocalHost, "alice");
        var feed = await service.GetFeedAsync(alice);

        // bob's outbox is newest-first (b-2 then b-1).
        Assert.Equal(2, feed.Count);
        Assert.Equal($"https://{LocalHost}/notes/b-2", IdOf(feed[0]));
        Assert.Equal($"https://{LocalHost}/notes/b-1", IdOf(feed[1]));
    }

    [Fact]
    public async Task Feed_NoFollows_ReturnsEmpty()
    {
        var (service, _) = Build(persistence: new InMemoryPersistenceProvider());
        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));
        Assert.Empty(feed);
    }

    [Fact]
    public async Task Feed_LocalFollowWithEmptyOutbox_ContributesNothing()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var carol = Actor(LocalHost, "carol");
            SeedActor(persistence, carol, "Carol");
            persistence.Follows.RecordFollowAsync(alice, carol).GetAwaiter().GetResult();
            // carol has no posts.
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));
        Assert.Empty(feed);
    }

    [Fact]
    public async Task Feed_MultipleLocalFollows_MergesInIriOrder()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var dave = Actor(LocalHost, "dave");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, dave, "Dave");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            persistence.Follows.RecordFollowAsync(alice, dave).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob 1");
            AddPost(persistence, dave, "d-1", "dave 1");
            AddPost(persistence, dave, "d-2", "dave 2");
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Deterministic IRI order across follows: bob (.../u/bob) sorts before dave (.../u/dave).
        // bob's outbox (b-1) then dave's outbox (d-2, d-1 newest-first).
        Assert.Equal(3, feed.Count);
        Assert.Equal($"https://{LocalHost}/notes/b-1", IdOf(feed[0]));
        Assert.Equal($"https://{LocalHost}/notes/d-2", IdOf(feed[1]));
        Assert.Equal($"https://{LocalHost}/notes/d-1", IdOf(feed[2]));
    }

    // --- Block filtering (F-07: apply the block edge) ---------------------------------

    [Fact]
    public async Task Feed_BlockedLocalFollow_IsExcludedFromFeed()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            // Alice blocked bob (the block edge alice → bob): bob's content must not appear in alice's feed.
            persistence.Moderation.RecordBlockAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob 1");
            AddPost(persistence, bob, "b-2", "bob 2");
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // bob is followed AND blocked: the block wins, so the feed is empty (bob contributes nothing).
        Assert.Empty(feed);
    }

    [Fact]
    public async Task Feed_BlockedRemoteFollow_IsExcludedFromFeed()
    {
        var remote = Actor(RemoteHost, "bob");
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                var alice = Actor(LocalHost, "alice");
                persistence.Follows.RecordFollowAsync(alice, remote).GetAwaiter().GetResult();
                // Alice blocked the remote bob: the block edge (alice → bob) excludes the remote outbox.
                persistence.Moderation.RecordBlockAsync(alice, remote).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(Pages(
                Page($"{remote.Value}/outbox", [Item("r-1")], next: null))));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // The remote bob is blocked: its outbox is not merged (the feed is empty).
        Assert.Empty(feed);
    }

    [Fact]
    public async Task Feed_PartialBlock_KeepsUnblockedFollows()
    {
        // Alice follows bob and dave, blocks only bob: dave's content is kept, bob's is excluded.
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var dave = Actor(LocalHost, "dave");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, dave, "Dave");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            persistence.Follows.RecordFollowAsync(alice, dave).GetAwaiter().GetResult();
            persistence.Moderation.RecordBlockAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob 1");
            AddPost(persistence, dave, "d-1", "dave 1");
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Only dave's post survives (bob is blocked).
        Assert.Single(feed);
        Assert.Equal($"https://{LocalHost}/notes/d-1", IdOf(feed[0]));
    }

    // --- Mute filtering (F-07: apply the mute edge) ----------------------------------

    [Fact]
    public async Task Feed_MutedLocalFollow_IsExcludedFromFeed()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            // Alice muted bob (the mute edge alice → bob): bob's content must not appear in alice's
            // feed (a soft exclusion — the follow is kept, only its content is hidden).
            persistence.Moderation.RecordMuteAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob 1");
            AddPost(persistence, bob, "b-2", "bob 2");
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // bob is followed AND muted: the mute wins, so the feed is empty (bob contributes nothing).
        Assert.Empty(feed);
    }

    [Fact]
    public async Task Feed_MutedRemoteFollow_IsExcludedFromFeed()
    {
        var remote = Actor(RemoteHost, "bob");
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                var alice = Actor(LocalHost, "alice");
                persistence.Follows.RecordFollowAsync(alice, remote).GetAwaiter().GetResult();
                // Alice muted the remote bob: the mute edge (alice → bob) excludes the remote outbox.
                persistence.Moderation.RecordMuteAsync(alice, remote).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(Pages(
                Page($"{remote.Value}/outbox", [Item("r-1")], next: null))));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // The remote bob is muted: its outbox is not merged (the feed is empty).
        Assert.Empty(feed);
    }

    [Fact]
    public async Task Feed_PartialMute_KeepsUnmutedFollows()
    {
        // Alice follows bob and dave, mutes only bob: dave's content is kept, bob's is excluded.
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var dave = Actor(LocalHost, "dave");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, dave, "Dave");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            persistence.Follows.RecordFollowAsync(alice, dave).GetAwaiter().GetResult();
            persistence.Moderation.RecordMuteAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob 1");
            AddPost(persistence, dave, "d-1", "dave 1");
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Only dave's post survives (bob is muted).
        Assert.Single(feed);
        Assert.Equal($"https://{LocalHost}/notes/d-1", IdOf(feed[0]));
    }

    [Fact]
    public async Task Feed_MuteDoesNotSeverFollow_UnlikeBlock()
    {
        // A mute is a soft exclusion (the follow is kept, only the content is hidden) — unlike a block,
        // which severs the relationship. Pin the mute edge: bob is still in alice's following (the
        // follow edge is intact), and removing the mute restores bob's content to the feed.
        var (service, persistence) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            persistence.Moderation.RecordMuteAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob 1");
        }));

        var alice = Actor(LocalHost, "alice");
        var bob = Actor(LocalHost, "bob");

        // The follow edge survives the mute (a mute does not sever the relationship).
        Assert.Contains(bob, await persistence.Follows.GetFollowingAsync(alice));
        // But bob's content is hidden from the feed.
        Assert.Empty(await service.GetFeedAsync(alice));

        // Un-muting (removing the mute edge) restores bob's content to the feed (the follow was never
        // severed).
        await persistence.Moderation.RemoveMuteAsync(alice, bob);
        var restored = await service.GetFeedAsync(alice);
        Assert.Single(restored);
        Assert.Equal($"https://{LocalHost}/notes/b-1", IdOf(restored[0]));
    }

    [Fact]
    public async Task Feed_NoModerationStore_IncludesAllFollows()
    {
        // Without a moderation store (moderation disabled), a recorded block does not exist, so every
        // follow is merged (the pre-F-07 behavior). The service is built WITHOUT a moderation store.
        var persistence = new InMemoryPersistenceProvider();
        var alice = Actor(LocalHost, "alice");
        var bob = Actor(LocalHost, "bob");
        SeedActor(persistence, bob, "Bob");
        await persistence.Follows.RecordFollowAsync(alice, bob);
        AddPost(persistence, bob, "b-1", "bob 1");
        var localActors = new LocalOnlyResolver(persistence);
        var service = new FeedService(persistence, localActors, new StubActorDocumentFetcher(_ => null),
            new StubClient(Pages()), Options.Create(new FeedOptions()));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        Assert.Single(feed);
        Assert.Equal($"https://{LocalHost}/notes/b-1", IdOf(feed[0]));
    }

    // --- Remote follows --------------------------------------------------------------

    [Fact]
    public async Task Feed_RemoteFollow_WalksOutboxPages()
    {
        var remote = Actor(RemoteHost, "bob");
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(Actor(LocalHost, "alice"), remote).GetAwaiter().GetResult();
            }),
            options: new FeedOptions { PagesPerActor = 2 },
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(
                Pages(
                    Page($"{remote.Value}/outbox", [Item("r-1")], next: $"{remote.Value}/outbox?page=2"),
                    Page($"{remote.Value}/outbox?page=2", [Item("r-2")], next: null))));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // With PagesPerActor = 2 both pages are walked, in order: r-1 (page 1) then r-2 (page 2).
        Assert.Equal(2, feed.Count);
        Assert.Equal($"https://{RemoteHost}/notes/r-1", IdOf(feed[0]));
        Assert.Equal($"https://{RemoteHost}/notes/r-2", IdOf(feed[1]));
    }

    [Fact]
    public async Task Feed_RemoteFollow_PagesPerActorCapsWalk()
    {
        var remote = Actor(RemoteHost, "bob");
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(Actor(LocalHost, "alice"), remote).GetAwaiter().GetResult();
            }),
            options: new FeedOptions { PagesPerActor = 1 },
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(Pages(
                Page($"{remote.Value}/outbox", [Item("r-1")], next: $"{remote.Value}/outbox?page=2"),
                Page($"{remote.Value}/outbox?page=2", [Item("r-2")], next: null))));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Only the first page is walked (PagesPerActor = 1): r-1 only, not r-2.
        Assert.Single(feed);
        Assert.Equal($"https://{RemoteHost}/notes/r-1", IdOf(feed[0]));
    }

    [Fact]
    public async Task Feed_RemoteFollow_OutboxCollectionFollowsFirstLink()
    {
        var remote = Actor(RemoteHost, "bob");
        var outboxIri = $"{remote.Value}/outbox";
        var firstPageIri = $"{outboxIri}/first";
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(Actor(LocalHost, "alice"), remote).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri(outboxIri) };
                return actor;
            }),
            client: new StubClient(Pages(
                // The outbox IRI is an OrderedCollection (not a page); its `first` points to the first page.
                Collection(outboxIri, first: firstPageIri),
                Page(firstPageIri, [Item("r-1")], next: null))));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        Assert.Single(feed);
        Assert.Equal($"https://{RemoteHost}/notes/r-1", IdOf(feed[0]));
    }

    [Fact]
    public async Task Feed_RemoteFollow_BrokenRemote_ContributesNothing()
    {
        var remote = Actor(RemoteHost, "bob");
        // The remote's outbox IRI 404s (the stub returns no collection document for it) — the remote
        // contributes nothing, but the feed (empty here) still completes.
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(Actor(LocalHost, "alice"), remote).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(Pages())); // no documents at all

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));
        Assert.Empty(feed);
    }

    [Fact]
    public async Task Feed_RemoteFollow_DocumentFetchFails_FallsBackToConventionalOutbox()
    {
        var remote = Actor(RemoteHost, "bob");
        // The actor document cannot be fetched (null) → the service falls back to the conventional
        // {actor}/outbox IRI, which the stub serves a collection + page for.
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(Actor(LocalHost, "alice"), remote).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(_ => null),
            client: new StubClient(Pages(
                Page($"{remote.Value}/outbox", [Item("r-1")], next: null))));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));
        Assert.Single(feed);
        Assert.Equal($"https://{RemoteHost}/notes/r-1", IdOf(feed[0]));
    }

    // --- Mixed local + remote, dedup, cap --------------------------------------------

    [Fact]
    public async Task Feed_MixedLocalAndRemote_MergesBoth()
    {
        var remote = Actor(RemoteHost, "bob");
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                var alice = Actor(LocalHost, "alice");
                var localBob = Actor(LocalHost, "localbob");
                SeedActor(persistence, localBob, "LocalBob");
                persistence.Follows.RecordFollowAsync(alice, localBob).GetAwaiter().GetResult();
                persistence.Follows.RecordFollowAsync(alice, remote).GetAwaiter().GetResult();
                AddPost(persistence, localBob, "lb-1", "local bob 1");
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(Pages(
                Page($"{remote.Value}/outbox", [Item("r-1")], next: null))));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Both the local (localbob) and remote (bob) follows contribute.
        Assert.Equal(2, feed.Count);
        Assert.Contains(feed, i => IdOf(i) == "https://a.test/notes/lb-1");
        Assert.Contains(feed, i => IdOf(i) == "https://b.test/notes/r-1");
    }

    [Fact]
    public async Task Feed_DuplicateItemIri_AppearsOnce()
    {
        // The same item IRI appears in two local follows' outboxes (a cross-post scenario). It must
        // appear only once in the feed (de-duplicated by IRI, keeping the first occurrence).
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var dave = Actor(LocalHost, "dave");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, dave, "Dave");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            persistence.Follows.RecordFollowAsync(alice, dave).GetAwaiter().GetResult();
            const string sharedIri = "https://a.test/notes/shared";
            AddPostWithId(persistence, bob, sharedIri, "shared (bob)");
            AddPost(persistence, bob, "b-2", "bob 2");
            AddPostWithId(persistence, dave, sharedIri, "shared (dave)");
            AddPost(persistence, dave, "d-2", "dave 2");
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        Assert.Equal(3, feed.Count);
        Assert.Equal(1, feed.Count(i => IdOf(i) == "https://a.test/notes/shared"));
    }

    [Fact]
    public async Task Feed_MaxItemsCapsTheFeed()
    {
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                var alice = Actor(LocalHost, "alice");
                var bob = Actor(LocalHost, "bob");
                SeedActor(persistence, bob, "Bob");
                persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
                AddPost(persistence, bob, "b-1", "1");
                AddPost(persistence, bob, "b-2", "2");
                AddPost(persistence, bob, "b-3", "3");
            }),
            options: new FeedOptions { MaxItems = 2 });

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        Assert.Equal(2, feed.Count);
        Assert.Equal($"https://{LocalHost}/notes/b-3", IdOf(feed[0]));
        Assert.Equal($"https://{LocalHost}/notes/b-2", IdOf(feed[1]));
    }

    // --- Builders --------------------------------------------------------------------

    private static (FeedService Service, InMemoryPersistenceProvider Persistence) Build(
        InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? actorDocs = null,
        IActivityPubClient? client = null,
        FeedOptions? options = null)
    {
        actorDocs ??= new StubActorDocumentFetcher(_ => null);
        client ??= new StubClient(Pages());
        var localActors = new LocalOnlyResolver(persistence);
        return (
            new FeedService(persistence, localActors, actorDocs, client, Options.Create(options ?? new FeedOptions())),
            persistence);
    }

    private static InMemoryPersistenceProvider SeedLocal(Action<InMemoryPersistenceProvider> seed)
    {
        var persistence = new InMemoryPersistenceProvider();
        seed(persistence);
        return persistence;
    }

    /// <summary>
    /// Seeds a local actor into the actor store (so the <see cref="LocalOnlyResolver"/> recognizes the IRI
    /// as local). Idempotent.
    /// </summary>
    private static void SeedActor(InMemoryPersistenceProvider persistence, Iri actorIri, string name)
        => persistence.Actors.PutActorAsync(new Person { Id = actorIri.Value, Name = [name] })
            .GetAwaiter().GetResult();

    private static void AddPost(InMemoryPersistenceProvider persistence, Iri actorIri, string suffix, string content)
        => AddPostWithId(persistence, actorIri, $"https://{LocalHost}/notes/{suffix}", content);

    private static void AddPostWithId(InMemoryPersistenceProvider persistence, Iri actorIri, string activityIri, string content)
        => persistence.Activities.AddToOutboxAsync(actorIri, new Create
        {
            Id = activityIri,
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Note { Id = $"{activityIri}#note", Content = [content] }],
        }).GetAwaiter().GetResult();

    private static string? IdOf(IObjectOrLink item) => item switch
    {
        IObject { Id: { } id } => id,
        ILink { Href: { } href } => href.ToString(),
        _ => null,
    };

    private static IReadOnlyList<(Iri Iri, IObject Doc)> Pages(params (Iri Iri, IObject Doc)[] docs) => docs;

    private static (Iri Iri, IObject Doc) Page(string iri, IReadOnlyList<IObjectOrLink> items, string? next)
    {
        var page = new OrderedCollectionPage { Id = iri, Items = items };
        if (next is not null)
        {
            page.Next = new Link { Href = new Uri(next) };
        }

        return (new Iri(iri), page);
    }

    private static (Iri Iri, IObject Doc) Collection(string iri, string first)
    {
        var collection = new OrderedCollection { Id = iri, First = new Link { Href = new Uri(first) } };
        return (new Iri(iri), collection);
    }

    private static Link Item(string suffix) => new() { Href = new Uri($"https://{RemoteHost}/notes/{suffix}") };

    // --- Stubs -----------------------------------------------------------------------

    /// <summary>
    /// An <see cref="ILocalActorResolver"/> that treats every actor present in the persistence provider's
    /// actor store as local (and every other actor as remote) — the same rule
    /// <see cref="DefaultLocalActorResolver"/> applies, but backed by the in-memory actor store the test
    /// seeds (so a remote actor IRI that was never seeded on this instance resolves as remote).
    /// </summary>
    private sealed class LocalOnlyResolver(IPersistenceProvider persistence) : ILocalActorResolver
    {
        public async Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default)
            => await persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A stub <see cref="IActorDocumentFetcher"/>: returns the actor built by the factory for the given
    /// IRI (or null when the factory returns null).
    /// </summary>
    private sealed class StubActorDocumentFetcher(Func<Iri, Actor?> factory) : IActorDocumentFetcher
    {
        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => Task.FromResult(factory(actorIri));
    }

    /// <summary>
    /// A stub <see cref="IActivityPubClient"/> for the feed service's outbox-fetch path:
    /// <see cref="IActivityPubClient.GetObjectAsync"/> returns the collection document mapped to the
    /// requested IRI (null when unmapped) and <see cref="IActivityPubClient.SendAsync"/> returns the page
    /// document mapped to the requested IRI (a 404 when unmapped). The other client methods are inert.
    /// </summary>
    private sealed class StubClient(IReadOnlyDictionary<Iri, IObject> documents) : IActivityPubClient
    {
        public StubClient(IReadOnlyList<(Iri Iri, IObject Doc)> docs)
            : this(docs.ToDictionary(d => d.Iri, d => d.Doc))
        {
        }

        public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default)
            => Task.FromResult(documents.TryGetValue(objectId, out var doc) ? doc : null);

        public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
            => Task.FromResult<Actor?>(null);

        public Task<int> DeliverAsync(Iri inboxId, IObject activity, CancellationToken ct = default)
            => Task.FromResult(202);

        public Task<int> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(202);

        public Task<int> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
            => Task.FromResult(202);

        public Task<int> PostReplyAsync(
            Iri actorId,
            Iri parentIri,
            string content,
            IEnumerable<Iri>? mentions = null,
            IEnumerable<Iri>? to = null,
            CancellationToken ct = default)
            => Task.FromResult(202);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            var iri = request.RequestUri is { } uri ? new Iri(uri) : default;
            return Task.FromResult(documents.TryGetValue(iri, out var doc)
                ? PageResponse(ActivityJson.Serialize(doc))
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) });
        }

        public async IAsyncEnumerable<CollectionPage> GetCollectionAsync(
            Iri collectionId,
            CollectionQuery? query = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Walk the mapped collection documents exactly as the shared client does: fetch the
            // collection, follow `first` (a page is used directly), then follow `next` across pages.
            if (!documents.TryGetValue(collectionId, out var collectionDoc))
            {
                yield break;
            }

            var pageIri = collectionDoc switch
            {
                OrderedCollectionPage => collectionId,
                Collection { First: { } first } => first.ResolveCollectionIri(),
                _ => null,
            };

            while (pageIri is { } current)
            {
                if (!documents.TryGetValue(current, out var pageDoc))
                {
                    yield break;
                }

                var page = CollectionPageFactory.FromOrderedCollectionPage(pageDoc as IObject);
                if (page is null)
                {
                    yield break;
                }

                yield return page;
                pageIri = page.NextPage;
                ct.ThrowIfCancellationRequested();
            }
        }

        public IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(
            Iri collectionId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(
            Iri communityId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetRepliesAsync(
            Iri objectIri,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> SearchAsync(
            Iri instanceBase,
            string? query = null,
            SearchOptions? options = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public Task<int> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(0);

        public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public Task<int> UnblockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> UnflagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(0);

        public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public Task<int> MuteAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> MuteAsync(Iri actorId, Iri targetId, Iris.Client.ProxyCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> UnmuteAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> UnmuteAsync(Iri actorId, Iri targetId, Iris.Client.ProxyCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(0);

        public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public void Dispose()
        {
        }

        private static HttpResponseMessage PageResponse(string json)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/activity+json");
            return response;
        }

        private static async IAsyncEnumerable<T> EmptyAsync<T>(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
