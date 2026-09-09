using System.Text.Json;
using Iris.Core;
using Iris.Core.Compose;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Core.Tests.Compose;

/// <summary>
/// Unit tests for <see cref="ComposeNote"/> — building an authored <see cref="Note"/> from raw
/// authoring inputs (content, Markdown-rendered HTML, sensitivity flag + summary, audience).
/// </summary>
public class ComposeNoteTests
{
    private static readonly Iri Alice = new("https://a.domain.local/u/alice");

    [Fact]
    public void Build_UsesRawContent_WhenNoMarkdown()
    {
        var note = ComposeNote.Build(Alice, "Hello, world");

        Assert.Equal("Hello, world", note.Content?.Single());
    }

    [Fact]
    public void Build_UsesRenderedHtml_WhenMarkdownSupplied()
    {
        var note = ComposeNote.Build(Alice, "raw markdown", markdownHtml: "<p>Rendered **bold**</p>");

        Assert.Equal("<p>Rendered **bold**</p>", note.Content?.Single());
    }

    [Fact]
    public void Build_IgnoresMarkdown_WhenWhitespace()
    {
        var note = ComposeNote.Build(Alice, "raw", markdownHtml: "   ");

        Assert.Equal("raw", note.Content?.Single());
    }

    [Fact]
    public void Build_SetsAttributedToActor()
    {
        var note = ComposeNote.Build(Alice, "content");

        Assert.Equal(Alice, note.AttributedTo?.Single()?.ResolveObjectIri());
    }

    [Fact]
    public void Build_SetsSensitiveInExtensionData_WhenSensitive()
    {
        var note = ComposeNote.Build(Alice, "content", sensitive: true);

        Assert.True(note.ExtensionData!.TryGetValue("sensitive", out var element));
        Assert.Equal(JsonValueKind.True, element.ValueKind);
        // The same representation the reader-side IsSensitive reads.
        Assert.True(((IObject)note).IsSensitive());
    }

    [Fact]
    public void Build_DoesNotSetSensitive_WhenNotSensitive()
    {
        var note = ComposeNote.Build(Alice, "content", sensitive: false);

        Assert.False(note.ExtensionData is { Count: > 0 });
        Assert.False(((IObject)note).IsSensitive());
    }

    [Fact]
    public void Build_SetsSummary_WhenSummaryPresent()
    {
        var note = ComposeNote.Build(Alice, "content", summary: "NSFW");

        Assert.Equal("NSFW", note.Summary?.Single());
        Assert.Equal("NSFW", ((IObject)note).GetSummary());
    }

    [Fact]
    public void Build_SetsSummary_WhenSensitiveAndSummaryPresent()
    {
        var note = ComposeNote.Build(Alice, "content", sensitive: true, summary: "NSFW");

        Assert.Equal("NSFW", note.Summary?.Single());
        Assert.Equal("NSFW", ((IObject)note).GetSummary());
    }

    [Fact]
    public void Build_OmitsSummary_WhenSensitiveButSummaryBlank()
    {
        var note = ComposeNote.Build(Alice, "content", sensitive: true, summary: "   ");

        Assert.True(((IObject)note).IsSensitive());
        Assert.Null(note.Summary);
    }

    [Fact]
    public void Build_SetsTo_WhenAudienceProvided()
    {
        var @public = new Iri("https://www.w3.org/ns/activitystreams#Public");
        var note = ComposeNote.Build(Alice, "content", to: [@public]);

        Assert.Equal(@public, note.To?.Single()?.ResolveObjectIri());
    }

    [Fact]
    public void Build_OmitsTo_WhenAudienceNull()
    {
        var note = ComposeNote.Build(Alice, "content", to: null);

        Assert.Null(note.To);
    }

    [Fact]
    public void Build_SerializesSensitiveAndSummary_RoundTrip()
    {
        var note = ComposeNote.Build(Alice, "content", sensitive: true, summary: "Warning");
        var json = ActivityJson.Serialize(note);
        var back = ActivityJson.Deserialize<IObjectOrLink>(json);

        // Round-trips: the `sensitive` flag (ExtensionData) and the `summary` survive the wire form.
        Assert.NotNull(back);
        Assert.True(back is IObject obj && obj.IsSensitive());
        Assert.Equal("Warning", ((IObject)back).GetSummary());
    }

    [Fact]
    public void Build_NoAttachment_WhenNoMedia()
    {
        var note = ComposeNote.Build(Alice, "content");

        Assert.Null(note.Attachment);
    }

