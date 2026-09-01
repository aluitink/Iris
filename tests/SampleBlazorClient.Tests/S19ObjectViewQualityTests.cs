using Iris.Core;
using Iris.Core.Identity;
using Bunit;
using KristofferStrube.ActivityStreams;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 19 (19.8.2) S19 tests: rendered object view quality. The <c>ObjectView</c> component renders
/// an object's audience (its <c>to</c> + <c>cc</c> recipients, as links to the recipients' actor pages,
/// the public-audience sentinel excluded) and its <c>published</c> timestamp (as a local time). These
/// tests render <c>ObjectView</c> in-process (bUnit) and assert the new markup is present — the
/// read-side quality slice 19.8.2 adds beyond the S2 deep-linking surface.
/// </summary>
public sealed class S19ObjectViewQualityTests
{
    /// <summary>
    /// A rendered anchor: its href and display text.
    /// </summary>
    private readonly record struct Anchor(string Href, string Text);

    /// <summary>
    /// Collects the rendered links from a rendered component so a test can assert the emitted
    /// audience navigation targets.
    /// </summary>
    private static List<Anchor> Hrefs(IRenderedComponent<Iris.Samples.SampleBlazorClient.Components.ObjectView> cut)
        => cut.FindAll("a")
            .Select(a => new Anchor(a.GetAttribute("href") ?? string.Empty, a.TextContent.Trim()))
            .Where(t => !string.IsNullOrEmpty(t.Href))
            .ToList();

    /// <summary>
    /// Decodes the <c>iri=</c> query param out of an emitted href.
    /// </summary>
    private static string IriFromHref(string href)
    {
        var idx = href.IndexOf("iri=", StringComparison.Ordinal);
        return Uri.UnescapeDataString(href[(idx + 4)..]);
    }

    private static IRenderedComponent<Iris.Samples.SampleBlazorClient.Components.ObjectView> RenderObjectView(
        BunitContext ctx,
        IObjectOrLink? item)
        => ctx.Render<Iris.Samples.SampleBlazorClient.Components.ObjectView>(
            p => p.Add(c => c.Item, item));

    [Fact]
    public void ObjectView_NoteWithAudience_RendersToAndCcAsActorLinks()
    {
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42",
            To = [new Link { Href = new Uri("https://iris-dev1.luit.ink/ap/v1/u/alice") }],
            Cc = [new Link { Href = new Uri("https://iris-dev1.luit.ink/ap/v1/c/iris") }],
            Content = ["<p>a note for the community</p>"],
        };

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        var toLink = Hrefs(cut).FirstOrDefault(l => IriFromHref(l.Href) == "https://iris-dev1.luit.ink/ap/v1/u/alice");
        var ccLink = Hrefs(cut).FirstOrDefault(l => IriFromHref(l.Href) == "https://iris-dev1.luit.ink/ap/v1/c/iris");

        Assert.True(toLink.Href.Length > 0, "a `to` audience IRI must render a link to /actor?iri=…");
        Assert.True(ccLink.Href.Length > 0, "a `cc` audience IRI must render a link to /actor?iri=…");
    }

    [Fact]
    public void ObjectView_NoteWithAudience_AudienceLinkShowsTheRecipientHandle()
    {
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42",
            To = [new Link { Href = new Uri("https://iris-dev1.luit.ink/ap/v1/u/alice") }],
            Content = ["<p>hello</p>"],
        };

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        var toLink = Hrefs(cut).FirstOrDefault(l => IriFromHref(l.Href) == "https://iris-dev1.luit.ink/ap/v1/u/alice");
        Assert.True(toLink.Href.Length > 0, "a `to` audience IRI must render a link");
        Assert.Equal("alice", toLink.Text);
    }

    [Fact]
    public void ObjectView_NoteWithOnlyPublicAudience_RendersNoAudienceLink()
    {
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42",
            To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
            Content = ["<p>a public note</p>"],
        };

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        // The public sentinel must not be rendered as an audience link.
        var audienceLinks = cut.FindAll("a").Where(a => (a.GetAttribute("href") ?? "").Contains("/actor?iri=")).ToList();
        Assert.Empty(audienceLinks);
    }

    [Fact]
    public void ObjectView_NoteWithPublished_RendersPublishedTimestamp()
    {
        var published = new DateTimeOffset(2026, 8, 24, 12, 30, 45, TimeSpan.Zero).UtcDateTime;
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42",
            Published = published,
            Content = ["<p>dated</p>"],
        };

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        // The published element carries the RFC-3339 value in its `title` (a stable assertion target).
        var publishedEl = cut.Find(".object-published");
        Assert.Equal(published.ToString("o"), publishedEl.GetAttribute("title"));
    }

    [Fact]
    public void ObjectView_NoteWithoutPublished_RendersNoTimestamp()
    {
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42",
            Content = ["<p>undated</p>"],
        };

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        Assert.Empty(cut.FindAll(".object-published"));
    }

    [Fact]
    public void ObjectView_NoteWithoutAudience_RendersNoAudienceRow()
    {
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42",
            Content = ["<p>no audience</p>"],
        };

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        Assert.Empty(cut.FindAll(".object-audience"));
    }

    [Fact]
    public void ObjectView_NoteWithAudienceAndPublished_RendersBoth()
    {
        var published = new DateTimeOffset(2026, 8, 24, 12, 30, 45, TimeSpan.Zero).UtcDateTime;
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42",
            To = [new Link { Href = new Uri("https://iris-dev1.luit.ink/ap/v1/u/alice") }],
            Published = published,
            Content = ["<p>full</p>"],
        };

        using var ctx = new BunitContext();
        var cut = RenderObjectView(ctx, note);

        var toLink = Hrefs(cut).FirstOrDefault(l => IriFromHref(l.Href) == "https://iris-dev1.luit.ink/ap/v1/u/alice");
        Assert.True(toLink.Href.Length > 0, "a `to` audience IRI must render a link");
        var publishedEl = cut.Find(".object-published");
        Assert.Equal(published.ToString("o"), publishedEl.GetAttribute("title"));
    }
}
