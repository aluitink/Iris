using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for <see cref="IriExtensions"/> inbox/outbox derivation and boundary conversions.
/// </summary>
public class IriExtensionsTests
{
    [Fact]
    public void InboxOf_AppendsInbox()
    {
        var inbox = new Iri("https://a.domain.local/u/alice").InboxOf();

        Assert.Equal("https://a.domain.local/u/alice/inbox", inbox.Value);
    }

    [Fact]
    public void OutboxOf_AppendsOutbox()
    {
        var outbox = new Iri("https://a.domain.local/u/alice").OutboxOf();

        Assert.Equal("https://a.domain.local/u/alice/outbox", outbox.Value);
    }

    [Fact]
    public void FollowersOf_AppendsFollowers()
    {
        var followers = new Iri("https://a.domain.local/u/alice").FollowersOf();

        Assert.Equal("https://a.domain.local/u/alice/followers", followers.Value);
    }

    [Fact]
    public void FollowingOf_AppendsFollowing()
    {
        var following = new Iri("https://a.domain.local/u/alice").FollowingOf();

        Assert.Equal("https://a.domain.local/u/alice/following", following.Value);
    }

    [Fact]
    public void InboxOf_TrailingSlashIsNotDuplicated()
    {
        var inbox = new Iri("https://a.domain.local/u/alice/").InboxOf();

        Assert.Equal("https://a.domain.local/u/alice/inbox", inbox.Value);
    }

    [Fact]
    public void InboxOf_RelativeIri_Throws()
    {
        var relative = new Iri("/u/alice");

        Assert.Throws<ArgumentException>(() => relative.InboxOf());
    }

    [Fact]
    public void ToIri_FromString_Converts()
    {
        Iri? iri = "https://a.domain.local/n/1".ToIri();

        Assert.Equal(new Iri("https://a.domain.local/n/1"), iri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToIri_FromNullOrBlankString_ReturnsNull(string? value)
    {
        Assert.Null(value.ToIri());
    }

    [Fact]
    public void ToIri_FromUri_Converts()
    {
        Iri? iri = new Uri("https://b.domain.local/u/bob").ToIri();

        Assert.Equal(new Iri("https://b.domain.local/u/bob"), iri);
    }

    [Fact]
    public void ToIri_FromNullUri_ReturnsNull()
    {
        Uri? nullUri = null;
        Assert.Null(nullUri.ToIri());
    }

    [Fact]
    public void ToLibraryId_RoundTrips()
    {
        Iri iri = new("https://a.domain.local/n/1");
        Iri? nullableIri = iri;

        Assert.Equal("https://a.domain.local/n/1", nullableIri.ToLibraryId());
        Assert.Null(default(Iri?).ToLibraryId());
    }

    [Fact]
    public void ToLinkHref_RoundTrips()
    {
        Iri iri = new("https://a.domain.local/n/1");
        Iri? nullableIri = iri;

        Assert.Equal(new Uri("https://a.domain.local/n/1"), nullableIri.ToLinkHref());
        Assert.Null(default(Iri?).ToLinkHref());
    }

    [Fact]
    public void ResolveObjectIri_FromLink_ReturnsHref()
    {
        IObjectOrLink link = new Link { Href = new Uri("https://a.domain.local/u/alice") };

        Assert.Equal(new Iri("https://a.domain.local/u/alice"), link.ResolveObjectIri());
    }

    [Fact]
    public void ResolveObjectIri_FromEmbeddedObject_ReturnsId()
    {
        IObjectOrLink person = new Person { Id = "https://a.domain.local/u/alice" };

        Assert.Equal(new Iri("https://a.domain.local/u/alice"), person.ResolveObjectIri());
    }

    [Fact]
    public void ResolveObjectIri_FromLinkWithoutHref_ReturnsNull()
    {
        IObjectOrLink link = new Link();

        Assert.Null(link.ResolveObjectIri());
    }

    [Fact]
    public void ResolveObjectIri_FromObjectWithoutId_ReturnsNull()
    {
        IObjectOrLink person = new Person { Name = ["No Id"] };

        Assert.Null(person.ResolveObjectIri());
    }

    [Fact]
    public void ResolveObjectIri_FromNull_ReturnsNull()
    {
        IObjectOrLink? none = null;

        Assert.Null(none.ResolveObjectIri());
    }

    [Fact]
    public void ResolveCollectionIri_FromLink_ReturnsHref()
    {
        ICollectionOrLink link = new Link { Href = new Uri("https://a.domain.local/pages/2") };

        var iri = link.ResolveCollectionIri();

        Assert.NotNull(iri);
        Assert.Equal("https://a.domain.local/pages/2", iri!.Value.Value);
    }

    [Fact]
    public void ResolveCollectionIri_FromObjectWithId_ReturnsId()
    {
        ICollectionOrLink collection = new OrderedCollection { Id = "https://a.domain.local/col" };

        var iri = collection.ResolveCollectionIri();

        Assert.NotNull(iri);
        Assert.Equal("https://a.domain.local/col", iri!.Value.Value);
    }

    [Fact]
    public void ResolveCollectionIri_FromLinkWithoutHref_ReturnsNull()
    {
        ICollectionOrLink link = new Link();

        Assert.Null(link.ResolveCollectionIri());
    }

    [Fact]
    public void ResolveCollectionIri_FromObjectWithoutId_ReturnsNull()
    {
        ICollectionOrLink collection = new OrderedCollection { Name = ["No Id"] };

        Assert.Null(collection.ResolveCollectionIri());
    }

    [Fact]
    public void ResolveCollectionIri_FromNull_ReturnsNull()
    {
        ICollectionOrLink? none = null;

        Assert.Null(none.ResolveCollectionIri());
    }

    [Fact]
    public void RepliesOf_AppendsReplies()
    {
        var replies = new Iri("https://a.domain.local/ap/v1/u/alice/notes/n1").RepliesOf();

        Assert.Equal("https://a.domain.local/ap/v1/u/alice/notes/n1/replies", replies.Value);
    }

    [Fact]
    public void RepliesOf_TrailingSlashIsNotDuplicated()
    {
        var replies = new Iri("https://a.domain.local/ap/v1/u/alice/notes/n1/").RepliesOf();

        Assert.Equal("https://a.domain.local/ap/v1/u/alice/notes/n1/replies", replies.Value);
    }

    [Fact]
    public void RepliesOf_RelativeIri_Throws()
    {
        var relative = new Iri("/u/alice/notes/n1");

        Assert.Throws<ArgumentException>(() => relative.RepliesOf());
    }

    [Fact]
    public void GetParentIri_FromLink_ReturnsParentIri()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/bob/notes/r1",
            InReplyTo = [new Link { Href = new Uri("https://a.domain.local/ap/v1/u/alice/notes/n1") }],
        };

        Assert.Equal(new Iri("https://a.domain.local/ap/v1/u/alice/notes/n1"), note.GetParentIri());
    }