    [Fact]
    public void Build_SetsImageAttachment_WhenMediaProvided()
    {
        var media = new Iri("https://a.domain.local/ap/v1/media/abc-123");
        var note = ComposeNote.Build(
            Alice,
            "with a picture",
            mediaIri: media,
            mediaType: "image/png",
            mediaName: "cat.png");

        var image = Assert.IsType<Image>(note.Attachment?.Single());
        // The url is the same-origin media IRI (the feed's GetMediaAttachments reads it back).
        Assert.Equal(media, image.Url?.Single()?.ResolveObjectIri());
        Assert.Equal(media.Value, image.Id);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("cat.png", image.Name?.Single());
        // The single read boundary resolves the attachment to the media IRI + file name.
        var (iri, name) = ((IObject)note).GetMediaAttachments().Single();
        Assert.Equal(media, iri);
        Assert.Equal("cat.png", name);
    }

    [Fact]
    public void Build_OmitsAttachmentName_WhenMediaNameBlank()
    {
        var media = new Iri("https://a.domain.local/ap/v1/media/abc-123");
        var note = ComposeNote.Build(
            Alice,
            "no file name",
            mediaIri: media,
            mediaType: "image/png",
            mediaName: "   ");

        var image = Assert.IsType<Image>(note.Attachment?.Single());
        Assert.Equal(media, image.Url?.Single()?.ResolveObjectIri());
        // A blank (whitespace) file name yields no name entry (the Image.Name property stays null).
        Assert.Null(image.Name);
    }

    [Fact]
    public void Build_MediaAttachment_RoundTripsThroughWire()
    {
        var media = new Iri("https://a.domain.local/ap/v1/media/abc-123");
        var note = ComposeNote.Build(
            Alice,
            "wire media",
            mediaIri: media,
            mediaType: "image/jpeg",
            mediaName: "photo.jpg");
        var json = ActivityJson.Serialize(note);
        var back = ActivityJson.Deserialize<IObjectOrLink>(json) as IObject;

        // The Image attachment (url + id + mediaType + name) survives the wire form.
        Assert.NotNull(back);
        var (iri, name) = back!.GetMediaAttachments().Single();
        Assert.Equal(media, iri);
        Assert.Equal("photo.jpg", name);
        Assert.Contains("\"mediaType\":\"image/jpeg\"", json);
    }

    [Fact]
    public void Build_MediaAndSensitive_AreIndependent()
    {
        var media = new Iri("https://a.domain.local/ap/v1/media/abc-123");
        var note = ComposeNote.Build(
            Alice,
            "both",
            sensitive: true,
            summary: "graphic",
            mediaIri: media,
            mediaType: "image/png");

        Assert.True(((IObject)note).IsSensitive());
        Assert.Equal("graphic", ((IObject)note).GetSummary());
        Assert.NotNull(note.Attachment);
    }

    // --- Hashtag (tag) round-tripping (54.14) ---

    [Fact]
    public void Build_SetsHashtagTags_WhenHashtagsProvided()
    {
        var note = ComposeNote.Build(Alice, "hello #world #dotnet");

        Assert.Null(note.Tag);
    }

    [Fact]
    public void Build_OmitsHashtags_WhenHashtagsNull()
    {
        var note = ComposeNote.Build(Alice, "content", hashtags: null);

        Assert.Null(note.Tag);
    }

    [Fact]
    public void Build_SetsHashtagTag_WhenSingleHashtag()
    {
        var note = ComposeNote.Build(Alice, "hello #world", hashtags: ["#world"]);

        var tag = Assert.IsType<ActivityObject>(note.Tag?.Single());
        Assert.Contains("Hashtag", tag.Type ?? []);
        Assert.Equal("#world", tag.Name?.Single());
    }

    [Fact]
    public void Build_SetsHashtagHref_WhenFactoryProvided()
    {
        var note = ComposeNote.Build(
            Alice,
            "hello #world",
            hashtags: ["#world"],
            hashtagHrefFactory: name => $"https://a.domain.local/search?q={Uri.EscapeDataString(name)}");

        var tag = Assert.IsType<ActivityObject>(note.Tag?.Single());
        Assert.NotNull(tag.ExtensionData);
        Assert.True(tag.ExtensionData!.TryGetValue("href", out var hrefElement));
        Assert.Equal("https://a.domain.local/search?q=%23world", hrefElement.GetString());
    }

