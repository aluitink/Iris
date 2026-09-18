using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Services;
using KristofferStrube.ActivityStreams;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for <see cref="VisibilityFilter"/> (the 139.2-s5 audience/visibility predicate). These
/// exercise the rule directly, with no infrastructure: a post is public when its <c>to</c>/<c>cc</c>
/// carries the public sentinel or names no audience, and a post that names a non-public audience is
/// visible only to its intended recipient.
/// </summary>
public sealed class VisibilityFilterTests
{
    private static IObject NoteWithAudience(params IObjectOrLink[] to) => new Note
    {
        Id = "https://iris.example/ap/v1/u/alice/notes/x",
        Content = ["body"],
        To = to,
    };

    private static IObjectOrLink PublicAudience() => new Link { Href = new Uri(Iri.Public.Value) };

    [Fact]
    public void PublicNote_IsVisibleToAnyone()
    {
        var note = NoteWithAudience(PublicAudience());
        Assert.True(VisibilityFilter.IsVisibleTo(note, null));
        Assert.True(VisibilityFilter.IsVisibleTo(note, new Iri("https://iris.example/ap/v1/u/bob")));
    }

    [Fact]
    public void NoAudienceNote_IsPublicByConvention()
    {
        var note = new Note { Id = "https://iris.example/ap/v1/u/alice/notes/y", Content = ["body"] };
        Assert.True(VisibilityFilter.IsPublic(note));
        Assert.True(VisibilityFilter.IsVisibleTo(note, null));
    }

    [Fact]
    public void DmNote_HiddenFromAnonymousAndNonRecipient_VisibleToRecipient()
    {
        var bob = new Iri("https://iris.example/ap/v1/u/bob");
        var carol = new Iri("https://iris.example/ap/v1/u/carol");
        var dm = NoteWithAudience(new Link { Href = new Uri(bob.Value) });

        Assert.False(VisibilityFilter.IsPublic(dm));
        Assert.False(VisibilityFilter.IsVisibleTo(dm, null));
        Assert.False(VisibilityFilter.IsVisibleTo(dm, carol));
        Assert.True(VisibilityFilter.IsVisibleTo(dm, bob));
    }

    [Fact]
    public void FollowersOnlyNote_VisibleOnlyToFollower()
    {
        var follower = new Iri("https://iris.example/ap/v1/u/follower");
        var stranger = new Iri("https://iris.example/ap/v1/u/stranger");
        // Followers-only: to = [follower], no public sentinel.
        var note = NoteWithAudience(new Link { Href = new Uri(follower.Value) });

        Assert.False(VisibilityFilter.IsPublic(note));
        Assert.False(VisibilityFilter.IsVisibleTo(note, null));
        Assert.False(VisibilityFilter.IsVisibleTo(note, stranger));
        Assert.True(VisibilityFilter.IsVisibleTo(note, follower));
    }

    [Fact]
    public void PublicPlusNamedRecipient_VisibleToEveryone()
    {
        // A post addressed to as:Public AND a specific actor is public (the sentinel makes it visible
        // to everyone; the named recipient is just an explicit mention).
        var note = NoteWithAudience(PublicAudience(), new Link { Href = new Uri("https://iris.example/ap/v1/u/bob") });
        Assert.True(VisibilityFilter.IsPublic(note));
        Assert.True(VisibilityFilter.IsVisibleTo(note, null));
    }

    [Fact]
    public void NullObject_IsVisible()
    {
        // No audience information to contradict visibility — keep the item rather than drop it.
        Assert.True(VisibilityFilter.IsVisibleTo(null, null));
        Assert.True(VisibilityFilter.IsPublic(null));
    }

    [Fact]
    public void FeedItemCreateActivity_UsesEmbeddedObjectAudience()
    {
        var bob = new Iri("https://iris.example/ap/v1/u/bob");
        var dmNote = NoteWithAudience(new Link { Href = new Uri(bob.Value) });
        var create = new Create
        {
            Id = "https://iris.example/ap/v1/u/alice/activities/create-dm",
            Actor = [new Link { Href = new Uri("https://iris.example/ap/v1/u/alice") }],
            Object = [dmNote],
        };

        // Anonymous / non-recipient cannot see the DM wrapped in a Create; the recipient can.
        Assert.False(VisibilityFilter.IsFeedItemVisibleTo(create, null));
        Assert.False(VisibilityFilter.IsFeedItemVisibleTo(create, new Iri("https://iris.example/ap/v1/u/carol")));
        Assert.True(VisibilityFilter.IsFeedItemVisibleTo(create, bob));

        var publicNote = NoteWithAudience(PublicAudience());
        var publicCreate = new Create
        {
            Id = "https://iris.example/ap/v1/u/alice/activities/create-pub",
            Actor = [new Link { Href = new Uri("https://iris.example/ap/v1/u/alice") }],
            Object = [publicNote],
        };
        Assert.True(VisibilityFilter.IsFeedItemVisibleTo(publicCreate, null));
    }

