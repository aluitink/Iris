using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using Xunit;

namespace Iris.Core.Tests.Identity;

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

    [Fact]
    public void GetAudienceIris_ReadsToThenCc()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            To = [new Link { Href = new Uri("https://a.domain.local/ap/v1/u/bob") }],
            Cc = [new Link { Href = new Uri("https://a.domain.local/ap/v1/c/iris") }],
        };

        Assert.Equal(
            [new Iri("https://a.domain.local/ap/v1/u/bob"), new Iri("https://a.domain.local/ap/v1/c/iris")],
            note.GetAudienceIris());
    }

    [Fact]
    public void GetAudienceIris_ExcludesThePublicSentinel()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
            Cc = [new Link { Href = new Uri("https://a.domain.local/ap/v1/u/bob") }],
        };

        Assert.Equal([new Iri("https://a.domain.local/ap/v1/u/bob")], note.GetAudienceIris());
    }

    [Fact]
    public void GetAudienceIris_DeduplicatesRepeatedAudiences()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            // bob appears in both `to` and `cc` (a common real-world shape) — it must appear once.
            To = [new Link { Href = new Uri("https://a.domain.local/ap/v1/u/bob") }],
            Cc = [new Link { Href = new Uri("https://a.domain.local/ap/v1/u/bob") }],
        };

        Assert.Equal([new Iri("https://a.domain.local/ap/v1/u/bob")], note.GetAudienceIris());
    }

    [Fact]
    public void GetAudienceIris_FromEmbeddedAudienceObject_ReturnsId()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            To = [new Person { Id = "https://a.domain.local/ap/v1/u/bob" }],
        };

        Assert.Equal([new Iri("https://a.domain.local/ap/v1/u/bob")], note.GetAudienceIris());
    }

    [Fact]
    public void GetAudienceIris_NoAudience_ReturnsEmpty()
    {
        IObject note = new Note { Id = "https://a.domain.local/ap/v1/u/alice/notes/n1" };

        Assert.Empty(note.GetAudienceIris());
    }

    [Fact]
    public void GetAudienceIris_Null_ReturnsEmpty()
    {
        IObject? none = null;

        Assert.Empty(none.GetAudienceIris());
    }

    [Fact]
    public void GetAudienceIris_OnlyPublicSentinel_ReturnsEmpty()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            To = [new Link { Href = new Uri("as:Public") }],
        };

        Assert.Empty(note.GetAudienceIris());
    }

    [Theory]
    [InlineData("as:Public", true)]
    [InlineData("https://www.w3.org/ns/activitystreams#Public", true)]
    [InlineData("http://www.w3.org/ns/activitystreams#Public", true)]
    [InlineData("https://a.domain.local/ap/v1/u/bob", false)]
    public void IsPublicAudience_DetectsTheWellKnownPublicIri(string value, bool expected)
    {
        Assert.Equal(expected, new Iri(value).IsPublicAudience());
    }

    [Fact]
    public void IsSensitive_SensitiveTrue_ReturnsTrue()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Content = ["<p>secret</p>"],
        };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["sensitive"] = JsonDocument.Parse("true").RootElement.Clone(),
        };

        Assert.True(note.IsSensitive());
    }

    [Fact]
    public void IsSensitive_SensitiveFalse_ReturnsFalse()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
        };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["sensitive"] = JsonDocument.Parse("false").RootElement.Clone(),
        };

        Assert.False(note.IsSensitive());
    }

    [Fact]
    public void IsSensitive_NoSensitiveTerm_ReturnsFalse()
    {
        IObject note = new Note { Id = "https://a.domain.local/ap/v1/u/alice/notes/n1" };

        Assert.False(note.IsSensitive());
    }

    [Fact]
    public void IsSensitive_NonBooleanSensitiveTerm_ReturnsFalse()
    {
        IObject note = new Note { Id = "https://a.domain.local/ap/v1/u/alice/notes/n1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["sensitive"] = JsonDocument.Parse("\"maybe\"").RootElement.Clone(),
        };

        Assert.False(note.IsSensitive());
    }

    [Fact]
    public void IsSensitive_Null_ReturnsFalse()
    {
        IObject? none = null;

        Assert.False(none.IsSensitive());
    }

    [Fact]
    public void GetSummary_SingleSummary_ReturnsIt()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Summary = ["A secret photo"],
        };

        Assert.Equal("A secret photo", note.GetSummary());
    }

    [Fact]
    public void GetSummary_MultipleSummaries_JoinsWithSpace()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Summary = ["Part one", "Part two"],
        };

        Assert.Equal("Part one Part two", note.GetSummary());
    }

    [Fact]
    public void GetSummary_NoSummary_ReturnsNull()
    {
        IObject note = new Note { Id = "https://a.domain.local/ap/v1/u/alice/notes/n1" };

        Assert.Null(note.GetSummary());
    }

    [Fact]
    public void GetSummary_OnlyBlankSummaries_ReturnsNull()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Summary = ["", "   "],
        };

        Assert.Null(note.GetSummary());
    }

    [Fact]
    public void GetSummary_Null_ReturnsNull()
    {
        IObject? none = null;

        Assert.Null(none.GetSummary());
    }

    [Fact]
    public void GetUpdated_WithUpdated_ReturnsIt()
    {
        var updated = new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc);
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Updated = updated,
        };

        Assert.Equal(updated, note.GetUpdated());
    }

    [Fact]
    public void GetUpdated_NoUpdated_ReturnsNull()
    {
        IObject note = new Note { Id = "https://a.domain.local/ap/v1/u/alice/notes/n1" };

        Assert.Null(note.GetUpdated());
    }

    [Fact]
    public void GetUpdated_Null_ReturnsNull()
    {
        IObject? none = null;

        Assert.Null(none.GetUpdated());
    }

    [Theory]
    [InlineData("<p>Hello</p>")]
    [InlineData("<h1>Title</h1>")]
    [InlineData("<ul><li>a</li></ul>")]
    [InlineData("  <p>leading whitespace</p>")]
    [InlineData("<P>uppercase</P>")]
    [InlineData("<pre><code>x</code></pre>")]
    [InlineData("<blockquote>quote</blockquote>")]
    public void IsPreRenderedHtmlContent_BlockHtml_ReturnsTrue(string content)
    {
        IObject note = new Note { Id = "https://a.domain.local/n/1", Content = [content] };

        Assert.True(note.IsPreRenderedHtmlContent());
    }

    [Theory]
    [InlineData("# A heading")]
    [InlineData("**bold** and *italic*")]
    [InlineData("plain text, no markup")]
    [InlineData("- a list item")]
    [InlineData("[a link](https://example.com)")]
    public void IsPreRenderedHtmlContent_MarkdownOrPlain_ReturnsFalse(string content)
    {
        IObject note = new Note { Id = "https://a.domain.local/n/1", Content = [content] };

        Assert.False(note.IsPreRenderedHtmlContent());
    }

    [Fact]
    public void IsPreRenderedHtmlContent_NoContent_ReturnsFalse()
    {
        IObject note = new Note { Id = "https://a.domain.local/n/1" };

        Assert.False(note.IsPreRenderedHtmlContent());
    }

    [Fact]
    public void IsPreRenderedHtmlContent_Null_ReturnsFalse()
    {
        IObject? none = null;

        Assert.False(none.IsPreRenderedHtmlContent());
    }

    [Fact]
    public void IsPreRenderedHtmlContent_FirstNonEmptyValue_Decides()
    {
        // The first non-empty content value is the one inspected (a blank leading value is skipped).
        IObject note = new Note { Id = "https://a.domain.local/n/1", Content = ["", "<p>real content</p>"] };

        Assert.True(note.IsPreRenderedHtmlContent());
    }
}