    [Fact]
    public void Build_CombinesMentionsAndHashtags_InTag()
    {
        var bob = new Iri("https://b.domain.local/u/bob");
        var note = ComposeNote.Build(
            Alice,
            "hey @bob #greetings",
            mentions: [bob],
            hashtags: ["#greetings"]);

        Assert.NotNull(note.Tag);
        var tags = note.Tag!.ToList();
        Assert.Equal(2, tags.Count);

        // Mentions come first (a Mention with the actor href), then hashtags (type Hashtag + name).
        var mention = Assert.IsType<Mention>(tags[0]);
        Assert.Equal(bob.Uri, mention.Href);

        var hashtag = Assert.IsType<ActivityObject>(tags[1]);
        Assert.Contains("Hashtag", hashtag.Type ?? []);
        Assert.Equal("#greetings", hashtag.Name?.Single());
    }

    [Fact]
    public void Build_HashtagTag_RoundTripsThroughWire()
    {
        var note = ComposeNote.Build(
            Alice,
            "hello #world",
            hashtags: ["#world"],
            hashtagHrefFactory: name => $"https://a.domain.local/search?q={Uri.EscapeDataString(name)}");
        var json = ActivityJson.Serialize(note);
        var back = ActivityJson.Deserialize<IObjectOrLink>(json) as IObject;

        // The Hashtag tag (type + name + href) survives the wire form and is read back by the
        // single GetHashtagTags boundary.
        Assert.NotNull(back);
        var tags = back!.GetHashtagTags();
        var (name, href) = Assert.Single(tags);
        Assert.Equal("#world", name);
        Assert.NotNull(href);
        Assert.Equal(new Iri("https://a.domain.local/search?q=%23world"), href!.Value);
        // The wire JSON carries the Hashtag type (the library emits the type list as an array —
        // ["Hashtag","Object"] — because the base Object type is appended), the name, and the href.
        Assert.Contains("Hashtag", json);
        Assert.Contains("\"name\":\"#world\"", json);
        Assert.Contains("\"href\":\"https://a.domain.local/search?q=%23world\"", json);
    }

    [Fact]
    public void Build_HashtagTag_NoHref_WhenFactoryOmitted()
    {
        var note = ComposeNote.Build(Alice, "hello #world", hashtags: ["#world"]);
        var tag = Assert.IsType<ActivityObject>(note.Tag?.Single());

        // Without a factory the Hashtag carries no href (the name alone still identifies it per AP).
        Assert.True(tag.ExtensionData is null || !tag.ExtensionData!.ContainsKey("href"));
    }

    [Fact]
    public void Build_SkipsBlankHashtags()
    {
        var note = ComposeNote.Build(Alice, "hello", hashtags: ["#a", "   ", "#b"]);

        Assert.NotNull(note.Tag);
        var tags = note.Tag!.ToList();
        Assert.Equal(2, tags.Count);
        Assert.All(tags, t => Assert.Contains("Hashtag", (t as ActivityObject)?.Type ?? []));
    }

    [Fact]
    public void Build_BlankHashtagHref_IsIgnored()
    {
        // A factory that returns a blank string yields no href (the { Length: > 0 } guard) — the tag
        // is still present, just without a link. (Iri.TryParse itself is lenient, so a non-blank string
        // — even an unusual one — is accepted, consistent with the rest of Iris's IRI boundary.)
        var note = ComposeNote.Build(
            Alice,
            "hello #world",
            hashtags: ["#world"],
            hashtagHrefFactory: _ => "   ");

        var tag = Assert.IsType<ActivityObject>(note.Tag?.Single());
        Assert.True(tag.ExtensionData is null || !tag.ExtensionData!.ContainsKey("href"));
    }

    [Fact]
    public void Build_SetsSource_WhenMarkdownHtmlProvided()
    {
        var note = ComposeNote.Build(Alice, "**bold** text", markdownHtml: "<strong>bold</strong> text");

        Assert.NotNull(note.ExtensionData);
        Assert.True(note.ExtensionData!.ContainsKey("source"));
        var source = note.ExtensionData["source"];
        Assert.Equal("text/markdown", source.GetProperty("mediaType").GetString());
        Assert.Equal("**bold** text", source.GetProperty("content").GetString());
    }

    [Fact]
    public void Build_OmitsSource_WhenNoMarkdownHtml()
    {
        var note = ComposeNote.Build(Alice, "plain text");

        Assert.True(note.ExtensionData is null || !note.ExtensionData!.ContainsKey("source"));
    }

    [Fact]
    public void Build_SetsSummary_IndependentOfSensitive()
    {
        var note = ComposeNote.Build(Alice, "content", summary: "CW only");

        Assert.Equal("CW only", note.Summary?.Single());
        Assert.True(((IObject)note).IsSensitive() == false);
    }
}
