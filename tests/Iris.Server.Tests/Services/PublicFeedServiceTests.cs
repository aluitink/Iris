using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Services;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PublicFeedService"/>: the instance-wide public timeline that merges all
/// local actors' outbox activities. The key behavior under test is <strong>global date ordering</strong>
/// — the merged feed must be sorted newest-first by the activity's <c>published</c> date, not grouped by
/// actor. The per-actor outboxes are each newest-first, but the merge concatenates them in actor-IRI
/// order, which would group an actor's older posts before another actor's newer ones if left unsorted.
/// </summary>
public sealed class PublicFeedServiceTests
{
    private const string Host = "a.test";

    private static Iri Actor(string handle) => new($"https://{Host}/ap/v1/u/{handle}");

    // --- Global date ordering --------------------------------------------------------

    [Fact]
    public async Task Feed_MergesActorOutboxes_SortedNewestFirstByDate()
    {
        // Seed two actors: alice (alphabetically first) and bob. Alice has an OLD post (3 days ago);
        // bob has a NEW post (just now). Without the date-sort, alice's old post would appear first
        // (because alice is alphabetically first and her outbox is read first). With the date-sort,
        // bob's newer post must appear first.
        var persistence = new InMemoryPersistenceProvider();
        var alice = Actor("alice");
        var bob = Actor("bob");
        SeedActor(persistence, alice, "Alice");
        SeedActor(persistence, bob, "Bob");

        var threeDaysAgo = DateTime.UtcNow.AddDays(-3);
        var now = DateTime.UtcNow;

        // Alice's post: 3 days ago (old).
        AddPost(persistence, alice, "alice-old", "alice old post", threeDaysAgo);
        // Bob's post: just now (new).
        AddPost(persistence, bob, "bob-new", "bob new post", now);

        var service = new PublicFeedService(persistence);
        var feed = await service.GetPublicFeedAsync(maxItems: 20);

        // The feed must be sorted newest-first: bob's new post first, alice's old post second.
        Assert.Equal(2, feed.Count);
        Assert.Equal("bob-new", IdOf(feed[0]));
        Assert.Equal("alice-old", IdOf(feed[1]));
    }

    [Fact]
    public async Task Feed_MultipleActors_AllPostsSortedNewestFirst()
    {
        // Seed three actors with posts at staggered dates. Verify the global sort is correct.
        var persistence = new InMemoryPersistenceProvider();
        var alice = Actor("alice");
        var bob = Actor("bob");
        var carol = Actor("carol");
        SeedActor(persistence, alice, "Alice");
        SeedActor(persistence, bob, "Bob");
        SeedActor(persistence, carol, "Carol");

        var t0 = DateTime.UtcNow.AddDays(-5);
        var t1 = DateTime.UtcNow.AddDays(-3);
        var t2 = DateTime.UtcNow.AddDays(-1);
        var t3 = DateTime.UtcNow;

        // Interleave posts across actors so the alphabetical merge order would be wrong.
        AddPost(persistence, alice, "a-t0", "alice t0", t0); // oldest
        AddPost(persistence, bob, "b-t2", "bob t2", t2);
        AddPost(persistence, carol, "c-t3", "carol t3", t3); // newest
        AddPost(persistence, alice, "a-t1", "alice t1", t1);
        AddPost(persistence, bob, "b-t0b", "bob t0b", t0.AddHours(1));

        var service = new PublicFeedService(persistence);
        var feed = await service.GetPublicFeedAsync(maxItems: 20);

        // Expected global order (newest-first): carol t3, bob t2, alice t1, bob t0b, alice t0.
        Assert.Equal(5, feed.Count);
        Assert.Equal("c-t3", IdOf(feed[0]));
        Assert.Equal("b-t2", IdOf(feed[1]));
        Assert.Equal("a-t1", IdOf(feed[2]));
        Assert.Equal("b-t0b", IdOf(feed[3]));
        Assert.Equal("a-t0", IdOf(feed[4]));
    }

