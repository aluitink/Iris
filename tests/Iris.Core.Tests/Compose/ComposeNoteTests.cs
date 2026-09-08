using System.Text.Json;
using Iris.Core;
using Iris.Core.Compose;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

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
    public void Build_SetsSummary_WhenSensitiveAndSummaryPresent()
    {
        var note = ComposeNote.Build(Alice, "content", sensitive: true, summary: "NSFW");

        Assert.Equal("NSFW", note.Summary?.Single());
        Assert.Equal("NSFW", ((IObject)note).GetSummary());
    }

    [Fact]
    public void Build_OmitsSummary_WhenNotSensitive()
    {
        var note = ComposeNote.Build(Alice, "content", sensitive: false, summary: "NSFW");

        Assert.Null(note.Summary);
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
}
