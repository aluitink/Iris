using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Options;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Server.Tests.Services;

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
        // severed). The cache must be cleared to pick up the new moderation state.
        await persistence.Moderation.RemoveMuteAsync(alice, bob);
        service.ClearFeedCache();
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

    // --- Audience / visibility (139.2-s5, follow-feed surface) ------------------------
    //
    // Without a visibility filter, an anonymous request to GET /u/{handle}/feed would surface the
    // feed owner's direct messages (in their own outbox) and their follows' non-public posts (in the
    // follows' outboxes). The filter drops non-public items not addressed to the requester and not
    // authored by them; the owner additionally sees their own non-public posts (the author clause).

    [Fact]
    public async Task Feed_Audience_AnonymousSeesOnlyPublic()
    {
        var (service, p) = Build(SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, alice, "Alice");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();

            // alice's own outbox: a public post, a DM to bob, a followers-only post (to bob + carol).
            AddPost(persistence, alice, "a-pub", "alice public");
            AddPostTo(persistence, alice, "a-dm", "alice dm", [Actor(LocalHost, "bob")]);
            AddPostTo(persistence, alice, "a-fol", "alice followers", [Actor(LocalHost, "bob"), Actor(LocalHost, "carol")]);

            // bob's outbox (alice follows bob): a public post and a DM to carol (not alice).
            AddPost(persistence, bob, "b-pub", "bob public");
            AddPostTo(persistence, bob, "b-dm", "bob dm", [Actor(LocalHost, "carol")]);
        }));

        // Anonymous: only the public posts (alice's + bob's) are visible. threadDepth 1 includes the
        // non-public (reply-classified) items so the visibility filter is what gates them.
        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"), threadDepth: 1, requesterIri: null);
        var ids = new HashSet<string?>(feed.Select(IdOf));
        Assert.Equal(2, feed.Count);
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/a-pub");
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/b-pub");
        Assert.DoesNotContain(ids, id => id == $"https://{LocalHost}/notes/a-dm");
        Assert.DoesNotContain(ids, id => id == $"https://{LocalHost}/notes/a-fol");
        Assert.DoesNotContain(ids, id => id == $"https://{LocalHost}/notes/b-dm");
    }

    [Fact]
    public async Task Feed_Audience_OwnerSeesOwnNonPublicAndFollowsPublic()
    {
        var (service, _) = Build(SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, alice, "Alice");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();

            AddPost(persistence, alice, "a-pub", "alice public");
            AddPostTo(persistence, alice, "a-dm", "alice dm", [Actor(LocalHost, "bob")]);
            AddPostTo(persistence, alice, "a-fol", "alice followers", [Actor(LocalHost, "bob"), Actor(LocalHost, "carol")]);

            AddPost(persistence, bob, "b-pub", "bob public");
            AddPostTo(persistence, bob, "b-dm", "bob dm", [Actor(LocalHost, "carol")]);
        }));

        var alice = Actor(LocalHost, "alice");
        var feed = await service.GetFeedAsync(alice, threadDepth: 1, requesterIri: alice);
        var ids = new HashSet<string?>(feed.Select(IdOf));

        // The owner sees their own posts (public, DM, followers-only — the author clause) and the
        // follow's public post, but NOT the follow's DM (bob's DM is addressed to carol, not alice).
        Assert.Equal(4, feed.Count);
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/a-pub");
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/a-dm");
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/a-fol");
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/b-pub");
        Assert.DoesNotContain(ids, id => id == $"https://{LocalHost}/notes/b-dm");
    }

    [Fact]
    public async Task Feed_Audience_RecipientSeesAddressedItems()
    {
        var (service, _) = Build(SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, alice, "Alice");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();

            AddPost(persistence, alice, "a-pub", "alice public");
            AddPostTo(persistence, alice, "a-dm", "alice dm", [Actor(LocalHost, "bob")]);
            AddPostTo(persistence, alice, "a-fol", "alice followers", [Actor(LocalHost, "bob"), Actor(LocalHost, "carol")]);

            AddPost(persistence, bob, "b-pub", "bob public");
            AddPostTo(persistence, bob, "b-dm", "bob dm", [Actor(LocalHost, "carol")]);
        }));

        // bob is a named recipient of alice's DM and followers-only post: he sees those in addition to
        // the public posts and his own DM (he is its author).
        var bob = Actor(LocalHost, "bob");
        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"), threadDepth: 1, requesterIri: bob);
        var ids = new HashSet<string?>(feed.Select(IdOf));
        Assert.Equal(5, feed.Count);
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/a-dm");
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/a-fol");
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/b-dm");
    }

    [Fact]
    public async Task Feed_Audience_NonRecipientSeesOnlyPublic()
    {
        var (service, _) = Build(SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, alice, "Alice");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();

            AddPost(persistence, alice, "a-pub", "alice public");
            AddPostTo(persistence, alice, "a-dm", "alice dm", [Actor(LocalHost, "bob")]);
            AddPostTo(persistence, alice, "a-fol", "alice followers", [Actor(LocalHost, "bob"), Actor(LocalHost, "carol")]);

            AddPost(persistence, bob, "b-pub", "bob public");
            AddPostTo(persistence, bob, "b-dm", "bob dm", [Actor(LocalHost, "carol")]);
        }));

        // carol is a recipient of alice's followers-only post and bob's DM, but not of alice's DM.
        var carol = Actor(LocalHost, "carol");
        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"), threadDepth: 1, requesterIri: carol);
        var ids = new HashSet<string?>(feed.Select(IdOf));
        Assert.Equal(4, feed.Count);
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/a-fol");
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/b-dm");
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/a-pub");
        Assert.Contains(ids, id => id == $"https://{LocalHost}/notes/b-pub");
        Assert.DoesNotContain(ids, id => id == $"https://{LocalHost}/notes/a-dm");
    }

    /// <summary>
    /// Seeds a <c>Create</c> of a note addressed to <paramref name="to"/> (a non-public audience) into
    /// the actor's outbox — the non-public shape the audience/visibility filter must handle.
    /// </summary>
    private static void AddPostTo(
        InMemoryPersistenceProvider persistence, Iri actorIri, string suffix, string content, IReadOnlyList<Iri> to)
    {
        var noteIri = $"https://{LocalHost}/notes/{suffix}";
        persistence.Activities.AddToOutboxAsync(actorIri, new Create
        {
            Id = noteIri,
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Note
            {
                Id = noteIri,
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
                To = to.Select(a => (IObjectOrLink)new Link { Href = new Uri(a.Value) }).ToList(),
            }],
        }).GetAwaiter().GetResult();
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

    // --- Phase 146: per-peer remote-follow circuit breaker (bounds re-probing a dead remote) -----

    [Fact]
    public async Task Feed_RemoteFollow_CircuitBreakerDeadPeer_StopsReprobingAfterThreshold()
    {
        var alice = Actor(LocalHost, "alice");
        var remote = Actor(RemoteHost, "bob");
        var deadRemote = new PerPeerFeedCircuitBreaker(failureThreshold: 2, openDuration: TimeSpan.FromMinutes(5));
        // The dead remote's outbox walk always yields nothing (no healthy outbox IRI is registered).
        var countingClient = new CountingClient(healthyOutboxIri: null, healthyNoteIri: null);
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(alice, remote).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: countingClient,
            circuitBreaker: deadRemote);

        // Rebuild the feed three times (the feed cache is bypassed each time) and count how many times
        // the dead remote's outbox is actually fetched over the wire.
        await service.GetFeedAsync(alice, bypassCache: true);
        await service.GetFeedAsync(alice, bypassCache: true);
        await service.GetFeedAsync(alice, bypassCache: true);

        // Build 1: 1 fetch (fails, count=1). Build 2: 1 fetch (fails, count=2 → circuit opens).
        // Build 3: circuit open → the fetch is SKIPPED (no third probe of the dead peer).
        Assert.Equal(2, countingClient.TotalFetchCount);
    }

    [Fact]
    public async Task Feed_RemoteFollow_CircuitBreakerDeadPeer_RecoveredInHalfOpen()
    {
        var alice = Actor(LocalHost, "alice");
        var remote = Actor(RemoteHost, "bob");
        // OpenDuration of 0: the circuit transitions to half-open immediately after opening, so a
        // subsequent rebuild fires a single probe.
        var deadRemote = new PerPeerFeedCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.Zero);
        // The outbox IRI the feed fetches is the actor IRI + "/outbox" (the advertised outbox link).
        var countingClient = new CountingClient(
            healthyOutboxIri: $"https://{RemoteHost}/ap/v1/u/bob/outbox",
            healthyNoteIri: $"https://{RemoteHost}/notes/r-1");
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(alice, remote).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: countingClient,
            circuitBreaker: deadRemote);

        // Build 1: the fetch returns a page (1 item) → the peer is healthy, circuit stays closed, the
        // remote's content surfaces in the feed. (The feed item is the Create activity, so its IRI is
        // the activity IRI, not the note IRI.)
        var feed = await service.GetFeedAsync(alice, bypassCache: true);
        Assert.Single(feed);
        Assert.Equal($"https://{RemoteHost}/notes/r-1/activity", IdOf(feed[0]));
        Assert.Equal(1, countingClient.FetchCountFor($"https://{RemoteHost}/ap/v1/u/bob/outbox"));

        // Build 2: still healthy, fetched again (circuit closed), content still surfaces.
        feed = await service.GetFeedAsync(alice, bypassCache: true);
        Assert.Single(feed);
        Assert.Equal(2, countingClient.FetchCountFor($"https://{RemoteHost}/ap/v1/u/bob/outbox"));
    }

    [Fact]
    public async Task Feed_RemoteFollow_CircuitBreakerDisabledByDefault_FetchesEveryRebuild()
    {
        var alice = Actor(LocalHost, "alice");
        var remote = Actor(RemoteHost, "bob");
        // The default (no breaker passed) is a no-op: a dead remote is re-probed on every rebuild (the
        // pre-146 behavior), so the fetch count grows with every rebuild.
        var countingClient = new CountingClient(healthyOutboxIri: null, healthyNoteIri: null);
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(alice, remote).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: countingClient,
            circuitBreaker: null);

        await service.GetFeedAsync(alice, bypassCache: true);
        await service.GetFeedAsync(alice, bypassCache: true);
        await service.GetFeedAsync(alice, bypassCache: true);

        // No breaker: every rebuild fetches the dead remote's outbox.
        Assert.Equal(3, countingClient.TotalFetchCount);
    }

    [Fact]
    public async Task Feed_RemoteFollow_CircuitBreaker_IsPerHost_OtherPeersUnaffected()
    {
        var alice = Actor(LocalHost, "alice");
        var deadRemote = Actor(RemoteHost, "bob"); // host b.test
        var healthyRemote = Actor("c.test", "carol"); // host c.test (a different peer)
        // Only the healthy remote's outbox (c.test/outbox) yields content (a public Create for h-1); the
        // dead remote's outbox (b.test/outbox) yields nothing. The dead host (b.test) opens its circuit
        // after the first failing (empty) fetch; the healthy host (c.test) stays closed and is fetched on
        // every rebuild. The two hosts have independent circuit state, so the healthy host is unaffected
        // by the dead host's open circuit.
        // The outbox IRIs the feed fetches are the actor IRIs + "/outbox".
        var healthyClient = new CountingClient(
            healthyOutboxIri: "https://c.test/ap/v1/u/carol/outbox",
            healthyNoteIri: "https://c.test/notes/h-1");
        var breaker = new PerPeerFeedCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMinutes(5));
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(alice, deadRemote).GetAwaiter().GetResult();
                persistence.Follows.RecordFollowAsync(alice, healthyRemote).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(iri =>
            {
                var actor = new Person { Id = iri.Value };
                actor.Outbox = new Link { Href = new Uri($"{iri.Value}/outbox") };
                return actor;
            }),
            client: healthyClient,
            circuitBreaker: breaker);

        // Build 1: the dead remote (b.test) fetches → yields nothing → its circuit opens (threshold 1).
        // The healthy remote (c.test) fetches → yields the h-1 Create (activity IRI .../activity).
        var feed = await service.GetFeedAsync(alice, bypassCache: true);
        Assert.Single(feed);
        Assert.Equal("https://c.test/notes/h-1/activity", IdOf(feed[0]));
        Assert.Equal(1, healthyClient.FetchCountFor("https://b.test/ap/v1/u/bob/outbox")); // dead fetched once
        Assert.Equal(1, healthyClient.FetchCountFor("https://c.test/ap/v1/u/carol/outbox")); // healthy fetched once

        // Build 2: the dead remote's circuit is open → skipped (no second probe of b.test); the healthy
        // remote is fetched again (c.test is a different host, so its closed circuit is unaffected).
        feed = await service.GetFeedAsync(alice, bypassCache: true);
        Assert.Single(feed);
        Assert.Equal("https://c.test/notes/h-1/activity", IdOf(feed[0]));
        Assert.Equal(1, healthyClient.FetchCountFor("https://b.test/ap/v1/u/bob/outbox")); // still 1 (skipped)
        Assert.Equal(2, healthyClient.FetchCountFor("https://c.test/ap/v1/u/carol/outbox")); // healthy fetched again
    }

    [Fact]
    public async Task Feed_RemoteFollow_CircuitBreakerDeadPeer_FirstRebuildStillServesDeliveredContent()
    {
        var alice = Actor(LocalHost, "alice");
        var remote = Actor(RemoteHost, "bob");
        var deadRemote = new PerPeerFeedCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMinutes(5));
        var countingClient = new CountingClient(healthyOutboxIri: null, healthyNoteIri: null);
        // Seed a delivered remote object attributed to the dead remote (the S25 half: content received
        // via the inbox, stored in the object store). On the FIRST rebuild the circuit is still closed,
        // so the remote follow is permitted: the live outbox walk yields nothing (the dead peer), but the
        // local delivered-content read runs and surfaces the already-received note. That same rebuild
        // records the failure and opens the circuit for subsequent rebuilds.
        var (service, _) = Build(
            persistence: SeedLocal(p =>
            {
                SeedActor(p, alice, "Alice"); // alice is local (the feed owner)
                p.Follows.RecordFollowAsync(alice, remote).GetAwaiter().GetResult();
                // Seed a delivered remote object attributed to the dead remote (the S25 half). The
                // in-memory store keys ListByActorAsync on the object's attributedTo[0], so the note
                // must name the remote as its author to be listed for the dead follow. It is public
                // (to: Public) so the feed's visibility filter surfaces it for alice.
                var deliveredIri = "https://" + RemoteHost + "/notes/delivered-1";
                p.Objects.PutObjectAsync(new Note
                {
                    Id = deliveredIri,
                    Content = ["delivered"],
                    AttributedTo = [new Link { Href = new Uri(remote.Value) }],
                    To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
                }).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: countingClient,
            circuitBreaker: deadRemote);

        // Build 1: the circuit is closed, so the remote follow is permitted. The live outbox walk yields
        // nothing (the dead peer), but the local delivered-content read runs and surfaces the note as a
        // synthetic Create (activity IRI = the note IRI). This rebuild opens the circuit.
        var feed = await service.GetFeedAsync(alice, bypassCache: true);
        Assert.Single(feed);
        Assert.Equal($"https://{RemoteHost}/notes/delivered-1", IdOf(feed[0]));
        Assert.Equal(1, countingClient.TotalFetchCount);

        // Build 2: the circuit is now open → the remote follow (both the live walk AND the delivered
        // read) is skipped entirely. The dead peer is not re-probed, and the feed no longer surfaces the
        // delivered note (it is held in the object store and re-surfaces once the circuit half-opens and
        // a probe succeeds again). This is the intended trade-off of bounding re-probes: while a peer is
        // open, its follow contributes nothing rather than being re-fetched on every 30-s rebuild.
        feed = await service.GetFeedAsync(alice, bypassCache: true);
        Assert.Empty(feed);
        Assert.Equal(1, countingClient.TotalFetchCount); // no new probe of the dead peer
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

    [Fact]
    public async Task Feed_RemoteFollow_DocumentFetchThrows_ContributesNothing_Not500()
    {
        // Regression: the IActorDocumentFetcher contract is "return null, do not throw" on fetch
        // failure, but the implementation can still throw (a transport error, timeout, or a signing-key
        // failure in the outbound actor-doc fetch). Before the fix, this throw propagated uncaught
        // through FeedService.FetchRemoteOutboxAsync → the feed handler → a 500 on the whole feed.
        // After the fix, the actor-doc fetch is guarded: a throwing remote contributes nothing and the
        // feed still completes (falling back to the conventional outbox IRI).
        var remote = Actor(RemoteHost, "bob");
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(Actor(LocalHost, "alice"), remote).GetAwaiter().GetResult();
            }),
            actorDocs: new ThrowingActorDocumentFetcher(),
            client: new StubClient(Pages(
                Page($"{remote.Value}/outbox", [Item("r-1")], next: null))));

        // Must not throw (the actor-doc fetch exception is caught; the feed falls back to the
        // conventional outbox IRI and fetches r-1 from the stub client).
        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));
        Assert.Single(feed);
        Assert.Equal($"https://{RemoteHost}/notes/r-1", IdOf(feed[0]));
    }

    [Fact]
    public async Task Feed_RemoteFollow_DocumentFetchThrows_NoOutbox_ContributesNothing()
    {
        // Same regression, but the conventional outbox IRI also has no document: the remote contributes
        // nothing (empty feed) and the feed still completes without throwing.
        var remote = Actor(RemoteHost, "bob");
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                persistence.Follows.RecordFollowAsync(Actor(LocalHost, "alice"), remote).GetAwaiter().GetResult();
            }),
            actorDocs: new ThrowingActorDocumentFetcher(),
            client: new StubClient(Pages())); // no documents at all

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));
        Assert.Empty(feed);
    }

    // --- S25: a delivered remote post surfaces in the follower's home feed ------------
    // When a remote Create is delivered to a local recipient, the CreateActivityHandler stores the
    // embedded object in the object store (keyed by its attributedTo). The home feed for a remote
    // follow walks the remote's outbox over the wire; if that walk yields nothing (an
    // unreachable/broken remote outbox, or a fresh delivery not yet reflected in the walked page),
    // the feed must still surface the delivered content from the object store (the same store/path
    // the inbox write lands in) rather than leaving the follower's timeline empty.

    [Fact]
    public async Task Feed_RemoteFollow_DeliveredContentInObjectStore_SurfacesInFeed()
    {
        var remote = Actor(RemoteHost, "bob");
        var noteIri = $"https://{RemoteHost}/notes/delivered-s25";
        // The remote follow's outbox walk yields nothing (no collection document mapped for the
        // outbox IRI), but a post from that remote author was delivered to a local recipient and is
        // stored in the object store (the production inbox path).
        var (service, persistence) = Build(
            persistence: SeedLocal(p =>
            {
                p.Follows.RecordFollowAsync(Actor(LocalHost, "alice"), remote).GetAwaiter().GetResult();
                // Store the delivered remote Note in the object store, attributed to the remote author
                // (mirrors CreateActivityHandler.StoreEmbeddedObjectAsync).
                p.Objects.PutObjectAsync(new Note
                {
                    Id = noteIri,
                    Content = ["delivered s25 payload"],
                    AttributedTo = [new Link { Href = new Uri(remote.Value) }],
                    To = [new Link { Href = new Uri(Iri.Public.Value) }],
                }).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(Pages())); // outbox walk yields nothing

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // The delivered remote post surfaces in alice's home feed even though the outbox walk was empty.
        Assert.Contains(feed, item => IdOf(item) == noteIri);
    }

    [Fact]
    public async Task Feed_RemoteFollow_DeliveredAndWireContent_Deduplicated()
    {
        var remote = Actor(RemoteHost, "bob");
        var noteIri = $"https://{RemoteHost}/notes/dedup-s25";
        // The same note is both (a) in the remote's outbox (walked over the wire, embedded) and
        // (b) stored in the object store as delivered content. The feed must render it once.
        var (service, _) = Build(
            persistence: SeedLocal(p =>
            {
                p.Follows.RecordFollowAsync(Actor(LocalHost, "alice"), remote).GetAwaiter().GetResult();
                p.Objects.PutObjectAsync(new Note
                {
                    Id = noteIri,
                    Content = ["dedup s25 payload"],
                    AttributedTo = [new Link { Href = new Uri(remote.Value) }],
                    To = [new Link { Href = new Uri(Iri.Public.Value) }],
                }).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(Pages(
                Page($"{remote.Value}/outbox",
                    [CreateItem(noteIri, embedded: true)],
                    next: null))));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Exactly one copy of the note: the wire-walked Create and the delivered-content Create both
        // reference the same object IRI, so the content-object coalesce pass renders it once.
        Assert.Single(feed);
        var content = (feed[0] as Create)?.Object?.FirstOrDefault() as Note;
        Assert.NotNull(content);
        Assert.Equal(noteIri, content!.Id);
    }

    [Fact]
    public async Task Feed_RemoteFollow_DeliveredTombstone_NotInFeed()
    {
        var remote = Actor(RemoteHost, "bob");
        var noteIri = $"https://{RemoteHost}/notes/tombstone-s25";
        // A deleted (tombstoned) object in the object store must not surface in the feed.
        var (service, _) = Build(
            persistence: SeedLocal(p =>
            {
                p.Follows.RecordFollowAsync(Actor(LocalHost, "alice"), remote).GetAwaiter().GetResult();
                p.Objects.PutObjectAsync(new Tombstone
                {
                    Id = noteIri,
                    Deleted = DateTime.UtcNow,
                }).GetAwaiter().GetResult();
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(Pages()));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));
        Assert.Empty(feed);
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

    // --- Content-object coalescing (a note surfaced as both a Create and an Announce) ----
    //
    // The same object can appear in the feed under two activity types: the author's own Create of a
    // note, and a follower's Announce (boost) of the same note. They are two distinct activities with
    // two distinct IRIs, so the by-IRI de-dup does not remove them — left un-coalesced the home
    // timeline renders the note twice. The by-content-object pass keeps a single representative per
    // object, preferring the item that carries the object EMBEDDED (the author's Create, which renders
    // the content + the server-rendered engagement counters in place) over a link-only reference
    // (a booster's bare Announce).

    [Fact]
    public async Task Feed_CreateAndAnnounceOfSameObject_AppearsOnce_PrefersEmbeddedCreate()
    {
        // alice follows a remote bob. bob's outbox (walked over the wire) carries the Create of a note
        // (the note EMBEDDED). alice's OWN outbox carries an Announce (boost) of that same note (a
        // bare LINK). The note must render once — as the embedded Create, not the link-only Announce.
        var remoteNote = "https://b.test/notes/r-1";
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                var alice = Actor(LocalHost, "alice");
                var bob = Actor(RemoteHost, "bob");
                persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
                // alice's own outbox: a boost (Announce) of the remote note (link-only object).
                AddAnnounce(persistence, alice, "https://a.test/announces/a-1", remoteNote, embedded: false);
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(Pages(
                // bob's outbox carries the Create of the SAME note, with the note embedded.
                Page($"{Actor(RemoteHost, "bob").Value}/outbox",
                    [CreateItem(remoteNote, embedded: true)], next: null))));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // The note appears exactly once.
        Assert.Single(feed);
        // It is the embedded Create (bob's), not the link-only Announce (alice's boost).
        var survivor = feed[0];
        Assert.IsType<Create>(survivor);
        // The survivor carries the embedded note (rich, renderable in place).
        Assert.IsType<Note>(((Create)survivor).Object!.First());
    }

    [Fact]
    public async Task Feed_TwoAnnounceOfSameObject_AppearsOnce_FirstWins()
    {
        // Two follows each boost the same remote note (two Announce, both link-only). The note renders
        // once (the first occurrence wins; neither embeds the object).
        var remoteNote = "https://b.test/notes/r-1";
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var dave = Actor(LocalHost, "dave");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, dave, "Dave");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            persistence.Follows.RecordFollowAsync(alice, dave).GetAwaiter().GetResult();
            // Both bob and dave boost the same remote note (link-only Announce in each outbox).
            AddAnnounce(persistence, bob, "https://a.test/announces/b-1", remoteNote, embedded: false);
            AddAnnounce(persistence, dave, "https://a.test/announces/d-1", remoteNote, embedded: false);
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        Assert.Single(feed);
        Assert.IsType<Announce>(feed[0]);
    }

    [Fact]
    public async Task Feed_LinkOnlyAnnounceThenEmbeddedCreate_EmbeddedWins_RegardlessOfOrder()
    {
        // Even when the link-only Announce is merged before the embedded Create (own outbox first, the
        // author's Create from a follow's outbox second), the embedded Create is promoted to the
        // representative and the link-only Announce is dropped.
        var remoteNote = "https://b.test/notes/r-1";
        var (service, _) = Build(
            persistence: SeedLocal(persistence =>
            {
                var alice = Actor(LocalHost, "alice");
                var bob = Actor(RemoteHost, "bob");
                persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
                // alice boosts the note first (own outbox, merged before the follow's outbox).
                AddAnnounce(persistence, alice, "https://a.test/announces/a-1", remoteNote, embedded: false);
            }),
            actorDocs: new StubActorDocumentFetcher(remote =>
            {
                var actor = new Person { Id = remote.Value };
                actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                return actor;
            }),
            client: new StubClient(Pages(
                Page($"{Actor(RemoteHost, "bob").Value}/outbox",
                    [CreateItem(remoteNote, embedded: true)], next: null))));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // One card, and it is the embedded Create.
        Assert.Single(feed);
        Assert.IsType<Create>(feed[0]);
        Assert.IsType<Note>(((Create)feed[0]).Object!.First());
    }

    [Fact]
    public async Task Feed_LikeAndCreateOfSameObject_BothKept()
    {
        // A Like of a note and the note's own Create are NOT coalesced: the Like is a social activity
        // (not a Create/Announce), so the by-content-object pass leaves it alone. Both appear.
        var noteIri = "https://a.test/notes/n-1";
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            // bob posts the note and likes it (both in bob's outbox).
            AddPost(persistence, bob, "n-1", "note 1");
            AddLike(persistence, bob, "https://a.test/likes/l-1", noteIri);
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Both the Create (note) and the Like survive — the Like is never coalesced by object.
        Assert.Equal(2, feed.Count);
        Assert.Contains(feed, i => i is Create);
        Assert.Contains(feed, i => i is Like);
    }

    [Fact]
    public async Task Feed_CreateOfTwoObjectsPlusAnnounceOfOne_NoOverDedup()
    {
        // bob posts two distinct notes; alice boosts one of them. The boosted note renders once (the
        // embedded Create, not the link-only Announce) and the un-boosted note renders once — the
        // coalescing must not drop the un-boosted note or over-coalesce across different objects.
        var noteA = "https://a.test/notes/a-1";
        var noteB = "https://a.test/notes/a-2";
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPostWithId(persistence, bob, noteA, "note A");
            AddPostWithId(persistence, bob, noteB, "note B");
            // alice boosts note A (link-only Announce in her own outbox).
            AddAnnounce(persistence, alice, "https://a.test/announces/a-1", noteA, embedded: false);
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Two notes total: note A once (coalesced with the boost), note B once.
        Assert.Equal(2, feed.Count);
        // note A survives as its embedded Create (the boost is dropped), note B survives as its Create.
        Assert.Equal(0, feed.Count(i => i is Announce));
        Assert.Equal(2, feed.Count(i => i is Create));
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

    [Fact]
    public async Task Feed_FollowReply_IsFilteredOut()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var carol = Actor(LocalHost, "carol");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, carol, "Carol");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            // bob posts a top-level post (public only).
            AddPost(persistence, bob, "b-1", "bob top-level post");
            // bob replies to carol (has a non-public audience).
            AddReply(persistence, bob, "b-2", "bob reply to carol", carol);
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Only bob's top-level post should appear; the reply is filtered out.
        Assert.Single(feed);
        Assert.Equal($"https://{LocalHost}/notes/b-1", IdOf(feed[0]));
    }

    [Fact]
    public async Task Feed_OwnReply_FilteredByDefault()
    {
        // 117.5: the actor's own replies to other actors are filtered from the home feed by default.
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            // alice posts a top-level post.
            AddPost(persistence, alice, "a-1", "alice top-level");
            // alice replies to bob (has a non-public audience).
            AddReply(persistence, alice, "a-2", "alice reply to bob", bob);
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Only the top-level post appears; the reply is filtered out.
        Assert.Single(feed);
        Assert.Equal($"https://{LocalHost}/notes/a-1", IdOf(feed[0]));
    }

    [Fact]
    public async Task Feed_OwnReply_IncludedWithThreadDepth()
    {
        // 117.5: with threadDepth > 0, the actor's own replies are included.
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, alice, "a-1", "alice top-level");
            AddReply(persistence, alice, "a-2", "alice reply to bob", bob);
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"), threadDepth: 1);

        // Both the post and the reply appear when depth is requested.
        Assert.Equal(2, feed.Count);
    }

    [Fact]
    public async Task Feed_FollowAnnounce_IsKept()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var carol = Actor(LocalHost, "carol");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, carol, "Carol");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            // carol posts a top-level post.
            AddPost(persistence, carol, "c-1", "carol post");
            // bob boosts carol's post (Announce).
            AddAnnounce(persistence, bob, "https://a.test/announce/b-1", "https://a.test/notes/c-1", embedded: true);
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // The boost should appear (Announce is not filtered).
        Assert.Single(feed);
        Assert.Equal("https://a.test/announce/b-1", IdOf(feed[0]));
    }

    [Fact]
    public async Task Feed_FollowReply_WithInReplyTo_IsFilteredOut()
    {
        // 117.1: reply detection via inReplyTo (primary signal).
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var carol = Actor(LocalHost, "carol");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, carol, "Carol");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob top-level post");
            // bob replies to carol's post (inReplyTo is set).
            AddReply(persistence, bob, "b-2", "bob reply to carol", Actor(LocalHost, "carol"));
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        Assert.Single(feed);
        Assert.Equal($"https://{LocalHost}/notes/b-1", IdOf(feed[0]));
    }

    [Fact]
    public async Task Feed_FollowReply_WithOnlyAudience_IsFilteredOut()
    {
        // 117.1: fallback to audience heuristic when inReplyTo is absent.
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var carol = Actor(LocalHost, "carol");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, carol, "Carol");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob top-level post");
            // bob's reply has NO inReplyTo but has a non-public audience (carol in `to`).
            var noteIri = $"https://{LocalHost}/notes/b-2";
            persistence.Activities.AddToOutboxAsync(bob, new Create
            {
                Id = noteIri,
                Actor = [new Link { Href = new Uri(bob.Value) }],
                Object = [new Note
                {
                    Id = noteIri,
                    Content = ["bob reply (audience only)"],
                    To = [new Link { Href = new Uri(carol.Value) }],
                }],
            }).GetAwaiter().GetResult();
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // The audience-only reply is still filtered (fallback heuristic).
        Assert.Single(feed);
        Assert.Equal($"https://{LocalHost}/notes/b-1", IdOf(feed[0]));
    }

    [Fact]
    public async Task Feed_FollowersOnlyTopLevelPost_IsNotTreatedAsReply()
    {
        // S46: a top-level post with followers-only visibility (to = the author's …/followers
        // collection) must not be misclassified as a directed reply by the audience fallback
        // heuristic — the collection names the owner's followers, never a single addressed actor.
        // Without this, the follow-reply filter drops every followers-only post from the home
        // timeline before the visibility filter can grant it to the follower.
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob public post");
            // bob's followers-only top-level post: to = bob's followers collection (NOT a literal
            // recipient and NOT as:Public), no inReplyTo.
            var noteIri = $"https://{LocalHost}/notes/b-fol";
            persistence.Activities.AddToOutboxAsync(bob, new Create
            {
                Id = noteIri,
                Actor = [new Link { Href = new Uri(bob.Value) }],
                Object = [new Note
                {
                    Id = noteIri,
                    Content = ["bob followers-only post"],
                    AttributedTo = [new Link { Href = new Uri(bob.Value) }],
                    To = [new Link { Href = new Uri($"{bob.Value}/followers") }],
                }],
            }).GetAwaiter().GetResult();
        }));

        // Default home timeline (threadDepth null): the followers-only post is top-level content, not
        // a reply, so it is retained by the thread filter. As a signed owner (alice) it is also visible
        // (alice follows bob). Both posts surface.
        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"), requesterIri: Actor(LocalHost, "alice"));
        var ids = feed.Select(IdOf).ToList();

        Assert.Contains($"https://{LocalHost}/notes/b-1", ids);
        Assert.Contains($"https://{LocalHost}/notes/b-fol", ids);
    }

    [Fact]
    public async Task Feed_FollowReply_ThreadDepth1_IncludesReply()
    {
        // 117.1: ?depth=1 includes first-level replies from followed actors.
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var carol = Actor(LocalHost, "carol");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, carol, "Carol");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob top-level post");
            AddReply(persistence, bob, "b-2", "bob reply to carol", Actor(LocalHost, "carol"));
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"), threadDepth: 1);

        // Both the post and the reply appear (depth 1 includes first-level replies).
        Assert.Equal(2, feed.Count);
    }

    [Fact]
    public async Task Feed_OwnAndFollowReplies_FilteredByDefault()
    {
        // 117.5: both the actor's own replies and followed actors' replies are filtered by default.
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, alice, "a-1", "alice top-level");
            AddReply(persistence, alice, "a-2", "alice reply to bob", Actor(LocalHost, "bob"));
            AddPost(persistence, bob, "b-1", "bob post");
            AddReply(persistence, bob, "b-2", "bob reply to alice", Actor(LocalHost, "alice"));
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // Only top-level posts remain: alice's a-1 and bob's b-1. Both replies are filtered.
        Assert.Equal(2, feed.Count);
        var ids = feed.Select(IdOf).ToHashSet();
        Assert.Contains($"https://{LocalHost}/notes/a-1", ids);
        Assert.Contains($"https://{LocalHost}/notes/b-1", ids);
        Assert.DoesNotContain($"https://{LocalHost}/notes/a-2", ids);
        Assert.DoesNotContain($"https://{LocalHost}/notes/b-2", ids);
    }

    [Fact]
    public async Task Feed_OwnAndFollowReplies_IncludedWithThreadDepth()
    {
        // 117.5: with threadDepth > 0, both own and follow replies are included.
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, alice, "a-1", "alice top-level");
            AddReply(persistence, alice, "a-2", "alice reply to bob", Actor(LocalHost, "bob"));
            AddPost(persistence, bob, "b-1", "bob post");
            AddReply(persistence, bob, "b-2", "bob reply to alice", Actor(LocalHost, "alice"));
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"), threadDepth: 1);

        // All four items appear when depth is requested.
        Assert.Equal(4, feed.Count);
        var ids = feed.Select(IdOf).ToHashSet();
        Assert.Contains($"https://{LocalHost}/notes/a-2", ids);
        Assert.Contains($"https://{LocalHost}/notes/b-2", ids);
    }

    [Fact]
    public async Task Feed_FollowAnnounce_NotAffectedByReplyFilter()
    {
        // 117.1: Announce (boost) activities are never treated as replies.
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var carol = Actor(LocalHost, "carol");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, carol, "Carol");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, carol, "c-1", "carol post");
            AddAnnounce(persistence, bob, "https://a.test/announce/b-1", "https://a.test/notes/c-1", embedded: true);
        }));

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        Assert.Single(feed);
        Assert.Equal("https://a.test/announce/b-1", IdOf(feed[0]));
    }

    // --- Server-side per-actor feed cache (feed load feel) ----------------------------

    [Fact]
    public async Task Feed_Cache_SameActor_ReturnsCachedList()
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
        var first = await service.GetFeedAsync(alice);
        var second = await service.GetFeedAsync(alice);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(IdOf(first[0]), IdOf(second[0]));
        Assert.Equal(IdOf(first[1]), IdOf(second[1]));
    }

    [Fact]
    public async Task Feed_Cache_DifferentActors_AreIsolated()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var carol = Actor(LocalHost, "carol");
            var bob = Actor(LocalHost, "bob");
            var dave = Actor(LocalHost, "dave");
            SeedActor(persistence, bob, "Bob");
            SeedActor(persistence, dave, "Dave");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            persistence.Follows.RecordFollowAsync(carol, dave).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-1", "bob 1");
            AddPost(persistence, dave, "d-1", "dave 1");
        }));

        var aliceFeed = await service.GetFeedAsync(Actor(LocalHost, "alice"));
        var carolFeed = await service.GetFeedAsync(Actor(LocalHost, "carol"));

        Assert.Single(aliceFeed);
        Assert.Equal($"https://{LocalHost}/notes/b-1", IdOf(aliceFeed[0]));
        Assert.Single(carolFeed);
        Assert.Equal($"https://{LocalHost}/notes/d-1", IdOf(carolFeed[0]));
    }

    [Fact]
    public async Task Feed_Cache_ThreadDepth_AppliedPerRequest()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-top", "bob top-level");
            AddReply(persistence, bob, "b-reply", "bob reply", Actor(LocalHost, "carol"));
        }));

        var alice = Actor(LocalHost, "alice");

        // Default (threadDepth null): replies excluded.
        var topOnly = await service.GetFeedAsync(alice);
        Assert.Single(topOnly);
        Assert.Equal($"https://{LocalHost}/notes/b-top", IdOf(topOnly[0]));

        // With threadDepth=1: replies included (from the same cached list).
        var withReplies = await service.GetFeedAsync(alice, threadDepth: 1);
        Assert.Equal(2, withReplies.Count);
        var ids = new HashSet<string?>(withReplies.Select(IdOf));
        Assert.Contains($"https://{LocalHost}/notes/b-top", ids);
        Assert.Contains($"https://{LocalHost}/notes/b-reply", ids);

        // The default view still excludes the reply (the cached list is unchanged).
        var topOnlyAgain = await service.GetFeedAsync(alice);
        Assert.Single(topOnlyAgain);
        Assert.Equal($"https://{LocalHost}/notes/b-top", IdOf(topOnlyAgain[0]));
    }

    [Fact]
    public async Task Feed_Cache_ClearFeedCache_ForcesRebuild()
    {
        var (service, persistence) = Build(persistence: SeedLocal(p =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(p, bob, "Bob");
            p.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(p, bob, "b-1", "bob 1");
        }));

        var alice = Actor(LocalHost, "alice");
        var first = await service.GetFeedAsync(alice);
        Assert.Single(first);

        // Clear the cache and add a new post.
        service.ClearFeedCache();
        AddPost(persistence, Actor(LocalHost, "bob"), "b-2", "bob 2");

        var second = await service.GetFeedAsync(alice);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public async Task Feed_Cache_InvalidateActorFeedCache_DropsOnlyThatActor()
    {
        var (service, persistence) = Build(persistence: SeedLocal(p =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            var carol = Actor(LocalHost, "carol");
            SeedActor(p, bob, "Bob");
            SeedActor(p, carol, "Carol");
            p.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            p.Follows.RecordFollowAsync(carol, bob).GetAwaiter().GetResult();
            AddPost(p, bob, "b-1", "bob 1");
        }));

        var alice = Actor(LocalHost, "alice");
        var carol = Actor(LocalHost, "carol");

        // Warm both actors' feed caches (each follows bob, who has one post).
        var aliceBefore = await service.GetFeedAsync(alice);
        var carolBefore = await service.GetFeedAsync(carol);
        Assert.Single(aliceBefore);
        Assert.Single(carolBefore);

        // A new post by bob appears for NEITHER until their cache is invalidated.
        AddPost(persistence, Actor(LocalHost, "bob"), "b-2", "bob 2");
        var aliceStale = await service.GetFeedAsync(alice);
        var carolStale = await service.GetFeedAsync(carol);
        Assert.Single(aliceStale);
        Assert.Single(carolStale);

        // Invalidating ONLY alice's feed cache drops her entry (rebuilds with b-2) but leaves carol's
        // entry cached (still the single pre-b-2 item). This is the unfollow seam: the handler drops the
        // un-follower's cache entry so the next /feed read reflects the removed follow immediately.
        service.InvalidateActorFeedCache(alice);
        var aliceFresh = await service.GetFeedAsync(alice);
        var carolStillStale = await service.GetFeedAsync(carol);
        Assert.Equal(2, aliceFresh.Count);
        Assert.Single(carolStillStale);

        // A no-op invalidation of an actor with no cache entry does not throw.
        service.InvalidateActorFeedCache(Actor(LocalHost, "nobody"));
    }

    [Fact]
    public async Task Feed_Cache_QueryFilter_AppliedPerRequest()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();
            AddPost(persistence, bob, "b-hello", "hello world");
            AddPost(persistence, bob, "b-goodbye", "goodbye moon");
        }));

        var alice = Actor(LocalHost, "alice");
        var all = await service.GetFeedAsync(alice);
        Assert.Equal(2, all.Count);

        var filtered = await service.GetFeedAsync(alice, query: "hello");
        Assert.Single(filtered);
        Assert.Equal($"https://{LocalHost}/notes/b-hello", IdOf(filtered[0]));

        // The unfiltered view still returns both (the cache is not mutated by the query).
        var allAgain = await service.GetFeedAsync(alice);
        Assert.Equal(2, allAgain.Count);
    }

    // --- S36 repro: home feed omits the actor's own post when the outbox is polluted with
    // --- actor-document activity -------------------------------------------------------

    [Fact]
    public async Task Feed_OwnPostPlusActorDocNoise_KeepsOwnCreate()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            SeedActor(persistence, alice, "Alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();

            // The actor's own content post (a Create of a public Note).
            AddPost(persistence, alice, "a-own", "my own post");

            // The actor's own outbox also holds actor-document activity noise (profile edits, a self
            // Follow, a Like, an Undo, a Delete) — the S36 symptom: the feed is dominated by these and
            // the content Create is absent.
            var aliceIri = alice.Value;
            persistence.Activities.AddToOutboxAsync(alice, new Update
            {
                Id = $"{aliceIri}/update-1",
                Actor = [new Link { Href = new Uri(aliceIri) }],
                Object = [new Link { Href = new Uri(aliceIri) }],
            }).GetAwaiter().GetResult();
            persistence.Activities.AddToOutboxAsync(alice, new Update
            {
                Id = $"{aliceIri}/update-2",
                Actor = [new Link { Href = new Uri(aliceIri) }],
                Object = [new Link { Href = new Uri(aliceIri) }],
            }).GetAwaiter().GetResult();
            persistence.Activities.AddToOutboxAsync(alice, new Follow
            {
                Id = $"{aliceIri}/follow-self",
                Actor = [new Link { Href = new Uri(aliceIri) }],
                Object = [new Link { Href = new Uri(aliceIri) }],
            }).GetAwaiter().GetResult();
            AddLike(persistence, alice, $"{aliceIri}/like-1", $"https://{LocalHost}/notes/other");
            persistence.Activities.AddToOutboxAsync(alice, new Undo
            {
                Id = $"{aliceIri}/undo-1",
                Actor = [new Link { Href = new Uri(aliceIri) }],
                Object = [new Link { Href = new Uri($"{aliceIri}/follow-self") }],
            }).GetAwaiter().GetResult();
            persistence.Activities.AddToOutboxAsync(alice, new Delete
            {
                Id = $"{aliceIri}/delete-1",
                Actor = [new Link { Href = new Uri(aliceIri) }],
                Object = [new Link { Href = new Uri($"https://{LocalHost}/notes/gone") }],
            }).GetAwaiter().GetResult();

            // A followed actor's post must also surface.
            AddPost(persistence, bob, "b-1", "bob 1");
        }));

        var alice = Actor(LocalHost, "alice");
        var feed = await service.GetFeedAsync(alice);

        var ids = feed.Select(IdOf).ToList();
        Assert.Contains($"https://{LocalHost}/notes/a-own", ids);
        Assert.Contains($"https://{LocalHost}/notes/b-1", ids);
    }

    // --- S50 repro: home feed omits the author's own cross-post (a Page) ------------------

    [Fact]
    public async Task Feed_OwnCrossPostPage_SurfacesInHomeFeed()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            SeedActor(persistence, alice, "Alice");

            // A top-level cross-post to a remote (non-Iris, e.g. Lemmy) community carries a Page
            // (138.11). The author's local-outbox publish path records it in alice's outbox; the home
            // feed's own-outbox filter must admit it (Page is content, like a Note).
            var pageIri = $"https://{LocalHost}/notes/a-crosspost";
            persistence.Activities.AddToOutboxAsync(alice, new Create
            {
                Id = pageIri,
                Actor = [new Link { Href = new Uri(alice.Value) }],
                To =
                [
                    new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") },
                    new Link { Href = new Uri("https://b.test/ap/v1/c/lemmy") },
                ],
                Object =
                [
                    new Page
                    {
                        Id = pageIri,
                        Content = ["a cross-posted top-level post (Page)"],
                        AttributedTo = [new Link { Href = new Uri(alice.Value) }],
                    },
                ],
            }).GetAwaiter().GetResult();

            // A plain Note post (the control): it must also surface, proving the filter still works.
            AddPost(persistence, alice, "a-note", "a plain note post");
        }));

        var alice = Actor(LocalHost, "alice");
        var feed = await service.GetFeedAsync(alice);

        // The author's own cross-post (a Page) must surface in the home feed (S50).
        Assert.Contains(feed, f => IdOf(f) == $"https://{LocalHost}/notes/a-crosspost");
        // The author's own Note post (the control) must still surface.
        Assert.Contains(feed, f => IdOf(f) == $"https://{LocalHost}/notes/a-note");
    }

    [Fact]
    public async Task Feed_OwnPostBuries_UnderCapOfActorDocNoise_StillKeepsOwnCreate()
    {
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            SeedActor(persistence, alice, "Alice");
            var aliceIri = alice.Value;

            // 250 actor-document Update activities (the S36 noise): each references the actor IRI, so
            // none is coalesced by the by-content-object pass and none is a reply. With the default
            // MaxItems = 200, the own post (added last) would be truncated to the last 200 of 251 if
            // the cap is applied before the own-outbox items are guaranteed a slot.
            for (var i = 0; i < 250; i++)
            {
                persistence.Activities.AddToOutboxAsync(alice, new Update
                {
                    Id = $"{aliceIri}/noise-{i:D3}",
                    Actor = [new Link { Href = new Uri(aliceIri) }],
                    Object = [new Link { Href = new Uri(aliceIri) }],
                }).GetAwaiter().GetResult();
            }

            // The actor's own content post.
            AddPost(persistence, alice, "a-own", "my own post");
        }));

        var alice = Actor(LocalHost, "alice");
        var feed = await service.GetFeedAsync(alice);

        Assert.Contains(feed, f => IdOf(f) == $"https://{LocalHost}/notes/a-own");
    }

    [Fact]
    public async Task S36_OwnCreatePlusSameObjectAnnouncePlusActorDocNoise_KeepsOwnCreate()
    {
        // The exact S36 shape: the actor's outbox holds their own note's Create AND an Announce (boost)
        // of that same note, plus heavy actor-document noise. The by-content-object coalesce pass groups
        // the Create and the Announce by the note IRI; the embedded Create must survive (the Announce is
        // the redundant one). S36: the live feed showed only the Announce + a Group Create, and the note's
        // own Create was absent.
        var noteIri = $"https://{LocalHost}/notes/a-own";
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            SeedActor(persistence, alice, "Alice");
            var aliceIri = alice.Value;

            // 120 actor-document Update activities (the S36 noise: profile edits / actor-IRI activity).
            for (var i = 0; i < 120; i++)
            {
                persistence.Activities.AddToOutboxAsync(alice, new Update
                {
                    Id = $"{aliceIri}/noise-{i:D3}",
                    Actor = [new Link { Href = new Uri(aliceIri) }],
                    Object = [new Link { Href = new Uri(aliceIri) }],
                }).GetAwaiter().GetResult();
            }

            // The actor's own content post (a Create of the note, embedded).
            AddPost(persistence, alice, "a-own", "my own post");

            // The actor ALSO boosted that same note (an Announce, link-only) — the S36 "Announce (boost
            // of the note)" that appeared in the feed while the Create did not.
            AddAnnounce(persistence, alice, $"{aliceIri}/announce-own", noteIri, embedded: false);
        }));

        var alice = Actor(LocalHost, "alice");
        var feed = await service.GetFeedAsync(alice);

        var createsOfNote = feed
            .OfType<Create>()
            .Where(c => c.Object?.FirstOrDefault() is IObject { Id: var id } && id == noteIri)
            .ToList();
        var announcesOfNote = feed
            .OfType<Announce>()
            .Where(a => a.Object?.FirstOrDefault().ResolveObjectIri()?.Value == noteIri)
            .ToList();

        Assert.True(
            createsOfNote.Count > 0,
            $"the own note Create must survive the coalesce pass; feed had {feed.Count} items, " +
            $"note Creates={createsOfNote.Count}, note Announces={announcesOfNote.Count}");
        // The note must not render twice (Create + Announce coalesced to one).
        Assert.Equal(1, createsOfNote.Count + announcesOfNote.Count);
    }

    [Fact]
    public async Task S36_LiveWireShape_CommunityGroupCreatePlusActorDocNoisePlusOwnNotePlusFollowNote_AllContentCreatesPresent()
    {
        // Reproduces the EXACT S36 live wire shape (ii-a1 feed, 16 items): the actor's outbox holds a
        // community Group Create, heavy actor-document noise (Updates on the actor IRI, self Follow,
        // Like, Undo, Delete, Add/Remove on the actor IRI), the actor's OWN note Create, AND a
        // local-follow's note Create. The live (pre-fix) feed showed NONE of the note Creates and was
        // dominated by the Group Create + actor-doc noise.
        //
        // S24-D2 / S36 (corrected expectation): the home feed's own-outbox branch now keeps only the
        // content the actor THEMSELVES authored (a Create of a Note/Article/Question, or their own
        // Announce). The Group Create (a community join, NOT a post) and the actor-doc noise are
        // excluded — so the feed surfaces the two real note Creates (own + local-follow) and drops the
        // Group Create and all the noise. The pre-fix assertion (Group Create present) encoded the
        // buggy S36 symptom where the Group was the "only content Create" precisely because the note
        // Creates were being dropped.
        var groupIri = $"https://{LocalHost}/c/test-community";
        var (service, _) = Build(persistence: SeedLocal(persistence =>
        {
            var alice = Actor(LocalHost, "alice");
            SeedActor(persistence, alice, "Alice");
            var bob = Actor(LocalHost, "bob");
            SeedActor(persistence, bob, "Bob");
            persistence.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();

            // 1. The community Group Create (the "only content Create" in the live S36 feed).
            persistence.Activities.AddToOutboxAsync(alice, new Create
            {
                Id = $"{alice.Value}/create-group",
                Actor = [new Link { Href = new Uri(alice.Value) }],
                Object = [new Group { Id = groupIri, Name = ["Test Community"] }],
            }).GetAwaiter().GetResult();

            // 2. Heavy actor-document noise (the S36 live feed: Update x several on the actor IRI,
            //    self Follow, Like, Undo, Delete, Add/Remove on the actor IRI, Follow x3).
            var aliceIri = alice.Value;
            for (var i = 0; i < 5; i++)
            {
                persistence.Activities.AddToOutboxAsync(alice, new Update
                {
                    Id = $"{aliceIri}/update-{i}",
                    Actor = [new Link { Href = new Uri(aliceIri) }],
                    Object = [new Link { Href = new Uri(aliceIri) }],
                }).GetAwaiter().GetResult();
            }
            persistence.Activities.AddToOutboxAsync(alice, new Follow
            {
                Id = $"{aliceIri}/follow-self",
                Actor = [new Link { Href = new Uri(aliceIri) }],
                Object = [new Link { Href = new Uri(aliceIri) }],
            }).GetAwaiter().GetResult();
            AddLike(persistence, alice, $"{aliceIri}/like-1", $"https://{LocalHost}/notes/other");
            persistence.Activities.AddToOutboxAsync(alice, new Undo
            {
                Id = $"{aliceIri}/undo-1",
                Actor = [new Link { Href = new Uri(aliceIri) }],
                Object = [new Link { Href = new Uri($"{aliceIri}/follow-self") }],
            }).GetAwaiter().GetResult();
            persistence.Activities.AddToOutboxAsync(alice, new Delete
            {
                Id = $"{aliceIri}/delete-1",
                Actor = [new Link { Href = new Uri(aliceIri) }],
                Object = [new Link { Href = new Uri($"https://{LocalHost}/notes/gone") }],
            }).GetAwaiter().GetResult();
            persistence.Activities.AddToOutboxAsync(alice, new Add
            {
                Id = $"{aliceIri}/add-1",
                Actor = [new Link { Href = new Uri(aliceIri) }],
                Object = [new Link { Href = new Uri(aliceIri) }],
            }).GetAwaiter().GetResult();
            persistence.Activities.AddToOutboxAsync(alice, new Remove
            {
                Id = $"{aliceIri}/remove-1",
                Actor = [new Link { Href = new Uri(aliceIri) }],
                Object = [new Link { Href = new Uri(aliceIri) }],
            }).GetAwaiter().GetResult();
            for (var i = 0; i < 3; i++)
            {
                persistence.Activities.AddToOutboxAsync(alice, new Follow
                {
                    Id = $"{aliceIri}/follow-{i}",
                    Actor = [new Link { Href = new Uri(aliceIri) }],
                    Object = [new Link { Href = new Uri($"https://{LocalHost}/ap/v1/u/follow-target-{i}") }],
                }).GetAwaiter().GetResult();
            }

            // 3. The actor's OWN note Create (absent in the live S36 feed).
            AddPost(persistence, alice, "a-own", "my own post");

            // 4. A local-follow's note Create (absent in the live S36 feed).
            AddPost(persistence, bob, "b-1", "bob 1");
        }));

        var alice = Actor(LocalHost, "alice");
        var feed = await service.GetFeedAsync(alice);

        // The feed contains Create activities; check for the note Creates by their object IRI.
        var createObjectIris = new List<string?>();
        foreach (var create in feed.OfType<Create>())
        {
            if (create.Object is { } objects)
            {
                foreach (var obj in objects)
                {
                    if (obj is IObject io)
                    {
                        createObjectIris.Add(io.Id);
                    }
                }
            }
        }
        // The Group Create (a community join, not a post) is correctly excluded from the content feed.
        Assert.DoesNotContain(groupIri, createObjectIris);
        // The actor's OWN note Create must be present (absent in the live S36 feed).
        Assert.Contains($"https://{LocalHost}/notes/a-own", createObjectIris);
        // The local-follow's note Create must be present (absent in the live S36 feed).
        Assert.Contains($"https://{LocalHost}/notes/b-1", createObjectIris);
    }

    [Fact]
    public async Task S36_OwnPostsSurvive_CapSaturationByLocalFollowPlusBrokenRemotePlusDeliveredContent()
    {
        // S36 live regression net: the home feed is saturated by a LOCAL follow whose outbox exceeds
        // MaxItems (200), AND a broken remote follow whose wire walk yields nothing but whose
        // inbox-delivered content is stored in the object store (the S25 union). The actor's OWN posts
        // (their outbox, merged before the follows) must still surface in the capped feed. This is the
        // in-memory shape of the live S36 symptom (home feed empty even for the actor's own posts)
        // under maximum load from all three merge sources (own outbox, local follow, remote follow's
        // delivered content).
        var local = Actor(LocalHost, "bob");
        var remote = Actor(RemoteHost, "carol");
        var (service, persistence) = Build(
            persistence: SeedLocal(p =>
            {
                var alice = Actor(LocalHost, "alice");
                SeedActor(p, alice, "Alice");
                SeedActor(p, local, "Bob");
                p.Follows.RecordFollowAsync(alice, local).GetAwaiter().GetResult();
                p.Follows.RecordFollowAsync(alice, remote).GetAwaiter().GetResult();

                // The local follow's outbox saturates the MaxItems cap (300 posts > 200).
                for (var i = 0; i < 300; i++)
                {
                    AddPost(p, local, $"lb-{i}", $"bob post {i}");
                }

                // The broken remote follow's delivered content (S25 union): stored in the object store
                // under the remote author, attributed to them (mirrors the inbox write path).
                for (var i = 0; i < 40; i++)
                {
                    p.Objects.PutObjectAsync(new Note
                    {
                        Id = $"https://{RemoteHost}/notes/delivered-{i}",
                        Content = [$"carol delivered {i}"],
                        AttributedTo = [new Link { Href = new Uri(remote.Value) }],
                        To = [new Link { Href = new Uri(Iri.Public.Value) }],
                    }).GetAwaiter().GetResult();
                }

                // The actor's own posts.
                AddPost(p, alice, "a-own-1", "alice own 1");
                AddPost(p, alice, "a-own-2", "alice own 2");
            }),
            actorDocs: new StubActorDocumentFetcher(iri =>
            {
                if (iri == remote)
                {
                    var actor = new Person { Id = remote.Value };
                    actor.Outbox = new Link { Href = new Uri($"{remote.Value}/outbox") };
                    return actor;
                }

                return null;
            }),
            client: new StubClient(Pages())); // the remote outbox walk yields nothing (broken remote)

        var feed = await service.GetFeedAsync(Actor(LocalHost, "alice"));

        // The cap holds (200).
        Assert.Equal(200, feed.Count);
        // The actor's own posts survive the cap saturation.
        Assert.Contains(feed, f => IdOf(f) == $"https://{LocalHost}/notes/a-own-1");
        Assert.Contains(feed, f => IdOf(f) == $"https://{LocalHost}/notes/a-own-2");
    }

    /// <summary>
    /// S36 regression: the home feed must surface the actor's own content <c>Create</c>s (Notes) even
    /// when the outbox is dominated by actor-document noise (Follow/Update/Like/Undo/Delete/Add/Remove)
    /// and the posts carry the standard public-post audience shape (<c>to</c>=Public,
    /// <c>cc</c>=[followers]). Before the S36 fix, <c>IsFollowReply</c>'s audience fallback inspected
    /// both <c>to</c> and <c>cc</c> (via <c>GetAudienceIris</c>), so a top-level post with
    /// <c>cc=[followers]</c> was mistaken for a directed reply and dropped from the home timeline.
    /// </summary>
    [Fact]
    public async Task S36_OwnPostsWithCcFollowers_SurfaceInHomeFeed_AmongActorDocNoise()
    {
        var alice = Actor(LocalHost, "alice");
        var bob = Actor(LocalHost, "bob");
        var (service, persistence) = Build(
            persistence: SeedLocal(p =>
            {
                SeedActor(p, alice, "Alice");
                SeedActor(p, bob, "Bob");
                p.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();

                // Content Creates with the live wire shape (to=Public, cc=followers).
                for (var i = 1; i <= 5; i++)
                {
                    p.Activities.AddToOutboxAsync(alice, new Create
                    {
                        Id = $"https://{LocalHost}/ap/v1/creates/c-{i}",
                        Actor = [new Link { Href = new Uri(alice.Value) }],
                        Object = [new Note
                        {
                            Id = $"https://{LocalHost}/ap/v1/u/alice/notes/n-{i}",
                            Content = [$"S36 post {i}"],
                            AttributedTo = [new Link { Href = new Uri(alice.Value) }],
                            To = [new Link { Href = new Uri(Iri.Public.Value) }],
                            Cc = [new Link { Href = new Uri($"{alice.Value}/followers") }],
                        }],
                    }).GetAwaiter().GetResult();
                }

                // Actor-document noise (the live outbox is dominated by these).
                for (var i = 1; i <= 8; i++)
                {
                    p.Activities.AddToOutboxAsync(alice, new Follow
                    {
                        Id = $"https://{LocalHost}/ap/v1/follows/f-{i}",
                        Actor = [new Link { Href = new Uri(alice.Value) }],
                        Object = [new Link { Href = new Uri(bob.Value) }],
                    }).GetAwaiter().GetResult();
                }
                for (var i = 1; i <= 5; i++)
                {
                    p.Activities.AddToOutboxAsync(alice, new Update
                    {
                        Id = $"https://{LocalHost}/ap/v1/updates/u-{i}",
                        Actor = [new Link { Href = new Uri(alice.Value) }],
                        Object = [new Link { Href = new Uri(alice.Value) }],
                    }).GetAwaiter().GetResult();
                }
                for (var i = 1; i <= 3; i++)
                {
                    p.Activities.AddToOutboxAsync(alice, new Like
                    {
                        Id = $"https://{LocalHost}/ap/v1/likes/l-{i}",
                        Actor = [new Link { Href = new Uri(alice.Value) }],
                        Object = [new Link { Href = new Uri($"https://{LocalHost}/notes/lk-{i}") }],
                    }).GetAwaiter().GetResult();
                }
                for (var i = 1; i <= 3; i++)
                {
                    p.Activities.AddToOutboxAsync(alice, new Undo
                    {
                        Id = $"https://{LocalHost}/ap/v1/undos/und-{i}",
                        Actor = [new Link { Href = new Uri(alice.Value) }],
                        Object = [new Link { Href = new Uri($"https://{LocalHost}/ap/v1/follows/f-{i}") }],
                    }).GetAwaiter().GetResult();
                }
                for (var i = 1; i <= 3; i++)
                {
                    p.Activities.AddToOutboxAsync(alice, new Delete
                    {
                        Id = $"https://{LocalHost}/ap/v1/deletes/d-{i}",
                        Actor = [new Link { Href = new Uri(alice.Value) }],
                        Object = [new Link { Href = new Uri($"https://{LocalHost}/notes/del-{i}") }],
                    }).GetAwaiter().GetResult();
                }
                for (var i = 1; i <= 2; i++)
                {
                    p.Activities.AddToOutboxAsync(alice, new Add
                    {
                        Id = $"https://{LocalHost}/ap/v1/adds/a-{i}",
                        Actor = [new Link { Href = new Uri(alice.Value) }],
                        Object = [new Link { Href = new Uri(alice.Value) }],
                    }).GetAwaiter().GetResult();
                }
                for (var i = 1; i <= 2; i++)
                {
                    p.Activities.AddToOutboxAsync(alice, new Remove
                    {
                        Id = $"https://{LocalHost}/ap/v1/removes/r-{i}",
                        Actor = [new Link { Href = new Uri(alice.Value) }],
                        Object = [new Link { Href = new Uri(alice.Value) }],
                    }).GetAwaiter().GetResult();
                }

                // A local follow with one post (no cc — the simple shape).
                AddPost(p, bob, "b-1", "bob post 1");
            }),
            options: new FeedOptions { MaxItems = 200, PagesPerActor = 1 });

        // The owner's home feed (the endpoint's path: requesterIri = owner, default threadDepth).
        var feed = await service.GetFeedAsync(alice, requesterIri: alice);

        // All 5 of alice's own content Creates (to=Public, cc=followers) must be in the feed.
        for (var i = 1; i <= 5; i++)
        {
            var noteIri = $"https://{LocalHost}/ap/v1/u/alice/notes/n-{i}";
            var found = feed.Any(item =>
                item is Create c && c.Object?.FirstOrDefault() is IObject obj && obj.Id == noteIri);
            Assert.True(found, $"alice's own post {i} (to=Public, cc=followers) must be in the home feed");
        }

        // The followed actor's post is also present.
        Assert.Contains(feed, item =>
            item is Create c2 && c2.Object?.FirstOrDefault() is IObject obj2
            && obj2.Id == $"https://{LocalHost}/notes/b-1");
    }

    /// <summary>
    /// S36 regression (follow-replies still filtered): a followed actor's reply (a <c>Create</c> whose
    /// note has a named <c>to</c> audience — the parent's author) is still excluded from the home feed
    /// at default depth, while the same actor's top-level post (<c>to</c>=Public, <c>cc</c>=followers)
    /// is included. This confirms the S36 fix (inspect only <c>to</c>, not <c>cc</c>) does not
    /// over-include directed replies.
    /// </summary>
    [Fact]
    public async Task S36_FollowReplyWithToNamed_IsStillFiltered_TopLevelWithCcFollowers_IsIncluded()
    {
        var alice = Actor(LocalHost, "alice");
        var bob = Actor(LocalHost, "bob");
        var (service, _) = Build(
            persistence: SeedLocal(p =>
            {
                SeedActor(p, alice, "Alice");
                SeedActor(p, bob, "Bob");
                p.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();

                // Bob's top-level post (to=Public, cc=followers) — must be in the feed.
                p.Activities.AddToOutboxAsync(bob, new Create
                {
                    Id = $"https://{LocalHost}/ap/v1/creates/b-top",
                    Actor = [new Link { Href = new Uri(bob.Value) }],
                    Object = [new Note
                    {
                        Id = $"https://{LocalHost}/ap/v1/u/bob/notes/b-top",
                        Content = ["bob top-level post"],
                        AttributedTo = [new Link { Href = new Uri(bob.Value) }],
                        To = [new Link { Href = new Uri(Iri.Public.Value) }],
                        Cc = [new Link { Href = new Uri($"{bob.Value}/followers") }],
                    }],
                }).GetAwaiter().GetResult();

                // Bob's reply to alice (to=[alice] — a named, non-public audience, no inReplyTo) —
                // must be filtered from the home feed at default depth.
                p.Activities.AddToOutboxAsync(bob, new Create
                {
                    Id = $"https://{LocalHost}/ap/v1/creates/b-reply",
                    Actor = [new Link { Href = new Uri(bob.Value) }],
                    Object = [new Note
                    {
                        Id = $"https://{LocalHost}/ap/v1/u/bob/notes/b-reply",
                        Content = ["bob reply to alice"],
                        AttributedTo = [new Link { Href = new Uri(bob.Value) }],
                        To = [new Link { Href = new Uri(alice.Value) }],
                    }],
                }).GetAwaiter().GetResult();
            }),
            options: new FeedOptions { MaxItems = 200, PagesPerActor = 1 });

        var feed = await service.GetFeedAsync(alice, requesterIri: alice);

        // Bob's top-level post (cc=followers) is included.
        Assert.Contains(feed, item =>
            item is Create c && c.Object?.FirstOrDefault() is IObject o && o.Id == $"https://{LocalHost}/ap/v1/u/bob/notes/b-top");

        // Bob's directed reply (to=[alice]) is filtered.
        Assert.DoesNotContain(feed, item =>
            item is Create c && c.Object?.FirstOrDefault() is IObject o && o.Id == $"https://{LocalHost}/ap/v1/u/bob/notes/b-reply");
    }

    /// <summary>
    /// S24-D2 regression: a foreign boost recorded in the owner's own outbox must not appear in the
    /// owner's home feed. The boost fan-out (AnnounceActivityHandler) records a local follower's boost
    /// of someone else's note in the follower's OWN outbox (an <c>Announce</c> whose <c>actor</c> is the
    /// foreign announcer, not the owner). Without a filter that foreign content pollutes the home feed
    /// (S24-D2: "foreign activities in local outbox") and, together with actor-doc noise, pushes the
    /// owner's real posts out of the MaxItems cap (S36). The own-outbox branch now keeps only the content
    /// the owner THEMSELVES authored: the foreign boost (actor = the announcer) is excluded, while the
    /// owner's own note and a followed actor's note (read via the follow branch, where the foreign
    /// content is legitimate) are both present.
    /// </summary>
    [Fact]
    public async Task S24D2_ForeignBoostInOwnOutbox_IsExcluded_OwnPostAndFollowPost_Present()
    {
        var alice = Actor(LocalHost, "alice");
        var bob = Actor(LocalHost, "bob");
        var carol = Actor(LocalHost, "carol");
        var (service, _) = Build(
            persistence: SeedLocal(p =>
            {
                SeedActor(p, alice, "Alice");
                SeedActor(p, bob, "Bob");
                SeedActor(p, carol, "Carol");
                // alice follows bob (so bob's post is legitimate content in alice's feed).
                p.Follows.RecordFollowAsync(alice, bob).GetAwaiter().GetResult();

                // alice's own note (to=Public, cc=followers) — must be in the feed.
                AddPost(p, alice, "a-own", "alice own post");

                // A FOREIGN boost recorded in alice's own outbox by the fan-out: an Announce of bob's
                // note whose actor is carol (the announcer), NOT alice. This is the S24-D2 foreign
                // content — it must be excluded from alice's home feed.
                p.Activities.AddToOutboxAsync(alice, new Announce
                {
                    Id = $"{carol.Value}/announces/b-note",
                    Actor = [new Link { Href = new Uri(carol.Value) }],
                    Object = [new Link { Href = new Uri($"https://{LocalHost}/notes/b-1") }],
                }).GetAwaiter().GetResult();

                // bob's note (the object of the foreign boost) in BOB's outbox — read via the follow
                // branch, where it is legitimate content. Must be in the feed.
                AddPost(p, bob, "b-1", "bob post 1");
            }),
            options: new FeedOptions { MaxItems = 200, PagesPerActor = 1 });

        var feed = await service.GetFeedAsync(alice, requesterIri: alice);

        // alice's own note is present.
        Assert.Contains(feed, item =>
            item is Create c && c.Object?.FirstOrDefault() is IObject o && o.Id == $"https://{LocalHost}/notes/a-own");

        // bob's note (via the follow branch) is present.
        Assert.Contains(feed, item =>
            item is Create c && c.Object?.FirstOrDefault() is IObject o && o.Id == $"https://{LocalHost}/notes/b-1");

        // The foreign boost (an Announce of bob's note authored by carol) is NOT present in alice's
        // home feed — the only Announce of bob's note in the feed would be this foreign one.
        Assert.DoesNotContain(feed, item =>
            item is Announce a && a.Object?.FirstOrDefault().ResolveObjectIri() is { } obj
            && obj.Value == $"https://{LocalHost}/notes/b-1");
    }

    // --- Builders --------------------------------------------------------------------

    private static (FeedService Service, InMemoryPersistenceProvider Persistence) Build(
        InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? actorDocs = null,
        IActivityPubClient? client = null,
        FeedOptions? options = null,
        IFeedCircuitBreaker? circuitBreaker = null)
    {
        actorDocs ??= new StubActorDocumentFetcher(_ => null);
        client ??= new StubClient(Pages());
        var localActors = new LocalOnlyResolver(persistence);
        return (
            new FeedService(
                persistence, localActors, actorDocs, client, Options.Create(options ?? new FeedOptions()),
                circuitBreaker: circuitBreaker),
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
            Object = [new Note { Id = activityIri, Content = [content] }],
        }).GetAwaiter().GetResult();

    /// <summary>
    /// Seeds a <c>Create</c> of a note that is a reply to <paramref name="repliedToIri"/> (the note's
    /// <c>inReplyTo</c> field contains the replied-to actor, and its <c>to</c> field contains the
    /// replied-to actor as audience, making it a non-public audience).
    /// </summary>
    private static void AddReply(
        InMemoryPersistenceProvider persistence, Iri actorIri, string suffix, string content, Iri repliedToIri)
    {
        var noteIri = $"https://{LocalHost}/notes/{suffix}";
        persistence.Activities.AddToOutboxAsync(actorIri, new Create
        {
            Id = noteIri,
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Note
            {
                Id = noteIri,
                Content = [content],
                InReplyTo = [new Link { Href = new Uri(repliedToIri.Value) }],
                To = [new Link { Href = new Uri(repliedToIri.Value) }, new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
            }],
        }).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Seeds an <c>Announce</c> (boost) of <paramref name="objectIri"/> into the actor's outbox. When
    /// <paramref name="embedded"/> the object is carried as a full <see cref="Note"/>; otherwise it is a
    /// bare <see cref="Link"/> (the common remote-boost shape, where the note is not re-embedded).
    /// </summary>
    private static void AddAnnounce(
        InMemoryPersistenceProvider persistence, Iri actorIri, string activityIri, string objectIri, bool embedded)
        => persistence.Activities.AddToOutboxAsync(actorIri, new Announce
        {
            Id = activityIri,
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = embedded
                ? [new Note { Id = objectIri, Content = ["boosted"] }]
                : [new Link { Href = new Uri(objectIri) }],
        }).GetAwaiter().GetResult();

    /// <summary>
    /// Seeds a <c>Like</c> of <paramref name="objectIri"/> into the actor's outbox (a social activity —
    /// never coalesced by the by-content-object pass).
    /// </summary>
    private static void AddLike(InMemoryPersistenceProvider persistence, Iri actorIri, string activityIri, string objectIri)
        => persistence.Activities.AddToOutboxAsync(actorIri, new Like
        {
            Id = activityIri,
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(objectIri) }],
        }).GetAwaiter().GetResult();

    /// <summary>
    /// Builds a <c>Create</c> of a note at <paramref name="noteIri"/> — used as a remote outbox page item
    /// (the author's own post). When <paramref name="embedded"/> the note is a full <see cref="Note"/>
    /// (rich, renderable in place); otherwise a bare <see cref="Link"/>.
    /// </summary>
    private static IObjectOrLink CreateItem(string noteIri, bool embedded) => new Create
    {
        Id = $"{noteIri}/activity",
        Actor = [new Link { Href = new Uri($"https://{RemoteHost}/ap/v1/u/author") }],
        Object = embedded
            ? [new Note { Id = noteIri, Content = ["remote note"] }]
            : [new Link { Href = new Uri(noteIri) }],
    };

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

    private static Link Item(string host, string suffix) => new() { Href = new Uri($"https://{host}/notes/{suffix}") };

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
    /// An <see cref="IActorDocumentFetcher"/> that always throws — simulating a transport error, timeout,
    /// or signing-key failure in the outbound actor-document fetch (a condition the contract says should
    /// return null, but the implementation can still throw).
    /// </summary>
    private sealed class ThrowingActorDocumentFetcher : IActorDocumentFetcher
    {
        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => throw new HttpRequestException("simulated outbound actor-doc fetch failure");
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

        public Task<IObject?> GetObjectFreshAsync(Iri objectId, CancellationToken ct = default)
            => GetObjectAsync(objectId, ct);

        public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
            => Task.FromResult<Actor?>(null);

        public Task<NodeInfo?> GetNodeInfoAsync(Iri instanceBase, CancellationToken ct = default)
            => Task.FromResult<NodeInfo?>(null);

        public Task<DeliveryResult> DeliverAsync(Iri targetId, IObject activity, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> UndoFollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> RejectAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> RequestJoinAsync(Iri actorId, Iri communityIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> RequestLeaveAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> AcceptJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> RejectJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> SetManuallyApprovesMembersAsync(Iri communityIri, bool enabled, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> SetManuallyApprovesFollowersAsync(Iri actorIri, bool enabled, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> UnlikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> UnannounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> CreateCommunityAsync(Iri actorId, string name, string displayName, string? description = null, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> UpdateActorAsync(Iri actorId, Actor updatedActor, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<LemmyPostScore?> GetLemmyPostScoreAsync(Iri iri, CancellationToken ct = default) => Task.FromResult<LemmyPostScore?>(null);
        public Task<DeliveryResult> DislikeAsync(Iri objectIri, Iri actorIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> UndislikeAsync(Iri objectIri, Iri actorIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> PostNoteAsync(Iri actorId, Note note, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> PostQuestionAsync(
            Iri actorId,
            string content,
            IEnumerable<string> options,
            DateTime? endsAt = null,
            bool multiple = false,
            IEnumerable<Iri>? to = null,
            IEnumerable<Iri>? cc = null,
            IEnumerable<Iri>? mentions = null,
            IEnumerable<string>? hashtags = null,
            Func<string, string?>? hashtagHrefFactory = null,
            CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> PostReplyAsync(
            Iri actorId,
            Iri parentIri,
            string content,
            IEnumerable<Iri>? mentions = null,
            IEnumerable<Iri>? to = null,
            IEnumerable<string>? hashtags = null,
            Iri? conversationIri = null,
            CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public Task<DeliveryResult> UpdateNoteAsync(Iri actorId, Note updatedNote, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(202, true, ""));

        public async IAsyncEnumerable<IObjectOrLink> GetInboxItemsAsync(
            Iri actorId, Iris.Client.Pipeline.ProxyCredentials credentials, CollectionQuery? query = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }

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

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetLikesAsync(
            Iri objectIri,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetSharesAsync(
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

        public IAsyncEnumerable<CollectionPage> SearchPagedAsync(
            Iri instanceBase,
            string? query = null,
            SearchOptions? options = null,
            CancellationToken ct = default)
            => EmptyAsync<CollectionPage>(ct);

        public Task<DeliveryResult> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(0, false, ""));

        public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public Task<DeliveryResult> UnblockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(0, false, ""));

        public Task<DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(0, false, ""));

        public Task<DeliveryResult> UnflagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult(0, false, ""));

        public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        public IAsyncEnumerable<IObjectOrLink> GetRelaysAsync(
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

    /// <summary>
    /// An <see cref="IActivityPubClient"/> that counts how many times <see cref="GetCollectionAsync"/> is
    /// called and yields a single page of public <c>Create</c> activities for the healthy outbox IRI (and
    /// nothing for the dead one). Used to verify the Phase 146 feed circuit breaker stops re-probing an
    /// open peer (the dead peer's fetch count stays flat while its circuit is open, then a single probe
    /// fires in half-open), while a healthy peer is fetched on every rebuild. The yielded items are public
    /// <c>Create</c> activities (the shape <c>GetDeliveredContentAsync</c> wraps remote content into), so
    /// they survive the feed's visibility filter for an anonymous request. All other client methods are
    /// inert.
    /// </summary>
    private sealed class CountingClient : IActivityPubClient
    {
        private readonly string? _healthyOutboxIri;
        private readonly string? _healthyNoteIri;
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _fetchesByOutbox = new();

        public CountingClient(string? healthyOutboxIri, string? healthyNoteIri)
        {
            _healthyOutboxIri = healthyOutboxIri;
            _healthyNoteIri = healthyNoteIri;
        }

        public int TotalFetchCount
        {
            get { lock (_gate) { return _fetchesByOutbox.Values.Sum(); } }
        }

        public int FetchCountFor(string outboxIri)
        {
            lock (_gate)
            {
                return _fetchesByOutbox.TryGetValue(outboxIri, out var n) ? n : 0;
            }
        }

        public async IAsyncEnumerable<CollectionPage> GetCollectionAsync(
            Iri collectionId,
            CollectionQuery? query = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (_gate)
            {
                _fetchesByOutbox[collectionId.Value] = _fetchesByOutbox.GetValueOrDefault(collectionId.Value) + 1;
            }

            await Task.CompletedTask.ConfigureAwait(false);
            // Only the healthy outbox IRI yields content; every other (dead) outbox yields nothing.
            if (_healthyOutboxIri is { } healthy && collectionId.Value == healthy && _healthyNoteIri is { } noteIri)
            {
                var uri = new Uri(collectionId.Value);
                var authorIri = $"https://{uri.Authority}/ap/v1/u/author";
                var create = new Create
                {
                    Id = $"{noteIri}/activity",
                    Actor = [new Link { Href = new Uri(authorIri) }],
                    Object = [new Note
                    {
                        Id = noteIri,
                        Content = ["remote note"],
                        AttributedTo = [new Link { Href = new Uri(authorIri) }],
                        To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
                    }],
                };
                var page = new OrderedCollectionPage
                {
                    Id = collectionId.Value,
                    Items = [create],
                };
                yield return CollectionPageFactory.FromOrderedCollectionPage(page)!;
            }
        }

        public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default) => Task.FromResult<IObject?>(null);
        public Task<IObject?> GetObjectFreshAsync(Iri objectId, CancellationToken ct = default) => Task.FromResult<IObject?>(null);
        public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default) => Task.FromResult<Actor?>(null);
        public Task<NodeInfo?> GetNodeInfoAsync(Iri instanceBase, CancellationToken ct = default) => Task.FromResult<NodeInfo?>(null);
        public Task<DeliveryResult> DeliverAsync(Iri targetId, IObject activity, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> UndoFollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> RejectAsync(Iri actorId, Iri followIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> RequestJoinAsync(Iri actorId, Iri communityIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> RequestLeaveAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> AcceptJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> RejectJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> SetManuallyApprovesMembersAsync(Iri actorIri, bool enabled, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> SetManuallyApprovesFollowersAsync(Iri actorIri, bool enabled, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> UnlikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> UnannounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> CreateCommunityAsync(Iri actorId, string name, string displayName, string? description = null, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> UpdateActorAsync(Iri actorId, Actor updatedActor, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<LemmyPostScore?> GetLemmyPostScoreAsync(Iri iri, CancellationToken ct = default) => Task.FromResult<LemmyPostScore?>(null);
        public Task<DeliveryResult> DislikeAsync(Iri objectIri, Iri actorIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> UndislikeAsync(Iri objectIri, Iri actorIri, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> PostNoteAsync(Iri actorId, Note note, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> PostQuestionAsync(Iri actorId, string content, IEnumerable<string> options, DateTime? endsAt = null, bool multiple = false, IEnumerable<Iri>? to = null, IEnumerable<Iri>? cc = null, IEnumerable<Iri>? mentions = null, IEnumerable<string>? hashtags = null, Func<string, string?>? hashtagHrefFactory = null, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> PostReplyAsync(Iri actorId, Iri parentIri, string content, IEnumerable<Iri>? mentions = null, IEnumerable<Iri>? to = null, IEnumerable<string>? cc = null, Iri? conversationIri = null, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public Task<DeliveryResult> UpdateNoteAsync(Iri actorId, Note updatedNote, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(202, true, ""));
        public async IAsyncEnumerable<IObjectOrLink> GetInboxItemsAsync(
            Iri actorId,
            Iris.Client.Pipeline.ProxyCredentials credentials,
            CollectionQuery? query = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) });
        public IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(Iri communityId, CollectionQuery? query = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public IAsyncEnumerable<IObjectOrLink> GetRepliesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public IAsyncEnumerable<IObjectOrLink> GetLikesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public IAsyncEnumerable<IObjectOrLink> GetSharesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public IAsyncEnumerable<IObjectOrLink> SearchAsync(Iri instanceBase, string? query = null, SearchOptions? options = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public IAsyncEnumerable<CollectionPage> SearchPagedAsync(Iri instanceBase, string? query = null, SearchOptions? options = null, CancellationToken ct = default) => EmptyAsync<CollectionPage>(ct);
        public Task<DeliveryResult> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(0, false, ""));
        public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public Task<DeliveryResult> UnblockAsync(Iri actorId, Iri targetId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(0, false, ""));
        public Task<DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(0, false, ""));
        public Task<DeliveryResult> UnflagAsync(Iri actorId, Iri targetId, CancellationToken ct = default) => Task.FromResult(new DeliveryResult(0, false, ""));
        public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public IAsyncEnumerable<IObjectOrLink> GetRelaysAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default) => EmptyAsync<IObjectOrLink>(ct);
        public void Dispose() { }

        private static async IAsyncEnumerable<T> EmptyAsync<T>([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