    [Fact]
    public void FeedItemBareObject_UsesItsOwnAudience()
    {
        var note = NoteWithAudience(PublicAudience());
        Assert.True(VisibilityFilter.IsFeedItemVisibleTo(note, null));

        var dm = NoteWithAudience(new Link { Href = new Uri("https://iris.example/ap/v1/u/bob") });
        Assert.False(VisibilityFilter.IsFeedItemVisibleTo(dm, null));
    }

    [Fact]
    public void FeedItemWithoutObject_KeptNotDropped()
    {
        // An activity with no extractable object has no assessable audience — keep it.
        var bareActivity = new Create
        {
            Id = "https://iris.example/ap/v1/u/alice/activities/create-bare",
        };
        Assert.True(VisibilityFilter.IsFeedItemVisibleTo(bareActivity, null));
    }
}

/// <summary>
/// Service-level tests for the audience/visibility filter wired into <see cref="PublicFeedService"/>
/// and <see cref="GlobalSearchService"/> (139.2-s5). Seeds an in-memory store with a public post, a
/// followers-only post, and a direct (DM) post, then asserts each read surface returns the right
/// items for an anonymous request vs. each recipient.
/// </summary>
public sealed class VisibilityFilterFeedAndSearchTests
{
    private const string Host = "iris.example";
    private static readonly Iri Alice = new($"https://{Host}/ap/v1/u/alice");
    private static readonly Iri Bob = new($"https://{Host}/ap/v1/u/bob");
    private static readonly Iri Carol = new($"https://{Host}/ap/v1/u/carol");

    private static Iri NoteIri(string tag) => new($"https://{Host}/ap/v1/u/alice/notes/{tag}-{Guid.NewGuid():N}");

    private static async Task<InMemoryPersistenceProvider> SeedAsync()
    {
        var p = new InMemoryPersistenceProvider();
        await p.Actors.PutActorAsync(new Person
        {
            Id = Alice.Value,
            PreferredUsername = "alice",
            Name = ["Alice"],
        });

        // Public post (as:Public), followers-only (to=[bob]), and a DM (to=[carol]).
        var pub = NoteIri("pub");
        var followersOnly = NoteIri("fol");
        var dm = NoteIri("dm");

        await AddCreateAsync(p, pub, to: [new Link { Href = new Uri(Iri.Public.Value) }], content: "public post content");
        await AddCreateAsync(p, followersOnly, to: [new Link { Href = new Uri(Bob.Value) }], content: "followers only content");
        await AddCreateAsync(p, dm, to: [new Link { Href = new Uri(Carol.Value) }], content: "direct message content");

        // Content objects for the search surface (the object store holds the notes themselves).
        await p.Objects.PutObjectAsync(Note(pub, "public post content", to: [new Link { Href = new Uri(Iri.Public.Value) }]));
        await p.Objects.PutObjectAsync(Note(followersOnly, "followers only content", to: [new Link { Href = new Uri(Bob.Value) }]));
        await p.Objects.PutObjectAsync(Note(dm, "direct message content", to: [new Link { Href = new Uri(Carol.Value) }]));

        return p;
    }

