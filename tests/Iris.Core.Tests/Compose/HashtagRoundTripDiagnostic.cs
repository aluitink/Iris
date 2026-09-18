using System.Linq;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using Xunit;

namespace Iris.Core.Tests.Compose;

/// <summary>
/// 54.14 — verifies a <c>Hashtag</c> tag (the write path stores it as a generic ActivityStreams
/// <c>Object</c> whose <c>type</c> is the bare string <c>"Hashtag"</c> — the library serializes a
/// single-element <c>Type</c> list as a plain string) survives the object store's document round-trip
/// (<see cref="ActivityJson"/> <c>Serialize</c> -&gt; <c>Deserialize&lt;IObjectOrLink&gt;</c>, exactly what
/// <c>AsDocument</c> does for the jsonb column) and is read back by
/// <see cref="IriExtensions.GetHashtagTags"/>.
///
/// This guards a subtle deserializer quirk: a <c>tag</c> item whose <c>type</c> is an ARRAY lacking the
/// <c>"Object"</c> base (e.g. <c>["Hashtag"]</c>) deserializes to a null entry and is dropped on the
/// re-serialize, whereas the write path's bare-string <c>"Hashtag"</c> form reconstructs correctly.
/// </summary>
public class HashtagStoreRoundTripTests
{
    // The exact jsonb shape ComposeNote.Build / PostReplyAsync store: root type the bare string "Note",
    // the hashtag's type the bare string "Hashtag", name an array, href in the object's extension data.
    private const string StoredNoteShape =
        "{\"id\": \"https://example.org/n1\", \"type\": \"Note\", \"to\": \"https://www.w3.org/ns/activitystreams#Public\"," +
        "\"content\": \"Testing hashtag rendering #5414hash and a mention @bob\"," +
        "\"@context\": \"https://www.w3.org/ns/activitystreams\"," +
        "\"attributedTo\": \"https://example.org/u/alice\"," +
        "\"published\": \"2026-09-09T04:16:31.0000000Z\"," +
        "\"tag\": [{\"type\": \"Hashtag\", \"name\": [\"#5414hash\"], \"href\": \"https://example.org/search?q=%235414hash\"}]," +
        "\"url\": \"https://example.org/notes/5414hash\"}";

    [Fact]
    public void WritePathHashtag_Survives_StoreRoundTrip()
    {
        // Deserialize EXACTLY as the store does (AsDocument.Deserialize = ActivityJson.Deserialize<IObjectOrLink>).
        var loaded = ActivityJson.Deserialize<IObjectOrLink>(StoredNoteShape);
        var note = Assert.IsType<Note>(loaded);

        var tags = note.Tag?.ToList() ?? [];
        Assert.Single(tags);
        var hashtag = Assert.IsType<KristofferStrube.ActivityStreams.Object>(tags[0]);
        Assert.Contains("Hashtag", hashtag.Type!);
        Assert.Equal("#5414hash", hashtag.Name?.Single());

        // The re-serialization (what the AP object-document endpoint serves) keeps the hashtag tag.
        var reSerialized = ActivityJson.Serialize(note);
        Assert.Contains("\"Hashtag\"", reSerialized);
        Assert.Contains("5414hash", reSerialized);
    }

    [Fact]
    public void WritePathHashtag_RoundTrips_ThroughGetHashtagTags()
    {
        var loaded = ActivityJson.Deserialize<IObjectOrLink>(StoredNoteShape);
        var note = Assert.IsType<Note>(loaded);

        // GetHashtagTags reads the hashtag back (name + href from the extension data).
        var hashtags = note.GetHashtagTags();
        var tag = Assert.Single(hashtags);
        Assert.Equal("#5414hash", tag.Name);
        Assert.NotNull(tag.Href);
        Assert.Equal("https://example.org/search?q=%235414hash", tag.Href!.Value.Value);
    }
}