    [Fact]
    public async Task Feed_PostsSameDate_StableMergeOrder()
    {
        // When two posts have the same published date, their relative order is the stable merge order
        // (actor-IRI order within the same date). alice is alphabetically first, so alice's post
        // appears before bob's post when both have the same date.
        var persistence = new InMemoryPersistenceProvider();
        var alice = Actor("alice");
        var bob = Actor("bob");
        SeedActor(persistence, alice, "Alice");
        SeedActor(persistence, bob, "Bob");

        var sameDate = DateTime.UtcNow;
        AddPost(persistence, alice, "a-same", "alice same", sameDate);
        AddPost(persistence, bob, "b-same", "bob same", sameDate);

        var service = new PublicFeedService(persistence);
        var feed = await service.GetPublicFeedAsync(maxItems: 20);

        Assert.Equal(2, feed.Count);
        // Stable sort: alice (alphabetically first) before bob.
        Assert.Equal("a-same", IdOf(feed[0]));
        Assert.Equal("b-same", IdOf(feed[1]));
    }

    [Fact]
    public async Task Feed_NoPublishedDate_SortsLast()
    {
        // A post with no published date sorts last (after all dated posts).
        var persistence = new InMemoryPersistenceProvider();
        var alice = Actor("alice");
        var bob = Actor("bob");
        SeedActor(persistence, alice, "Alice");
        SeedActor(persistence, bob, "Bob");

        var now = DateTime.UtcNow;
        AddPost(persistence, alice, "a-dated", "alice dated", now);
        AddPostNoDate(persistence, bob, "b-undated", "bob undated");

        var service = new PublicFeedService(persistence);
        var feed = await service.GetPublicFeedAsync(maxItems: 20);

        Assert.Equal(2, feed.Count);
        // Dated post first, undated post last.
        Assert.Equal("a-dated", IdOf(feed[0]));
        Assert.Equal("b-undated", IdOf(feed[1]));
    }

    [Fact]
    public async Task Feed_EmptyOutboxes_ReturnsEmpty()
    {
        var persistence = new InMemoryPersistenceProvider();
        var alice = Actor("alice");
        SeedActor(persistence, alice, "Alice");
        // No posts.

        var service = new PublicFeedService(persistence);
        var feed = await service.GetPublicFeedAsync(maxItems: 20);
        Assert.Empty(feed);
    }

    [Fact]
    public async Task Feed_ExcludesNonPersonActors()
    {
        // Groups (communities) are excluded from the public feed.
        var persistence = new InMemoryPersistenceProvider();
        var alice = Actor("alice");
        var group = new Iri($"https://{Host}/ap/v1/c/test");
        SeedActor(persistence, alice, "Alice");
        SeedGroup(persistence, group, "Test Group");
        AddPost(persistence, alice, "a-post", "alice post", DateTime.UtcNow);
        AddPost(persistence, group, "g-post", "group post", DateTime.UtcNow);

        var service = new PublicFeedService(persistence);
        var feed = await service.GetPublicFeedAsync(maxItems: 20);

        // Only alice's post (the group's post is excluded).
        Assert.Single(feed);
        Assert.Equal("a-post", IdOf(feed[0]));
    }

    // --- Helpers ---------------------------------------------------------------------

    private static void SeedActor(InMemoryPersistenceProvider persistence, Iri actorIri, string name)
        => persistence.Actors.PutActorAsync(new Person { Id = actorIri.Value, Name = [name] })
            .GetAwaiter().GetResult();

    private static void SeedGroup(InMemoryPersistenceProvider persistence, Iri groupIri, string name)
        => persistence.Actors.PutActorAsync(new Group { Id = groupIri.Value, Name = [name] })
            .GetAwaiter().GetResult();

    private static void AddPost(
        InMemoryPersistenceProvider persistence,
        Iri actorIri,
        string suffix,
        string content,
        DateTime published)
    {
        var activityIri = $"https://{Host}/notes/{suffix}";
        persistence.Activities.AddToOutboxAsync(actorIri, new Create
        {
            Id = activityIri,
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Note { Id = activityIri, Content = [content] }],
            Published = published,
        }).GetAwaiter().GetResult();
    }

    private static void AddPostNoDate(
        InMemoryPersistenceProvider persistence,
        Iri actorIri,
        string suffix,
        string content)
    {
        var activityIri = $"https://{Host}/notes/{suffix}";
        persistence.Activities.AddToOutboxAsync(actorIri, new Create
        {
            Id = activityIri,
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Note { Id = activityIri, Content = [content] }],
        }).GetAwaiter().GetResult();
    }

    private static string IdOf(IObjectOrLink item)
    {
        if (item is Create create)
        {
            return create.Id?.Replace("https://a.test/notes/", "") ?? "?";
        }

        if (item is IObject { Id: { } id })
        {
            return id.Replace("https://a.test/notes/", "");
        }

        return "?";
    }
}
