using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Tests;

/// <summary>
/// Interop round-trip tests for foreign ActivityStreams implementations (Phase 79.3): a PeerTube
/// <c>Video</c> object (whose body IS the media) and a Pleroma <c>Note</c> (with Pleroma-specific
/// extension fields). Both must deserialize through the polymorphic converter and survive a
/// serialize round-trip without losing fields.
/// </summary>
public class ForeignInteropRoundTripTests
{
    /// <summary>
    /// A PeerTube video post: a single <c>Video</c> object whose <c>url</c> points at the media file
    /// and <c>preview</c> at the thumbnail. The <c>name</c>/<c>content</c> map to the typed
    /// properties; the object's own media IRI must be resolvable via
    /// <see cref="IriExtensions.GetSelfMediaIri"/> so the UI can emit a player.
    /// </summary>
    [Fact]
    public void PeerTube_VideoObject_DeserializesAndSelfMediaIriResolves()
    {
        var json = """
            {
              "@context": ["https://join-lemmy.org/context.json", "https://www.w3.org/ns/activitystreams"],
              "type": "Video",
              "id": "https://peertube.example.org/videos/watch/abc",
              "name": "A PeerTube video",
              "content": "<p>Video description</p>",
              "duration": "PT10M",
              "url": "https://peertube.example.org/static/files/abc.webm",
              "preview": "https://peertube.example.org/thumbnails/abc.jpg",
              "attributedTo": "https://peertube.example.org/users/foo",
              "published": "2024-01-01T00:00:00Z"
            }
            """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;

        // The library maps the "Video" type to its concrete Video class.
        Assert.IsType<Video>(payload);
        var video = (Video)payload;
        Assert.Equal("https://peertube.example.org/videos/watch/abc", video.Id);
        Assert.Equal("A PeerTube video", video.Name!.First());
        Assert.Equal("<p>Video description</p>", video.Content!.First());

        // The object's own media IRI (the `url`) resolves so the UI can render a player.
        var selfMedia = video.GetSelfMediaIri();
        Assert.NotNull(selfMedia);
        Assert.Equal("https://peertube.example.org/static/files/abc.webm", selfMedia!.Value.Value);

        // The round-trip preserves the type and the self-media url.
        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("Video", doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("abc.webm", reserialized);
    }

    /// <summary>
    /// A non-media object (a plain note) must NOT report a self-media IRI — its media, if any, lives
    /// in <c>attachment</c> and is handled by <see cref="IriExtensions.GetRichAttachments"/>.
    /// </summary>
    [Fact]
    public void Plain_Note_HasNoSelfMediaIri()
    {
        var json = """
            {
              "@context": "https://www.w3.org/ns/activitystreams",
              "type": "Note",
              "id": "https://a.domain.local/n/1",
              "content": "<p>hi</p>",
              "url": "https://a.domain.local/n/1"
            }
            """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;

        Assert.IsType<Note>(payload);
        Assert.Null(((IObject)payload).GetSelfMediaIri());
    }

    /// <summary>
    /// A Pleroma note carries Pleroma-specific fields (<c>conversationId</c>, <c>emoji</c>,
    /// <c>toot</c>, <c>pleroma</c>). None of these are typed on the Iris model; they must survive the
    /// serialize round-trip through the ActivityStreams <c>ExtensionData</c> bag.
    /// </summary>
    [Fact]
    public void Pleroma_Note_ExtensionFieldsRoundTrip()
    {
        var json = """
            {
              "@context": ["https://join-lemmy.org/context.json", "https://www.w3.org/ns/activitystreams"],
              "type": "Note",
              "id": "https://pleroma.example.org/objects/xyz",
              "content": "<p>hi</p>",
              "source": {"content": "hi", "mediaType": "text/markdown"},
              "conversationId": "https://pleroma.example.org/objects/root",
              "emoji": [{"name": "cat", "imageUrl": "https://pleroma.example.org/emoji/cat.png", "shortCode": ":cat:"}],
              "toot": {"emoji": []},
              "pleroma": {"local": true},
              "attributedTo": "https://pleroma.example.org/users/foo"
            }
            """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;

        Assert.IsType<Note>(payload);

        var reserialized = ActivityJson.Serialize(payload);
        Assert.Contains("conversationId", reserialized);
        Assert.Contains("pleroma", reserialized);
        Assert.Contains(":cat:", reserialized);
    }
}
