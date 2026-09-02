using Bunit;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 20.4 (c) tests: the <c>ObjectView</c> component renders a note's <c>content</c> through the
/// dependency-free <see cref="Markdown"/> renderer (20.4c) — content that is Markdown (a heading, a
/// link, code, a list) displays as the rendered HTML, not as escaped raw text. These tests render
/// <c>ObjectView</c> in-process (bUnit) and assert the emitted markup.
/// </summary>
public sealed class S20MarkdownObjectViewTests
{
    /// <summary>
    /// Renders an <c>ObjectView</c> for the given item.
    /// </summary>
    private static IRenderedComponent<Iris.Samples.SampleBlazorClient.Components.ObjectView> RenderObjectView(
        BunitContext ctx,
        IObjectOrLink? item)
        => ctx.Render<Iris.Samples.SampleBlazorClient.Components.ObjectView>(
            p => p.Add(c => c.Item, item));

    private static Note NoteWithContent(string content)
        => new()
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42",
            Content = [content],
        };

    [Fact]
    public void ObjectView_NoteWithMarkdownHeading_RendersH1()
    {
        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, NoteWithContent("# A heading"));

        // The markdown heading renders as a real <h1>, not escaped raw text.
        Assert.Equal("A heading", cut.Find(".object-content h1").TextContent);
        Assert.DoesNotContain("# A heading", cut.Markup);
    }

    [Fact]
    public void ObjectView_NoteWithMarkdownLink_RendersAnchor()
    {
        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, NoteWithContent("see [the site](https://example.com/x)"));

        // The markdown link renders as a real <a> with the href.
        var a = cut.Find(".object-content a");
        Assert.Equal("https://example.com/x", a.GetAttribute("href"));
        Assert.Equal("the site", a.TextContent);
    }

    [Fact]
    public void ObjectView_NoteWithMarkdownCode_RendersCodeElement()
    {
        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, NoteWithContent("run `dotnet build` now"));

        Assert.Equal("dotnet build", cut.Find(".object-content code").TextContent);
    }

    [Fact]
    public void ObjectView_NoteWithMarkdownList_RendersListItems()
    {
        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, NoteWithContent("- one\n- two"));

        var items = cut.FindAll(".object-content li");
        Assert.Equal(2, items.Count);
        Assert.Equal("one", items[0].TextContent);
        Assert.Equal("two", items[1].TextContent);
    }

    [Fact]
    public void ObjectView_NoteWithPlainContent_RendersAsParagraphText()
    {
        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, NoteWithContent("just plain text, no markdown"));

        // Plain content renders as a paragraph with the literal text.
        Assert.Equal("just plain text, no markdown", cut.Find(".object-content p").TextContent);
    }

    [Fact]
    public void ObjectView_NoteWithRawHtml_EscapesItDoesNotRenderLiveTags()
    {
        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, NoteWithContent("<script>alert(1)</script>"));

        // A <script> in the content is escaped (inert) — no live script tag is emitted.
        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("&lt;script&gt;", cut.Markup);
    }

    [Fact]
    public void ObjectView_SensitiveNoteWithMarkdown_Revealed_RendersMarkdown()
    {
        // A sensitive note whose content is markdown renders the markdown only after reveal.
        var note = NoteWithContent("# secret heading");
        note.ExtensionData = new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>
        {
            ["sensitive"] = System.Text.Json.JsonDocument.Parse("true").RootElement.Clone(),
        };

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        // Hidden initially: no heading rendered.
        Assert.Empty(cut.FindAll(".object-content h1"));

        // Reveal: the markdown heading renders.
        cut.Find(".object-sensitive-reveal").Click();
        Assert.Equal("secret heading", cut.Find(".object-content h1").TextContent);
    }
}
