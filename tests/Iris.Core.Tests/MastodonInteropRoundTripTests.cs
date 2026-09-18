using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Tests;

/// <summary>
/// Interop round-trip tests for real Mastodon wire format (Phase 81.1). The fixtures are genuine
/// documents captured from <c>mastodon.online</c> (a live Mastodon instance that permits unsigned
/// ActivityPub fetches) — full Mastodon <c>@context</c> (an array of strings + <c>@id</c>/<c>@type</c>
/// term mappings), explicit <c>null</c> values for absent fields, and a scalar <c>attributedTo</c>.
/// They exercise the scenarios Iris must federate correctly: a <c>Person</c> actor; a public
/// <c>Note</c> with a media <c>Document</c> attachment + hashtag; a followers-only cross-instance
/// <c>Note</c> reply with a mention; and a public <c>Note</c> with multiple mentions.
///
/// Each fixture is deserialized through the polymorphic <see cref="ActivityJson"/> entry point,
/// asserted to the correct concrete type, checked via the core-side rendering extractors
/// (<see cref="IriExtensions"/>), and verified to survive a serialize round-trip without losing the
/// Mastodon extension fields (<c>atomUri</c>, <c>inReplyToAtomUri</c>, <c>contentMap</c>,
/// <c>conversation</c>, <c>webfinger</c>, …) — those land in <c>ExtensionData</c>.
/// </summary>
public class MastodonInteropRoundTripTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "InteropFixtures", name));

    /// <summary>
    /// A real Mastodon <c>Person</c> actor: <c>preferredUsername</c>/<c>name</c>/<c>summary</c>
    /// populate the typed properties; the single-valued <c>inbox</c>/<c>outbox</c>/<c>followers</c>
    /// resolve to <see cref="ILink"/>; and Mastodon extensions (<c>webfinger</c>, <c>featured</c>)
    /// survive the round-trip in <c>ExtensionData</c>.
    /// </summary>
    [Fact]
    public void Mastodon_PersonActor_DeserializesAndRoundTrips()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("mastodon-actor-person.json"))!;

        Assert.IsType<Person>(payload);
        var person = (Person)payload;

        Assert.Equal("https://mastodon.online/users/Gargron", person.Id);
        Assert.Equal("Gargron", person.PreferredUsername);
        Assert.Equal("Eugen (Personal)", person.Name!.First());
        Assert.NotNull(person.Summary);
        Assert.Contains("Developer of Mastodon", person.Summary!.First());

        // The single-valued actor endpoints resolve to Links.
        Assert.Equal("https://mastodon.online/users/Gargron/inbox", person.Inbox?.Href?.ToString());
        Assert.Equal("https://mastodon.online/users/Gargron/outbox", person.Outbox?.Href?.ToString());
        Assert.Equal("https://mastodon.online/users/Gargron/followers", person.Followers?.Href?.ToString());

        // Mastodon-specific extension fields are preserved (not dropped) on the round-trip.
        Assert.NotNull(person.ExtensionData);
        Assert.True(person.ExtensionData!.ContainsKey("webfinger"));
        Assert.Equal("Gargron@mastodon.online", person.ExtensionData["webfinger"].GetString());

        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("Person", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("Gargron", doc.RootElement.GetProperty("preferredUsername").GetString());
        Assert.Contains("webfinger", reserialized);
        Assert.Contains("featured", reserialized);
    }

    /// <summary>
    /// A real public Mastodon <c>Note</c> with a media <c>Document</c> attachment and a hashtag:
    /// <c>to</c> is the public AS URI, <c>cc</c> is the actor's followers, <c>sensitive</c> is
    /// false, the media resolves via <see cref="IriExtensions.GetRichAttachments"/>, the hashtag
    /// resolves via <see cref="IriExtensions.GetHashtagTags"/>, and the content is pre-rendered
    /// HTML (so the UI emits it verbatim, not through the Markdown renderer).
    /// </summary>
    [Fact]
    public void Mastodon_PublicNoteWithMediaAndHashtag_DeserializesAndExtracts()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("mastodon-note-108379641124250772.json"))!;

        Assert.IsType<Note>(payload);
        var note = (Note)payload;

        Assert.Equal("https://mastodon.online/users/Gargron/statuses/108379641124250772", note.Id);
        Assert.NotNull(note.Content);
        Assert.Contains("cats in wicker baskets", note.Content!.First());

        // Public visibility: the raw `to` carries the AS Public sentinel (the deserializer kept it),
        // and `GetAudienceIris()` deliberately filters that sentinel out (public is a visibility flag,
        // not a deliverable audience) while keeping the explicit `cc` followers collection.
        Assert.Contains(note.To!, ResolvePublicSentinel);
        var audience = note.GetAudienceIris();
        Assert.DoesNotContain(audience, i => i.Value == "https://www.w3.org/ns/activitystreams#Public");
        Assert.Contains(audience, i => i.Value == "https://mastodon.online/users/Gargron/followers");

        // Not sensitive (Mastodon sends explicit `sensitive: false`).
        Assert.False(note.IsSensitive());
        Assert.Null(note.GetSummary());

        // The media attachment (a `Document` with `mediaType: image/jpeg`) resolves to a rich URL.
        var attachments = note.GetRichAttachments();
        Assert.Single(attachments);
        Assert.StartsWith("https://", attachments[0].Url.Value);

        // The hashtag resolves to name + href. Mastodon stores the name WITH the `#` prefix
        // (`"#Caturday"`); the extractor surfaces it verbatim (the UI renders/strips as it chooses).
        var hashtags = note.GetHashtagTags();
        var caturday = hashtags.Single();
        Assert.Equal("#Caturday", caturday.Name);
        Assert.Equal("https://mastodon.online/tags/Caturday", caturday.Href?.Value);

        // Mastodon content is pre-rendered HTML (contains <p>/<a>), so the UI emits it verbatim.
        Assert.True(note.IsPreRenderedHtmlContent());

        // Mastodon extension fields survive the round-trip.
        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("atomUri", reserialized);
        Assert.Contains("inReplyToAtomUri", reserialized);
        Assert.Contains("contentMap", reserialized);
        Assert.Contains("conversation", reserialized);
    }

    /// <summary>
    /// A real followers-only, cross-instance Mastodon <c>Note</c> reply: <c>to</c> is the actor's
    /// followers (NOT public), <c>inReplyTo</c> points at a <c>kirakiratter.com</c> status, and the
    /// <c>kirakiratter.com</c> author is a <c>Mention</c> tag. The reply target and mention must both
    /// resolve so the UI can link the parent + the mentioned account.
    /// </summary>
    [Fact]
    public void Mastodon_FollowersOnlyCrossInstanceReply_DeserializesAndExtracts()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("mastodon-note-109235168212367317.json"))!;

        Assert.IsType<Note>(payload);
        var note = (Note)payload;

        // Followers-only: `to` is the followers collection, NOT the public AS URI.
        var audience = note.GetAudienceIris();
        Assert.Contains(audience, i => i.Value == "https://mastodon.online/users/Gargron/followers");
        Assert.DoesNotContain(audience, i => i.Value == "https://www.w3.org/ns/activitystreams#Public");

        // Cross-instance reply: the parent IRI is on kirakiratter.com. `inReplyTo` is a list in the
        // library; the first (only) entry is the parent.
        var parentIri = note.InReplyTo?.FirstOrDefault();
        Assert.NotNull(parentIri);
        Assert.Equal("https://kirakiratter.com/users/smartkittymomo/statuses/109235159758149546",
            ResolveIriString(parentIri!));

        // The cross-instance author is a Mention tag.
        var mentions = note.GetMentionIris();
        var mention = mentions.Single();
        Assert.Equal("https://kirakiratter.com/users/smartkittymomo", mention.Value);

        // Round-trip preserves the reply target.
        var reserialized = ActivityJson.Serialize(payload);
        Assert.Contains("kirakiratter.com", reserialized);
    }

    /// <summary>
    /// A real public Mastodon <c>Note</c> with two mentions (a cross-instance reply to
    /// <c>mastodon.social</c>): both mentions resolve, and the reply target resolves.
    /// </summary>
    [Fact]
    public void Mastodon_PublicNoteWithMultipleMentions_DeserializesAndExtracts()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("mastodon-note-109251632999281181.json"))!;

        Assert.IsType<Note>(payload);
        var note = (Note)payload;

        // Public visibility: raw `to` carries the AS Public sentinel (kept by the deserializer).
        Assert.Contains(note.To!, ResolvePublicSentinel);

        // Cross-instance reply to mastodon.social.
        var parentIri = note.InReplyTo?.FirstOrDefault();
        Assert.NotNull(parentIri);
        Assert.Equal("https://mastodon.social/users/shoq/statuses/109249016719721759",
            ResolveIriString(parentIri!));

        // Both mentions resolve (shoq + gnomon).
        var mentions = note.GetMentionIris();
        Assert.Equal(2, mentions.Count);
        Assert.Contains(mentions, i => i.Value == "https://mastodon.social/users/shoq");
        Assert.Contains(mentions, i => i.Value == "https://mastodon.social/users/gnomon");
    }

    /// <summary>
    /// An <c>inReplyTo</c>/<c>to</c>/<c>cc</c> entry is an <see cref="IObjectOrLink"/>; this helper
    /// extracts its IRI string whether it deserialized as a <see cref="Link"/> (<c>href</c>) or an
    /// <see cref="IObject"/> with an <c>id</c>.
    /// </summary>
    private static string ResolveIriString(IObjectOrLink orLink)
    {
        if (orLink is ILink link)
        {
            return link.Href?.ToString() ?? string.Empty;
        }

        if (orLink is IObject obj)
        {
            return obj.Id ?? string.Empty;
        }

        return string.Empty;
    }

    private const string PublicSentinel = "https://www.w3.org/ns/activitystreams#Public";

    private static bool ResolvePublicSentinel(IObjectOrLink orLink)
        => ResolveIriString(orLink) == PublicSentinel;
}
