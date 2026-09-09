using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using Xunit;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

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

    // --- GetHashtagTags (54.14) ---

    [Fact]
    public void GetHashtagTags_NullObject_ReturnsEmpty()
    {
        IObject? obj = null;

        var result = obj.GetHashtagTags();

        Assert.Empty(result);
    }

    [Fact]
    public void GetHashtagTags_NoTag_ReturnsEmpty()
    {
        var note = new ActivityObject();

        var result = note.GetHashtagTags();

        Assert.Empty(result);
    }

    [Fact]
    public void GetHashtagTags_HashtagWithHref_ReturnsNameAndHref()
    {
        // A Hashtag tag as a foreign server emits it: a generic object of type Hashtag with name +
        // href (the href lands in ExtensionData because the Object base does not model href).
        var note = new ActivityObject
        {
            Tag = new IObjectOrLink[]
            {
                new ActivityObject
                {
                    Id = "https://other.example/notes/1#tag=0",
                    Type = ["Hashtag"],
                    Name = ["#hello"],
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["href"] = JsonSerializer.SerializeToElement("https://other.example/tags/hello"),
                    }
                }
            }
        };

        var result = note.GetHashtagTags();

        var (name, href) = Assert.Single(result);
        Assert.Equal("#hello", name);
        Assert.NotNull(href);
        Assert.Equal(new Iri("https://other.example/tags/hello"), href!.Value);
    }

    [Fact]
    public void GetHashtagTags_HashtagWithoutHref_ReturnsNameWithNullHref()
    {
        // A server that emits a Hashtag with only a name (no href) — still surfaced, just unlinked.
        var note = new ActivityObject
        {
            Tag = new IObjectOrLink[]
            {
                new ActivityObject
                {
                    Type = ["Hashtag"],
                    Name = ["#justname"]
                }
            }
        };

        var result = note.GetHashtagTags();

        var (name, href) = Assert.Single(result);
        Assert.Equal("#justname", name);
        Assert.Null(href);
    }

    [Fact]
    public void GetHashtagTags_MixedMentionAndHashtag_ReturnsOnlyHashtags()
    {
        // The mention reader and hashtag reader are independent boundaries over the same `tag` array:
        // GetHashtagTags returns only the Hashtag entries, not the Mention entries.
        var note = new ActivityObject
        {
            Tag = new IObjectOrLink[]
            {
                new Mention { Href = new Uri("https://a.domain.local/u/bob") },
                new ActivityObject
                {
                    Type = ["Hashtag"],
                    Name = ["#greetings"],
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["href"] = JsonSerializer.SerializeToElement("https://a.domain.local/search?q=%23greetings"),
                    }
                }
            }
        };

        var result = note.GetHashtagTags();

        var (name, href) = Assert.Single(result);
        Assert.Equal("#greetings", name);
        Assert.NotNull(href);
        Assert.Equal(new Iri("https://a.domain.local/search?q=%23greetings"), href!.Value);
    }

    [Fact]
    public void GetHashtagTags_CaseInsensitiveType_Matches()
    {
        // A foreign server that capitalizes the type term differently is still matched (lenient).
        var note = new ActivityObject
        {
            Tag = new IObjectOrLink[]
            {
                new ActivityObject
                {
                    Type = ["HASHTAG"],
                    Name = ["#loud"]
                }
            }
        };

        var result = note.GetHashtagTags();

        var (name, _) = Assert.Single(result);
        Assert.Equal("#loud", name);
    }

    [Fact]
    public void GetHashtagTags_UnparseableHref_IsTreatedAsAbsent()
    {
        // An href that fails Iri.TryParse (e.g. an unescaped space in the authority — the one class of
        // input Uri.TryCreate rejects beyond blank/empty) is ignored: the tag is surfaced without a link.
        var note = new ActivityObject
        {
            Tag = new IObjectOrLink[]
            {
                new ActivityObject
                {
                    Type = ["Hashtag"],
                    Name = ["#badhref"],
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["href"] = JsonSerializer.SerializeToElement("http://x y/z"),
                    }
                }
            }
        };

        var result = note.GetHashtagTags();

        var (name, href) = Assert.Single(result);
        Assert.Equal("#badhref", name);
        Assert.Null(href);
    }

    [Fact]
    public void GetHashtagTags_SkipsHashtagWithoutName()
    {
        // A Hashtag-type tag with no name (malformed) is skipped.
        var note = new ActivityObject
        {
            Tag = new IObjectOrLink[]
            {
                new ActivityObject { Type = ["Hashtag"] }
            }
        };

        var result = note.GetHashtagTags();

        Assert.Empty(result);
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

    // --- GetCustomEmojis (58.1) ---

    [Fact]
    public void GetCustomEmojis_NullObject_ReturnsEmpty()
    {
        IObject? obj = null;

        Assert.Empty(obj.GetCustomEmojis());
    }

    [Fact]
    public void GetCustomEmojis_NoEmojiProperty_ReturnsEmpty()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };

        Assert.Empty(note.GetCustomEmojis());
    }

    [Fact]
    public void GetCustomEmojis_EmptyEmojiArray_ReturnsEmpty()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["emoji"] = JsonDocument.Parse("[]").RootElement.Clone(),
        };

        Assert.Empty(note.GetCustomEmojis());
    }

    [Fact]
    public void GetCustomEmojis_SingleEmoji_ReturnsNameShortCodeAndUrl()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["emoji"] = JsonDocument.Parse("""
                [{"name":"smile","shortCode":":smile:","staticUrl":"https://cdn.example.com/emoji/smile.png","url":"https://cdn.example.com/emoji/smile.png"}]
                """).RootElement.Clone(),
        };

        var result = note.GetCustomEmojis();

        Assert.Single(result);
        Assert.Equal("smile", result[0].Name);
        Assert.Equal(":smile:", result[0].ShortCode);
        Assert.Equal(new Iri("https://cdn.example.com/emoji/smile.png"), result[0].Url);
    }

    [Fact]
    public void GetCustomEmojis_MultipleEmojis_ReturnsAllInOrder()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["emoji"] = JsonDocument.Parse("""
                [
                    {"name":"cat","shortCode":":cat:","staticUrl":"https://cdn.example.com/emoji/cat.png"},
                    {"name":"dog","shortCode":":dog:","staticUrl":"https://cdn.example.com/emoji/dog.png"}
                ]
                """).RootElement.Clone(),
        };

        var result = note.GetCustomEmojis();

        Assert.Equal(2, result.Count);
        Assert.Equal("cat", result[0].Name);
        Assert.Equal(":cat:", result[0].ShortCode);
        Assert.Equal("dog", result[1].Name);
        Assert.Equal(":dog:", result[1].ShortCode);
    }

    [Fact]
    public void GetCustomEmojis_NoShortCode_DerivesFromName()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["emoji"] = JsonDocument.Parse("""
                [{"name":"party","staticUrl":"https://cdn.example.com/emoji/party.png"}]
                """).RootElement.Clone(),
        };

        var result = note.GetCustomEmojis();

        Assert.Single(result);
        Assert.Equal(":party:", result[0].ShortCode);
    }

    [Fact]
    public void GetCustomEmojis_NoStaticUrl_FallsBackToUrl()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["emoji"] = JsonDocument.Parse("""
                [{"name":"wave","shortCode":":wave:","url":"https://cdn.example.com/emoji/wave.png"}]
                """).RootElement.Clone(),
        };

        var result = note.GetCustomEmojis();

        Assert.Single(result);
        Assert.Equal(new Iri("https://cdn.example.com/emoji/wave.png"), result[0].Url);
    }

    [Fact]
    public void GetCustomEmojis_NoUrls_ReturnsNullUrl()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["emoji"] = JsonDocument.Parse("""
                [{"name":"mystery","shortCode":":mystery:"}]
                """).RootElement.Clone(),
        };

        var result = note.GetCustomEmojis();

        Assert.Single(result);
        Assert.Null(result[0].Url);
    }

    [Fact]
    public void GetCustomEmojis_SkipsEmojiWithoutName()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["emoji"] = JsonDocument.Parse("""
                [{"shortCode":":unnamed:","staticUrl":"https://cdn.example.com/emoji/x.png"}]
                """).RootElement.Clone(),
        };

        Assert.Empty(note.GetCustomEmojis());
    }

    [Fact]
    public void GetCustomEmojis_NonArrayEmojiProperty_ReturnsEmpty()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["emoji"] = JsonDocument.Parse("\"not-an-array\"").RootElement.Clone(),
        };

        Assert.Empty(note.GetCustomEmojis());
    }

    [Fact]
    public void GetCustomEmojis_SkipsNonObjectEntries()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["emoji"] = JsonDocument.Parse("""
                ["not-an-object", {"name":"valid","shortCode":":valid:","staticUrl":"https://cdn.example.com/v.png"}]
                """).RootElement.Clone(),
        };

        var result = note.GetCustomEmojis();

        Assert.Single(result);
        Assert.Equal("valid", result[0].Name);
    }

    [Fact]
    public void GetCustomEmojis_RoundTripsThroughJsonSerialization()
    {
        var note = new Note
        {
            Id = "https://a.domain.local/n/1",
            Content = ["Hello :smile: world"],
        };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["emoji"] = JsonDocument.Parse("""
                [{"name":"smile","shortCode":":smile:","staticUrl":"https://cdn.example.com/emoji/smile.png"}]
                """).RootElement.Clone(),
        };

        var json = ActivityJson.Serialize(note);
        var roundTripped = ActivityJson.Deserialize<IObjectOrLink>(json);
        var roundTrippedNote = Assert.IsAssignableFrom<IObject>(roundTripped);

        var result = roundTrippedNote.GetCustomEmojis();

        Assert.Single(result);
        Assert.Equal("smile", result[0].Name);
        Assert.Equal(":smile:", result[0].ShortCode);
        Assert.Equal(new Iri("https://cdn.example.com/emoji/smile.png"), result[0].Url);
    }

    // --- GetPollData (58.2) ---

    [Fact]
    public void GetPollData_NullObject_ReturnsNull()
    {
        IObject? obj = null;

        Assert.Null(obj.GetPollData());
    }

    [Fact]
    public void GetPollData_NoPoll_ReturnsNull()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };

        Assert.Null(note.GetPollData());
    }

    [Fact]
    public void GetPollData_MastodonPoll_ParsesOptionsVotesEndsAt()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["poll"] = JsonDocument.Parse("""
                {
                    "id": "poll-1",
                    "options": [
                        {"title": "Alice", "votesCount": 3},
                        {"title": "Bob", "votesCount": 4},
                        {"title": "Charlie", "votesCount": 5}
                    ],
                    "endsAt": "2026-12-01T00:00:00Z",
                    "expired": false,
                    "multiple": false,
                    "totalVotes": 12
                }
                """).RootElement.Clone(),
        };

        var poll = note.GetPollData();

        Assert.NotNull(poll);
        Assert.Equal(3, poll!.Options.Count);
        Assert.Equal("Alice", poll.Options[0].Title);
        Assert.Equal(3, poll.Options[0].Votes);
        Assert.Equal("Charlie", poll.Options[2].Title);
        Assert.Equal(5, poll.Options[2].Votes);
        Assert.Equal(12, poll.TotalVotes);
        Assert.False(poll.Expired);
        Assert.False(poll.Multiple);
        Assert.Equal(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), poll.EndsAt);
    }

    [Fact]
    public void GetPollData_MastodonPoll_ExpiredAndMultiple()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["poll"] = JsonDocument.Parse("""
                {
                    "options": [
                        {"title": "Yes", "votesCount": 7},
                        {"title": "No", "votesCount": 3}
                    ],
                    "endsAt": "2026-01-01T00:00:00Z",
                    "expired": true,
                    "multiple": true,
                    "totalVotes": 10
                }
                """).RootElement.Clone(),
        };

        var poll = note.GetPollData();

        Assert.NotNull(poll);
        Assert.True(poll!.Expired);
        Assert.True(poll.Multiple);
        Assert.Equal(10, poll.TotalVotes);
        Assert.Equal(2, poll.Options.Count);
    }

    [Fact]
    public void GetPollData_MastodonPoll_NoTotalVotes_SumsOptions()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["poll"] = JsonDocument.Parse("""
                {
                    "options": [
                        {"title": "A", "votesCount": 2},
                        {"title": "B", "votesCount": 3}
                    ],
                    "expired": false,
                    "multiple": false
                }
                """).RootElement.Clone(),
        };

        var poll = note.GetPollData();

        Assert.NotNull(poll);
        Assert.Equal(5, poll!.TotalVotes);
    }

    [Fact]
    public void GetPollData_As2Question_ParsesOptionsWithNamesAndVotes()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["options"] = JsonDocument.Parse("""
                [
                    {"name": "Option A", "votes": 5},
                    {"name": "Option B", "votes": 3}
                ]
                """).RootElement.Clone(),
            ["endTime"] = JsonDocument.Parse("\"2026-11-15T12:00:00Z\"").RootElement.Clone(),
            ["closed"] = JsonDocument.Parse("false").RootElement.Clone(),
            ["multiple"] = JsonDocument.Parse("false").RootElement.Clone(),
        };

        var poll = note.GetPollData();

        Assert.NotNull(poll);
        Assert.Equal(2, poll!.Options.Count);
        Assert.Equal("Option A", poll.Options[0].Title);
        Assert.Equal(5, poll.Options[0].Votes);
        Assert.Equal("Option B", poll.Options[1].Title);
        Assert.Equal(3, poll.Options[1].Votes);
        Assert.Equal(8, poll.TotalVotes);
        Assert.False(poll.Expired);
        Assert.Equal(new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc), poll.EndsAt);
    }

    [Fact]
    public void GetPollData_As2Question_Closed_ParsesExpired()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["options"] = JsonDocument.Parse("""
                [
                    {"name": "X", "votes": 1},
                    {"name": "Y", "votes": 2}
                ]
                """).RootElement.Clone(),
            ["closed"] = JsonDocument.Parse("true").RootElement.Clone(),
        };

        var poll = note.GetPollData();

        Assert.NotNull(poll);
        Assert.True(poll!.Expired);
    }

    [Fact]
    public void GetPollData_As2Question_NoEndTime_EndsAtIsNull()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["options"] = JsonDocument.Parse("""
                [
                    {"name": "A", "votes": 1}
                ]
                """).RootElement.Clone(),
        };

        var poll = note.GetPollData();

        Assert.NotNull(poll);
        Assert.Null(poll!.EndsAt);
    }

    [Fact]
    public void GetPollData_MastodonPoll_SkipsOptionWithoutTitle()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["poll"] = JsonDocument.Parse("""
                {
                    "options": [
                        {"votesCount": 1},
                        {"title": "Valid", "votesCount": 2}
                    ],
                    "expired": false,
                    "multiple": false
                }
                """).RootElement.Clone(),
        };

        var poll = note.GetPollData();

        Assert.NotNull(poll);
        Assert.Single(poll!.Options);
        Assert.Equal("Valid", poll.Options[0].Title);
    }

    [Fact]
    public void GetPollData_MastodonPoll_EmptyOptions_ReturnsNull()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["poll"] = JsonDocument.Parse("""
                {"options": [], "expired": false, "multiple": false}
                """).RootElement.Clone(),
        };

        Assert.Null(note.GetPollData());
    }

    [Fact]
    public void GetPollData_MastodonPoll_NonObjectPoll_ReturnsNull()
    {
        var note = new Note { Id = "https://a.domain.local/n/1" };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["poll"] = JsonDocument.Parse("\"not-an-object\"").RootElement.Clone(),
        };

        Assert.Null(note.GetPollData());
    }

    [Fact]
    public void GetPollData_RoundTripsThroughJsonSerialization()
    {
        var note = new Note
        {
            Id = "https://a.domain.local/n/1",
            Content = ["Who wins?"],
        };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["poll"] = JsonDocument.Parse("""
                {
                    "id": "poll-1",
                    "options": [
                        {"title": "Alice", "votesCount": 3},
                        {"title": "Bob", "votesCount": 7}
                    ],
                    "endsAt": "2026-12-01T00:00:00Z",
                    "expired": false,
                    "multiple": false,
                    "totalVotes": 10
                }
                """).RootElement.Clone(),
        };

        var json = ActivityJson.Serialize(note);
        var roundTripped = ActivityJson.Deserialize<IObjectOrLink>(json);
        var roundTrippedNote = Assert.IsAssignableFrom<IObject>(roundTripped);

        var poll = roundTrippedNote.GetPollData();

        Assert.NotNull(poll);
        Assert.Equal(2, poll!.Options.Count);
        Assert.Equal("Alice", poll.Options[0].Title);
        Assert.Equal(3, poll.Options[0].Votes);
        Assert.Equal(10, poll.TotalVotes);
    }

    [Fact]
    public void GetRichAttachments_FromImage_ReturnsImageType()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Attachment = [new Image { Id = "https://cdn.example.com/media/1.jpg", Name = ["photo.jpg"] }],
        };

        var attachments = note.GetRichAttachments();
        Assert.Single(attachments);
        Assert.Equal("Image", attachments[0].Type);
        Assert.Equal("photo.jpg", attachments[0].Name);
        Assert.Equal(new Iri("https://cdn.example.com/media/1.jpg"), attachments[0].Url);
    }

    [Fact]
    public void GetRichAttachments_FromDocument_ReturnsDocumentType()
    {
        var json = """
        {
            "id": "https://a.domain.local/ap/v1/u/alice/notes/n1",
            "type": "Note",
            "attachment": [
                {
                    "type": "Document",
                    "name": "report.pdf",
                    "url": "https://cdn.example.com/files/report.pdf"
                }
            ]
        }
        """;

        var note = ActivityJson.Deserialize<IObjectOrLink>(json);
        var noteObj = Assert.IsAssignableFrom<IObject>(note);

        var attachments = noteObj.GetRichAttachments();
        Assert.Single(attachments);
        Assert.Equal("Document", attachments[0].Type);
        Assert.Equal("report.pdf", attachments[0].Name);
        Assert.Equal(new Iri("https://cdn.example.com/files/report.pdf"), attachments[0].Url);
    }

    [Fact]
    public void GetRichAttachments_FromAudioWithPreview_ReturnsPreview()
    {
        var json = """
        {
            "id": "https://a.domain.local/ap/v1/u/alice/notes/n1",
            "type": "Note",
            "attachment": [
                {
                    "type": "Audio",
                    "name": "podcast.mp3",
                    "url": "https://cdn.example.com/audio/podcast.mp3",
                    "preview": {
                        "type": "Image",
                        "url": "https://cdn.example.com/preview/podcast.jpg"
                    }
                }
            ]
        }
        """;

        var note = ActivityJson.Deserialize<IObjectOrLink>(json);
        var noteObj = Assert.IsAssignableFrom<IObject>(note);

        var attachments = noteObj.GetRichAttachments();
        Assert.Single(attachments);
        Assert.Equal("Audio", attachments[0].Type);
        Assert.Equal("podcast.mp3", attachments[0].Name);
        Assert.Equal(new Iri("https://cdn.example.com/audio/podcast.mp3"), attachments[0].Url);
        Assert.NotNull(attachments[0].Preview);
        Assert.Equal(new Iri("https://cdn.example.com/preview/podcast.jpg"), attachments[0].Preview!);
    }

    [Fact]
    public void GetRichAttachments_FromVideo_ReturnsVideoType()
    {
        var json = """
        {
            "id": "https://a.domain.local/ap/v1/u/alice/notes/n1",
            "type": "Note",
            "attachment": [
                {
                    "type": "Video",
                    "name": "clip.mp4",
                    "url": "https://cdn.example.com/video/clip.mp4",
                    "preview": "https://cdn.example.com/preview/clip.jpg"
                }
            ]
        }
        """;

        var note = ActivityJson.Deserialize<IObjectOrLink>(json);
        var noteObj = Assert.IsAssignableFrom<IObject>(note);

        var attachments = noteObj.GetRichAttachments();
        Assert.Single(attachments);
        Assert.Equal("Video", attachments[0].Type);
        Assert.Equal("clip.mp4", attachments[0].Name);
        Assert.Equal(new Iri("https://cdn.example.com/video/clip.mp4"), attachments[0].Url);
        Assert.NotNull(attachments[0].Preview);
    }

    [Fact]
    public void GetRichAttachments_FromLink_ReturnsNullType()
    {
        IObject note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Attachment = [new Link { Href = new Uri("https://example.com/page") }],
        };

        var attachments = note.GetRichAttachments();
        Assert.Single(attachments);
        Assert.Null(attachments[0].Type);
        Assert.Equal(new Iri("https://example.com/page"), attachments[0].Url);
    }

    [Fact]
    public void GetRichAttachments_MultipleMixedTypes_ReturnsAll()
    {
        var json = """
        {
            "id": "https://a.domain.local/ap/v1/u/alice/notes/n1",
            "type": "Note",
            "attachment": [
                { "type": "Image", "id": "https://cdn.example.com/1.jpg", "name": ["a.jpg"] },
                { "type": "Document", "name": "doc.pdf", "url": "https://cdn.example.com/doc.pdf" },
                { "type": "Audio", "name": "song.mp3", "url": "https://cdn.example.com/song.mp3" }
            ]
        }
        """;

        var note = ActivityJson.Deserialize<IObjectOrLink>(json);
        var noteObj = Assert.IsAssignableFrom<IObject>(note);

        var attachments = noteObj.GetRichAttachments();
        Assert.Equal(3, attachments.Count);
        Assert.Equal("Image", attachments[0].Type);
        Assert.Equal("Document", attachments[1].Type);
        Assert.Equal("Audio", attachments[2].Type);
    }

    [Fact]
    public void GetRichAttachments_NoAttachments_ReturnsEmpty()
    {
        IObject note = new Note { Id = "https://a.domain.local/ap/v1/u/alice/notes/n1" };
        Assert.Empty(note.GetRichAttachments());
    }

    [Fact]
    public void GetRichAttachments_Null_ReturnsEmpty()
    {
        IObject? none = null;
        Assert.Empty(none.GetRichAttachments());
    }
}
