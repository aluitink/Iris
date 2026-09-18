using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Tests;

/// <summary>
/// Interop round-trip tests for real Misskey wire format (Phase 81.2). The fixtures are genuine
/// documents captured from <c>misskey.io</c> (a live Misskey instance that permits unsigned
/// ActivityPub fetches) — full Misskey <c>@context</c> (mapping <c>Emoji</c> to <c>toot:Emoji</c> and
/// declaring the <c>misskey:</c>-namespaced extension terms), explicit <c>null</c>/<c>[]</c> values,
/// and a scalar <c>attributedTo</c>. They exercise the scenarios that distinguish Misskey from
/// Mastodon: a <c>Person</c> actor carrying <c>misskey:</c> extensions (and omitting <c>name</c>); a
/// minimal public <c>Note</c>; a <c>Note</c> whose <c>tag</c> array holds Misskey custom
/// <c>Emoji</c> objects (which Iris's hashtag extractor must NOT misclassify as hashtags); and an
/// <c>Announce</c> (renote) activity.
///
/// Each fixture is deserialized through the polymorphic <see cref="ActivityJson"/> entry point,
/// asserted to the correct concrete type, checked via the core-side extractors
/// (<see cref="IriExtensions"/>), and verified to survive a serialize round-trip without dropping the
/// <c>misskey:</c> extension fields (those land in <c>ExtensionData</c>).
/// </summary>
public class MisskeyInteropRoundTripTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "InteropFixtures", name));

    /// <summary>
    /// A real Misskey <c>Person</c> actor: <c>preferredUsername</c> populates, <c>name</c> is absent
    /// (Misskey omits it — the extractor must tolerate a null name), and the <c>misskey:</c>
    /// extensions (<c>_misskey_summary</c>, <c>isCat</c>, <c>image</c>, <c>sharedInbox</c>) survive
    /// the round-trip in <c>ExtensionData</c>.
    /// </summary>
    [Fact]
    public void Misskey_PersonActor_DeserializesAndRoundTrips()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("misskey-actor-person.json"))!;

        Assert.IsType<Person>(payload);
        var person = (Person)payload;

        Assert.Equal("https://misskey.io/users/7rkr40rk13", person.Id);
        Assert.Equal("misskey", person.PreferredUsername);
        // Misskey omits `name` — the property is null/empty, not a crash.
        Assert.True(person.Name is null || !person.Name.Any(),
            "Misskey omits `name`; Person.Name must be null or empty, not a value.");

        // The single-valued actor endpoints resolve to Links.
        Assert.Equal("https://misskey.io/users/7rkr40rk13/inbox", person.Inbox?.Href?.ToString());
        Assert.Equal("https://misskey.io/users/7rkr40rk13/outbox", person.Outbox?.Href?.ToString());

        // Misskey-specific extension fields are preserved (not dropped) on the round-trip.
        Assert.NotNull(person.ExtensionData);
        Assert.True(person.ExtensionData!.ContainsKey("isCat"), "Misskey `isCat` extension should be preserved.");
        Assert.True(person.ExtensionData.ContainsKey("_misskey_summary"), "Misskey `_misskey_summary` extension should be preserved.");

        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("Person", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("misskey", doc.RootElement.GetProperty("preferredUsername").GetString());
        Assert.Contains("isCat", reserialized);
        Assert.Contains("_misskey_summary", reserialized);
    }

    /// <summary>
    /// A real Misskey <c>Note</c> whose <c>tag</c> array holds custom <c>Emoji</c> objects (not
    /// mentions or hashtags): the note deserializes to a <see cref="Note"/>, the content is
    /// pre-rendered HTML, and — critically — <see cref="IriExtensions.GetHashtagTags"/> returns
    /// <em>no</em> hashtags (the <c>Emoji</c> tags must not be misclassified as hashtags). The
    /// Emoji tags survive the round-trip in <c>Tag</c>.
    /// </summary>
    [Fact]
    public void Misskey_NoteWithCustomEmojiTags_IsNotMisclassifiedAsHashtags()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("misskey-note-tags.json"))!;

        Assert.IsType<Note>(payload);
        var note = (Note)payload;

        Assert.Equal("https://misskey.io/notes/9dfvsv623j", note.Id);
        Assert.NotNull(note.Content);
        Assert.Contains(":stat_sake:", note.Content!.First());

        // The two tags are custom Emojis, NOT hashtags — the hashtag extractor must return empty.
        var hashtags = note.GetHashtagTags();
        Assert.Empty(hashtags);

        // The tags themselves are present (two Emoji objects) and survive the round-trip.
        Assert.NotNull(note.Tag);
        Assert.Equal(2, note.Tag!.Count());
        var reserialized = ActivityJson.Serialize(payload);
        Assert.Contains(":stat_sake:", reserialized);
        Assert.Contains(":yosano_party:", reserialized);

        // Misskey content is pre-rendered HTML (contains <p>/<span>).
        Assert.True(note.IsPreRenderedHtmlContent());
    }

    /// <summary>
    /// A real Misskey <c>Announce</c> (renote) activity: it deserializes to an
    /// <see cref="Announce"/>, and both the <c>actor</c> (the re-noting user) and the <c>object</c>
    /// (the re-noted note IRI) resolve — the two fields Iris's announce handler depends on to store +
    /// propagate a boost.
    /// </summary>
    [Fact]
    public void Misskey_Announce_Renote_DeserializesAndResolvesActorAndObject()
    {
        // Deserialize through the polymorphic converter (IObjectOrLink) and cast to Activity — the
        // established pattern for inbound activities (FileBackedDeliveryDeadLetterStore). This lets the
        // converter dispatch the "Announce" type to the concrete Announce class.
        var activity = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("misskey-announce-renote.json")) as Activity;

        Assert.NotNull(activity);
        Assert.IsType<Announce>(activity);
        var announce = (Announce)activity!;

        // The re-noting actor resolves to the Misskey user IRI.
        var actorIri = announce.Actor?.FirstOrDefault()?.ResolveObjectIri();
        Assert.NotNull(actorIri);
        Assert.Equal("https://misskey.io/users/9bdcydhst5", actorIri?.Value);

        // The re-noted object resolves to the Misskey note IRI.
        var objectIri = announce.Object?.FirstOrDefault()?.ResolveObjectIri();
        Assert.NotNull(objectIri);
        Assert.Equal("https://misskey.io/notes/9dfck93uyh", objectIri?.Value);

        // Round-trip preserves the activity type + both IRIs.
        var reserialized = ActivityJson.Serialize(activity);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("Announce", doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("9dfck93uyh", reserialized);
        Assert.Contains("9bdcydhst5", reserialized);
    }

    /// <summary>
    /// A real minimal public Misskey <c>Note</c>: it deserializes to a <see cref="Note"/>, is not
    /// sensitive, carries the public audience in <c>to</c> (filtered out of
    /// <see cref="IriExtensions.GetAudienceIris"/>, the designed contract), and round-trips.
    /// </summary>
    [Fact]
    public void Misskey_BasicPublicNote_DeserializesAndRoundTrips()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("misskey-note-basic.json"))!;

        Assert.IsType<Note>(payload);
        var note = (Note)payload;

        Assert.Equal("https://misskey.io/notes/7roqp5rjfp", note.Id);
        Assert.NotNull(note.Content);
        Assert.False(note.IsSensitive());

        // Public visibility: raw `to` carries the AS Public sentinel; GetAudienceIris filters it.
        Assert.Contains(note.To!, l => ResolveIriString(l) == "https://www.w3.org/ns/activitystreams#Public");
        var audience = note.GetAudienceIris();
        Assert.DoesNotContain(audience, i => i.Value == "https://www.w3.org/ns/activitystreams#Public");

        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
    }

    /// <summary>
    /// An <c>actor</c>/<c>object</c>/<c>to</c> entry is an <see cref="IObjectOrLink"/>; this helper
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
}
