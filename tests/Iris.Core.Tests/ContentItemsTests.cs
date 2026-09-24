using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Tests;

/// <summary>
/// S81: the single shared content-item classification. Every posts/feed view (the Blazor profile and
/// actor "Posts" tabs, the home feed, the server's <c>?type=content</c> outbox filter, and the
/// per-actor <c>postsCount</c> counter) now funnels through <see cref="ContentItems.IsContentPost"/>,
/// so the set of content types cannot drift between them. These tests pin that set: a content post is
/// a <c>Create</c> of a Note/Article/Page/Question, an <c>Announce</c>, or a bare content object.
/// </summary>
public class ContentItemsTests
{
    private static Create CreateOf(params IObjectOrLink[] objects)
        => new() { Object = objects };

    [Theory]
    [InlineData("note")]
    [InlineData("article")]
    [InlineData("page")]
    [InlineData("question")]
    public void CreateOfContentObject_IsContentPost(string kind)
    {
        var create = CreateOf(ContentObject(kind));

        Assert.True(ContentItems.IsContentPost(create));
    }

    [Fact]
    public void Announce_IsContentPost()
    {
        var announce = new Announce { Object = [new Link { Href = new Uri("https://a.test/n/1") }] };

        Assert.True(ContentItems.IsContentPost(announce));
    }

    [Fact]
    public void BareContentObject_IsContentPost()
    {
        // Some servers publish the object directly rather than wrapped in a Create; the home feed
        // always accepted this shape, so the shared predicate must too.
        Assert.True(ContentItems.IsContentPost(new Page { Content = ["<p>cross-post</p>"] }));
        Assert.True(ContentItems.IsContentPost(new Question { Content = ["poll?"] }));
    }

    [Fact]
    public void CreateOfNonContentObject_IsNotContentPost()
    {
        // A community join is a Create of a Group — not content. A bare link is not content.
        Assert.False(ContentItems.IsContentPost(CreateOf(new Group { Id = "https://a.test/c/community" })));
        Assert.False(ContentItems.IsContentPost(CreateOf(new Link { Href = new Uri("https://a.test/n/1") })));
    }

    [Fact]
    public void SocialActivities_AreNotContentPosts()
    {
        Assert.False(ContentItems.IsContentPost(new Follow { Actor = [new Link { Href = new Uri("https://a.test/u/a") }], Object = [new Link { Href = new Uri("https://a.test/u/b") }] }));
        Assert.False(ContentItems.IsContentPost(new Like { Actor = [new Link { Href = new Uri("https://a.test/u/a") }], Object = [new Link { Href = new Uri("https://a.test/n/1") }] }));
    }

    private static IObjectOrLink ContentObject(string kind) => kind switch
    {
        "note" => new Note { Content = ["hello"] },
        "article" => new Article { Content = ["<p>article</p>"] },
        "page" => new Page { Content = ["<p>cross-post</p>"] },
        "question" => new Question { Content = ["poll?"] },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