    [Fact]
    public void GetParentIri_FromEmbeddedParentObject_ReturnsParentId()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/bob/notes/r1",
            InReplyTo = [new Note { Id = "https://a.domain.local/ap/v1/u/alice/notes/n1" }],
        };

        Assert.Equal(new Iri("https://a.domain.local/ap/v1/u/alice/notes/n1"), note.GetParentIri());
    }

    [Fact]
    public void GetParentIri_ToplevelNote_ReturnsNull()
    {
        IObject note = new Note { Id = "https://a.domain.local/ap/v1/u/alice/notes/n1" };

        Assert.Null(note.GetParentIri());
    }

    [Fact]
    public void GetParentIri_Null_ReturnsNull()
    {
        IObject? none = null;

        Assert.Null(none.GetParentIri());
    }

    [Fact]
    public void GetMentionIris_ExtractsMentionHrefs()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/bob/notes/r1",
            Tag =
            [
                new Mention { Href = new Uri("https://b.domain.local/ap/v1/u/carol") },
                new Mention { Href = new Uri("https://c.domain.local/ap/v1/u/dave") },
            ],
        };

        Assert.Equal(
            [new Iri("https://b.domain.local/ap/v1/u/carol"), new Iri("https://c.domain.local/ap/v1/u/dave")],
            note.GetMentionIris());
    }

    [Fact]
    public void GetMentionIris_IgnoresNonMentionTags()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/bob/notes/r1",
            Tag =
            [
                new Link { Href = new Uri("https://example.com/tags/hashtag") },
                new Mention { Href = new Uri("https://b.domain.local/ap/v1/u/carol") },
            ],
        };

        Assert.Equal([new Iri("https://b.domain.local/ap/v1/u/carol")], note.GetMentionIris());
    }

    [Fact]
    public void GetMentionIris_NoTags_ReturnsEmpty()
    {
        IObject note = new Note { Id = "https://a.domain.local/ap/v1/u/alice/notes/n1" };

        Assert.Empty(note.GetMentionIris());
    }

    [Fact]
    public void GetMentionIris_Null_ReturnsEmpty()
    {
        IObject? none = null;

        Assert.Empty(none.GetMentionIris());
    }

    [Fact]
    public void GetAttachmentIris_FromImageWithId_ReturnsId()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Attachment = [new Image { Id = "https://cdn.example.com/media/1.jpg" }],
        };

        Assert.Equal([new Iri("https://cdn.example.com/media/1.jpg")], note.GetAttachmentIris());
    }

    [Fact]
    public void GetAttachmentIris_FromLink_ReturnsHref()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Attachment = [new Link { Href = new Uri("https://cdn.example.com/media/2.jpg") }],
        };

        Assert.Equal([new Iri("https://cdn.example.com/media/2.jpg")], note.GetAttachmentIris());
    }

    [Fact]
    public void GetAttachmentIris_FromImageWithoutId_FallsBackToUrl()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Attachment = [new Image { Url = [new Link { Href = new Uri("https://cdn.example.com/media/3.jpg") }] }],
        };

        Assert.Equal([new Iri("https://cdn.example.com/media/3.jpg")], note.GetAttachmentIris());
    }

    [Fact]
    public void GetAttachmentIris_NoAttachments_ReturnsEmpty()
    {
        IObject note = new Note { Id = "https://a.domain.local/ap/v1/u/alice/notes/n1" };

        Assert.Empty(note.GetAttachmentIris());
    }

    [Fact]
    public void GetAttachmentIris_Null_ReturnsEmpty()
    {
        IObject? none = null;

        Assert.Empty(none.GetAttachmentIris());
    }
}
