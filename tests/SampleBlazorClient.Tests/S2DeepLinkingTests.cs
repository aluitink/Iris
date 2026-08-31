using Iris.Core;
using Iris.Core.Identity;
using Bunit;
using KristofferStrube.ActivityStreams;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 8 (second round) S2 tests: deep-linking. The <c>ObjectView</c> component renders every object
/// and actor IRI as a real link (not a dead <c>&lt;a href="#"&gt;</c>): an object IRI links to the
/// explorer's object page (<c>/object?iri=…</c>), an actor IRI (the author / a mention) links to the
/// actor page (<c>/actor?iri=…</c>), and a reply's parent (<c>inReplyTo</c>) + mentions + attachments are
/// likewise rendered as links. These tests render <c>ObjectView</c> in-process (bUnit) and assert the
/// emitted <c>&lt;a href&gt;</c> values point at the right routes — the navigation surface S2 adds.
/// </summary>
public sealed class S2DeepLinkingTests
{
    /// <summary>
    /// A rendered anchor: its href and display text.
    /// </summary>
    private readonly record struct Anchor(string Href, string Text);

    /// <summary>
    /// Collects the rendered links from a rendered component so a test can assert the emitted
    /// navigation targets.
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

    [Fact]
    public void ObjectView_Note_RendersObjectLinkToObjectPage()
    {
        var noteIri = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/42";
        var note = new Note
        {
            Id = noteIri,
            Content = ["<p>a note</p>"],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Components.ObjectView>(
            p => p.Add(c => c.Item, (IObjectOrLink?)note));

        var objectLink = Hrefs(cut).FirstOrDefault(l => l.Href.Contains("/object?iri="));
        Assert.True(objectLink.Href.Length > 0, "an object IRI must render a link to /object?iri=…");
        Assert.Equal(noteIri, IriFromHref(objectLink.Href));
    }

    [Fact]
    public void ObjectView_NoteWithAuthor_RendersActorLinkToActorPage()
    {
        var actorIri = "https://iris-dev1.luit.ink/ap/v1/u/bob";
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/1",
            AttributedTo = [new Link { Href = new Uri(actorIri) }],
            Content = ["<p>authored</p>"],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Components.ObjectView>(
            p => p.Add(c => c.Item, (IObjectOrLink?)note));

        var actorLink = Hrefs(cut).FirstOrDefault(l => l.Href.Contains("/actor?iri="));
        Assert.True(actorLink.Href.Length > 0, "an author IRI must render a link to /actor?iri=…");
        Assert.Equal(actorIri, IriFromHref(actorLink.Href));
        Assert.Equal("bob", actorLink.Text);
    }

    [Fact]
    public void ObjectView_Actor_RendersObjectLinkToObjectPage()
    {
        var actorIri = "https://iris-dev1.luit.ink/ap/v1/u/alice";
        var actor = new Person
        {
            Id = actorIri,
            PreferredUsername = "alice",
            Name = ["alice"],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Components.ObjectView>(
            p => p.Add(c => c.Item, (IObjectOrLink?)actor));

        // An actor's own IRI renders as a link to /object?iri=… (an actor is an object).
        var objectLink = Hrefs(cut).FirstOrDefault(l => l.Href.Contains("/object?iri="));
        Assert.True(objectLink.Href.Length > 0, "an actor's IRI must render a link to /object?iri=…");
        Assert.Equal(actorIri, IriFromHref(objectLink.Href));
    }

    [Fact]
    public void ObjectView_Reply_RendersParentLinkToObjectPage()
    {
        var parentIri = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/7";
        var reply = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/alice/notes/99",
            AttributedTo = [new Link { Href = new Uri("https://iris-dev1.luit.ink/ap/v1/u/alice") }],
            InReplyTo = [new Link { Href = new Uri(parentIri) }],
            Content = ["<p>a reply</p>"],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Components.ObjectView>(
            p => p.Add(c => c.Item, (IObjectOrLink?)reply));

        var hrefs = Hrefs(cut).Select(l => l.Href).ToList();
        Assert.True(
            hrefs.Any(h => h.Contains("/object?iri=") && h.Contains(Uri.EscapeDataString(parentIri))),
            $"a reply must render a link to its parent object page (hrefs: {string.Join("; ", hrefs)})");
    }

    [Fact]
    public void ObjectView_NoteWithMention_RendersMentionLinkToActorPage()
    {
        var mentionedIri = "https://iris-dev1.luit.ink/ap/v1/u/carol";
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/5",
            AttributedTo = [new Link { Href = new Uri("https://iris-dev1.luit.ink/ap/v1/u/bob") }],
            Tag =
            [
                new Mention { Href = new Uri(mentionedIri), Name = ["@carol"] },
            ],
            Content = ["<p>hey @carol</p>"],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Components.ObjectView>(
            p => p.Add(c => c.Item, (IObjectOrLink?)note));

        // The author (bob) also renders an /actor?iri= link, so target the mention (carol) specifically.
        var mentionLink = Hrefs(cut).FirstOrDefault(l => l.Href.Contains("/actor?iri=") && IriFromHref(l.Href) == mentionedIri);
        Assert.True(mentionLink.Href.Length > 0, "a mention IRI must render a link to /actor?iri=…");
        Assert.Equal(mentionedIri, IriFromHref(mentionLink.Href));
        Assert.Equal("carol", mentionLink.Text);
    }

    [Fact]
    public void ObjectView_NoteWithAttachment_RendersAttachmentLink()
    {
        var attachmentIri = "https://iris-dev1.luit.ink/ap/v1/media/photo1.png";
        var note = new Note
        {
            Id = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/6",
            AttributedTo = [new Link { Href = new Uri("https://iris-dev1.luit.ink/ap/v1/u/bob") }],
            Attachment =
            [
                new Image { Id = attachmentIri },
            ],
            Content = ["<p>with a photo</p>"],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Components.ObjectView>(
            p => p.Add(c => c.Item, (IObjectOrLink?)note));

        var attachmentLink = Hrefs(cut).FirstOrDefault(l => l.Href.Contains("/ap/v1/media/"));
        Assert.True(attachmentLink.Href.Length > 0, "an attachment IRI must render a link");
    }

    [Fact]
    public void ObjectView_LinkItem_RendersObjectLinkToObjectPage()
    {
        var target = "https://iris-dev1.luit.ink/ap/v1/u/bob/notes/8";
        var link = new Link { Href = new Uri(target) };

        using var ctx = new BunitContext();
        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Components.ObjectView>(
            p => p.Add(c => c.Item, (IObjectOrLink?)link));

        var objectLink = Hrefs(cut).FirstOrDefault(l => l.Href.Contains("/object?iri="));
        Assert.True(objectLink.Href.Length > 0, "a link item's href must render a link to /object?iri=…");
        Assert.Equal(target, IriFromHref(objectLink.Href));
    }
}
