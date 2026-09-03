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
}
