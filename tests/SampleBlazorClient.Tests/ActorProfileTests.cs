using Iris.Samples.SampleBlazorClient.Components;
using Bunit;
using KristofferStrube.ActivityStreams;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// bUnit component tests for <see cref="ActorProfile"/> (31.5): the profile-style header at the top of
/// the actor/community detail pages. Pins the header's presentation contract — the actor's icon (avatar)
/// + handle + name + summary render as a profile (not just the raw object view), a missing icon falls
/// back to a name initial, a redundant name (== handle) is omitted, and a non-actor renders a muted
/// placeholder — the invariants the browser-assisted sample review (31.5) confirmed live.
/// </summary>
public class ActorProfileTests
{
    private const string AliceIri = "https://a.domain.local/ap/v1/u/alice";

    [Fact]
    public void ActorWithIcon_RendersAvatarImage()
    {
        // The profile shows the actor's icon as an <img> avatar (the icon IRI, resolved from the icon's
        // id) — not just the raw object view.
        var actor = new Person
        {
            Id = AliceIri,
            PreferredUsername = "alice",
            Name = ["Alice"],
            Icon = [new Image { Id = "https://a.domain.local/ap/v1/media/alice.png" }],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<ActorProfile>(parameters => parameters
            .Add(p => p.Actor, actor)
            .Add(p => p.ActorId, AliceIri));

        var img = cut.Find("img.actor-profile-avatar-img");
        Assert.Equal("https://a.domain.local/ap/v1/media/alice.png", img.GetAttribute("src"));
        // The avatar is a link out to the icon IRI.
        Assert.Equal("https://a.domain.local/ap/v1/media/alice.png", cut.Find(".actor-profile-avatar a").GetAttribute("href"));
    }

    [Fact]
    public void ActorWithoutIcon_RendersInitialFallback()
    {
        // No icon → the header falls back to the name's initial in a styled frame (a profile-style
        // avatar, never a broken image).
        var actor = new Person
        {
            Id = AliceIri,
            PreferredUsername = "alice",
            Name = ["Alice"],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<ActorProfile>(parameters => parameters
            .Add(p => p.Actor, actor)
            .Add(p => p.ActorId, AliceIri));

        var fallback = cut.Find(".actor-profile-avatar-fallback");
        Assert.Equal("A", fallback.TextContent.Trim());
        Assert.Empty(cut.FindAll("img"));
    }

    [Fact]
    public void Actor_RendersHandleAndNameAndSummaryAndIri()
    {
        var actor = new Person
        {
            Id = AliceIri,
            PreferredUsername = "alice",
            Name = ["Alice the Engineer"],
            Summary = ["Building things with ActivityPub."],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<ActorProfile>(parameters => parameters
            .Add(p => p.Actor, actor)
            .Add(p => p.ActorId, AliceIri));

        Assert.Equal("alice", cut.Find(".actor-profile-handle").TextContent.Trim());
        Assert.Equal("Alice the Engineer", cut.Find(".actor-profile-name").TextContent.Trim());
        Assert.Equal("Building things with ActivityPub.", cut.Find(".actor-profile-summary").TextContent.Trim());
        Assert.Contains(AliceIri, cut.Find(".actor-profile-iri").TextContent);
    }

    [Fact]
    public void Actor_RedundantName_Omitted()
    {
        // A name identical to the preferredUsername (the seeded actors) is omitted — the name would
        // duplicate the handle on the same line (29.2 visual-review convention).
        var actor = new Person
        {
            Id = AliceIri,
            PreferredUsername = "alice",
            Name = ["alice"],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<ActorProfile>(parameters => parameters
            .Add(p => p.Actor, actor)
            .Add(p => p.ActorId, AliceIri));

        Assert.Equal("alice", cut.Find(".actor-profile-handle").TextContent.Trim());
        Assert.Empty(cut.FindAll(".actor-profile-name"));
    }

    [Fact]
    public void NonActor_RendersMutedPlaceholder()
    {
        // A non-actor document (a note) is not an actor profile: the header renders a muted
        // placeholder rather than an empty frame.
        var note = new Note
        {
            Id = "https://a.domain.local/ap/v1/u/alice/notes/n1",
            Content = ["hello"],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<ActorProfile>(parameters => parameters
            .Add(p => p.Actor, note)
            .Add(p => p.ActorId, note.Id));

        Assert.Contains("No actor document", cut.Markup);
        Assert.Empty(cut.FindAll(".actor-profile-handle"));
    }

    [Fact]
    public void Group_RendersAsActorProfile()
    {
        // A community (a Group actor) is an Actor: the header presents it the same way (handle + name),
        // confirming the profile-style treatment generalizes beyond Person (31.6 reuses this for
        // /community).
        var group = new Group
        {
            Id = "https://a.domain.local/ap/v1/c/community",
            PreferredUsername = "community",
            Name = ["Iris Community"],
            Icon = [new Link { Href = new Uri("https://a.domain.local/ap/v1/media/community.png") }],
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<ActorProfile>(parameters => parameters
            .Add(p => p.Actor, group)
            .Add(p => p.ActorId, group.Id));

        Assert.Equal("community", cut.Find(".actor-profile-handle").TextContent.Trim());
        Assert.Equal("Iris Community", cut.Find(".actor-profile-name").TextContent.Trim());
        Assert.Equal(
            "https://a.domain.local/ap/v1/media/community.png",
            cut.Find("img.actor-profile-avatar-img").GetAttribute("src"));
    }
}