    private static async Task AddCreateAsync(InMemoryPersistenceProvider p, Iri noteIri, IObjectOrLink[] to, string content)
    {
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(Alice.Value) }],
            Object = [Note(noteIri, content, to)],
        };
        await p.Activities.AddToOutboxAsync(Alice, create);
    }

    private static IObject Note(Iri iri, string content, IObjectOrLink[] to) => new Note
    {
        Id = iri.Value,
        Content = [content],
        AttributedTo = [new Link { Href = new Uri(Alice.Value) }],
        To = to,
    };

    private static string[] Ids(IReadOnlyList<IObjectOrLink> items) =>
        items.Where(i => i is IObject { Id: { } id }).Select(i => (i as IObject)!.Id!).ToArray();

    [Fact]
    public async Task PublicFeed_Anonymous_SeesOnlyPublic()
    {
        var p = await SeedAsync();
        var feed = new PublicFeedService(p);
        var ids = Ids(await feed.GetPublicFeedAsync(50, requesterIri: null));

        Assert.Contains(ids, id => id.Contains("notes/pub-"));
        Assert.DoesNotContain(ids, id => id.Contains("notes/fol-"));
        Assert.DoesNotContain(ids, id => id.Contains("notes/dm-"));
    }

    [Fact]
    public async Task PublicFeed_Recipient_SeesPublicPlusTheirContent()
    {
        var p = await SeedAsync();
        var feed = new PublicFeedService(p);

        // bob is the recipient of the followers-only post.
        var bobIds = Ids(await feed.GetPublicFeedAsync(50, requesterIri: Bob));
        Assert.Contains(bobIds, id => id.Contains("notes/pub-"));
        Assert.Contains(bobIds, id => id.Contains("notes/fol-"));
        Assert.DoesNotContain(bobIds, id => id.Contains("notes/dm-")); // carol's DM, not bob's

        // carol is the recipient of the DM.
        var carolIds = Ids(await feed.GetPublicFeedAsync(50, requesterIri: Carol));
        Assert.Contains(carolIds, id => id.Contains("notes/pub-"));
        Assert.DoesNotContain(carolIds, id => id.Contains("notes/fol-")); // bob's followers-only
        Assert.Contains(carolIds, id => id.Contains("notes/dm-"));
    }

    [Fact]
    public async Task PublicFeed_NonRecipient_SeesOnlyPublic()
    {
        var p = await SeedAsync();
        var feed = new PublicFeedService(p);
        var stranger = new Iri($"https://{Host}/ap/v1/u/stranger");
        var ids = Ids(await feed.GetPublicFeedAsync(50, requesterIri: stranger));

        Assert.Contains(ids, id => id.Contains("notes/pub-"));
        Assert.DoesNotContain(ids, id => id.Contains("notes/fol-"));
        Assert.DoesNotContain(ids, id => id.Contains("notes/dm-"));
    }

    [Fact]
    public async Task Search_Anonymous_SeesOnlyPublicContent()
    {
        var p = await SeedAsync();
        var search = new GlobalSearchService(p);
        var items = await search.SearchAsync("content", requesterIri: null);
        var ids = Ids(items);

        Assert.Contains(ids, id => id.Contains("notes/pub-"));
        Assert.DoesNotContain(ids, id => id.Contains("notes/fol-"));
        Assert.DoesNotContain(ids, id => id.Contains("notes/dm-"));
    }

    [Fact]
    public async Task Search_Recipient_SeesPublicPlusTheirContent()
    {
        var p = await SeedAsync();
        var search = new GlobalSearchService(p);

        var bobIds = Ids(await search.SearchAsync("content", requesterIri: Bob));
        Assert.Contains(bobIds, id => id.Contains("notes/pub-"));
        Assert.Contains(bobIds, id => id.Contains("notes/fol-"));
        Assert.DoesNotContain(bobIds, id => id.Contains("notes/dm-"));

        var carolIds = Ids(await search.SearchAsync("content", requesterIri: Carol));
        Assert.Contains(carolIds, id => id.Contains("notes/pub-"));
        Assert.DoesNotContain(carolIds, id => id.Contains("notes/fol-"));
        Assert.Contains(carolIds, id => id.Contains("notes/dm-"));
    }

    [Fact]
    public async Task Search_Total_ReflectsOnlyVisibleContent()
    {
        var p = await SeedAsync();
        var search = new GlobalSearchService(p);

        // Anonymous: only the public note is visible content (3 actors are always visible in the
        // directory pass, but the query "content" matches only content objects, not actor names).
        var (anonItems, anonTotal) = await search.SearchPagedAsync("content", default, null, int.MaxValue, 0, requesterIri: null);
        Assert.Equal(1, anonTotal);
        Assert.Single(anonItems);

        // bob: public + followers-only = 2 visible content items.
        var (bobItems, bobTotal) = await search.SearchPagedAsync("content", default, null, int.MaxValue, 0, requesterIri: Bob);
        Assert.Equal(2, bobTotal);
        Assert.Equal(2, bobItems.Count);
    }

    [Fact]
    public async Task Search_Actors_UnaffectedByVisibility()
    {
        var p = await SeedAsync();
        var search = new GlobalSearchService(p);
        // An empty query matches all actors + content. For an anonymous requester, the actor directory
        // is fully visible (a person is not gated by a post's audience) but only the public note shows.
        var (items, _) = await search.SearchPagedAsync(null, default, null, int.MaxValue, 0, requesterIri: null);
        // alice is the only seeded actor; the DM/followers-only notes are hidden from anonymous.
        Assert.Contains(items, i => i is IObject { Id: var id } && id == Alice.Value);
        Assert.DoesNotContain(items, i => i is IObject { Id: { } id } && id.Contains("notes/dm-"));
    }
}
