using System.Text.Json;
using Bunit;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 20.4 (sensitivity) tests: the <c>ObjectView</c> component renders a content-sensitive
/// object (the ActivityStreams <c>sensitive</c> term) behind a notice that carries the object's
/// <c>summary</c>, and does not render the object's real content until the viewer reveals it. These
/// tests render <c>ObjectView</c> in-process (bUnit) and assert the emitted markup — the
/// read-side sensitivity slice 20.4 adds.
/// </summary>
public sealed class S20SensitivityObjectViewTests
{
    /// <summary>
    /// Renders an <c>ObjectView</c> for the given item.
    /// </summary>
    private static IRenderedComponent<Iris.Samples.SampleBlazorClient.Components.ObjectView> RenderObjectView(
        BunitContext ctx,
        IObjectOrLink? item)
        => ctx.Render<Iris.Samples.SampleBlazorClient.Components.ObjectView>(
            p => p.Add(c => c.Item, item));

    /// <summary>
    /// Builds a sensitive <c>Note</c> (the <c>sensitive</c> term lives in <c>ExtensionData</c>, per
    /// the 3rd-Party ActivityStreams rules) with the given content and summary.
    /// </summary>
    private static Note SensitiveNote(string content, string? summary)
    {
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42",
            Content = [content],
        };
        if (summary is not null)
        {
            note.Summary = [summary];
        }

        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["sensitive"] = JsonDocument.Parse("true").RootElement.Clone(),
        };
        return note;
    }

    [Fact]
    public void ObjectView_SensitiveNote_RendersNoticeAndHidesContent()
    {
        var note = SensitiveNote("<p>the actual secret content</p>", "A secret photo");

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        // The sensitive wrapper + notice are present, carrying the summary and the reveal button.
        Assert.NotEmpty(cut.FindAll(".object-sensitive"));
        var notice = cut.Find(".object-sensitive-notice");
        Assert.Equal("A secret photo", cut.Find(".object-sensitive-summary").TextContent);
        Assert.Equal("Show", cut.Find(".object-sensitive-reveal").TextContent);

        // The real content must NOT be rendered (nor present in the DOM) while hidden.
        Assert.Empty(cut.FindAll(".object-content"));
        Assert.DoesNotContain("the actual secret content", cut.Markup);
        Assert.NotNull(notice);
    }

    [Fact]
    public void ObjectView_SensitiveNote_Reveal_ShowsContent()
    {
        var note = SensitiveNote("<p>the actual secret content</p>", "A secret photo");

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        // Hidden initially.
        Assert.Empty(cut.FindAll(".object-content"));

        // Reveal it.
        cut.Find(".object-sensitive-reveal").Click();

        // The real content is now rendered (as text — the object view escapes HTML, rendering
        // markdown/HTML is a separate slice), and the button flips to "Hide".
        Assert.Equal("<p>the actual secret content</p>", cut.Find(".object-content").TextContent);
        Assert.Equal("Hide", cut.Find(".object-sensitive-reveal").TextContent);
    }

    [Fact]
    public void ObjectView_SensitiveNote_Reveal_TogglesBackToHidden()
    {
        var note = SensitiveNote("<p>the actual secret content</p>", "A secret photo");

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        cut.Find(".object-sensitive-reveal").Click(); // reveal
        Assert.NotEmpty(cut.FindAll(".object-content"));

        cut.Find(".object-sensitive-reveal").Click(); // hide again
        Assert.Empty(cut.FindAll(".object-content"));
        Assert.Equal("Show", cut.Find(".object-sensitive-reveal").TextContent);
    }

    [Fact]
    public void ObjectView_SensitiveNoteWithoutSummary_RendersNoticeLabelOnly()
    {
        var note = SensitiveNote("<p>the actual secret content</p>", null);

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        // No summary element, but the generic label + reveal button are present and content hidden.
        Assert.Empty(cut.FindAll(".object-sensitive-summary"));
        Assert.Equal("This content may be sensitive.", cut.Find(".object-sensitive-label").TextContent);
        Assert.Equal("Show", cut.Find(".object-sensitive-reveal").TextContent);
        Assert.Empty(cut.FindAll(".object-content"));
    }

    [Fact]
    public void ObjectView_NonSensitiveNote_RendersContentDirectlyWithNoNotice()
    {
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42",
            Content = ["<p>an ordinary note</p>"],
        };

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        // No sensitive wrapper/notice; the content renders directly (as text).
        Assert.Empty(cut.FindAll(".object-sensitive"));
        Assert.Equal("<p>an ordinary note</p>", cut.Find(".object-content").TextContent);
        Assert.Empty(cut.FindAll(".object-sensitive-reveal"));
    }
}
