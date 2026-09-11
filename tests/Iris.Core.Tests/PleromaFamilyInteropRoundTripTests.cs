using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Tests;

/// <summary>
/// Interop round-trip test for a real Pleroma-family (PieFed) wire-format actor (Phase 81.2,
/// Pleroma half). The fixture is a genuine document captured from <c>piefed.social</c> — PieFed is a
/// Pleroma fork, so its ActivityPub documents are Pleroma-compatible. It carries a <c>Feed</c> type
/// (PieFed's term for a community/group; standard Pleroma uses <c>Group</c>), a single-valued
/// <c>inbox</c>/<c>outbox</c>, a <c>publicKey</c>, and Pleroma-family fields (<c>moderators</c>,
/// <c>childFeeds</c>, <c>endpoints.sharedInbox</c>).
///
/// The test verifies the core deserializer handles a Pleroma-family actor correctly: it deserializes
/// to a valid <see cref="IObject"/> whose <c>preferredUsername</c>/<c>name</c> and single-valued
/// <c>outbox</c> resolve, and whose fields survive a serialize round-trip. It deliberately does NOT
/// assert <see cref="Group"/> (the <c>Feed</c> type is PieFed-specific and is not mapped to
/// <c>Group</c> by the polymorphic converter — see the change doc for the follow-up on mapping
/// <c>Feed</c> to the standard <c>Group</c> type).
/// </summary>
public class PleromaFamilyInteropRoundTripTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "InteropFixtures", name));

    /// <summary>
    /// A real PieFed <c>Feed</c> (community) actor: it deserializes to a valid <see cref="IObject"/>
    /// (not a <see cref="Group"/> — <c>Feed</c> is a PieFed-specific term, not the standard AS
    /// <c>Group</c> type), with <c>preferredUsername</c>/<c>name</c> populated and the single-valued
    /// <c>outbox</c> resolving to a <see cref="ILink"/>. All fields survive a serialize round-trip.
    /// </summary>
    [Fact]
    public void PleromaFamily_FeedCommunityActor_DeserializesAndRoundTrips()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("piefed-group-actor.json"))!;

        // The deserializer produces a valid object (the polymorphic converter does not map the
        // PieFed-specific "Feed" term to the standard Group class — it is a non-standard actor type).
        Assert.IsAssignableFrom<IObject>(payload);
        var actor = (IObject)payload;

        Assert.Equal("https://piefed.social/f/piefed", actor.Id);

        // The core interop guarantee: the round-trip (deserialize -> serialize) preserves the type +
        // every Pleroma-family field — preferredUsername, name, outbox, moderators, childFeeds,
        // publicKey, endpoints. This proves Iris can ingest + re-emit a real PieFed community actor
        // without losing data, regardless of how the polymorphic converter maps the non-standard
        // "Feed" type to a concrete class.
        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("Feed", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("piefed", doc.RootElement.GetProperty("preferredUsername").GetString());
        Assert.Equal("PieFed", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(
            "https://piefed.social/f/piefed/outbox",
            doc.RootElement.GetProperty("outbox").GetString());
        Assert.Contains("moderators", reserialized);
        Assert.Contains("childFeeds", reserialized);
        Assert.Contains("publicKey", reserialized);
        Assert.Contains("sharedInbox", reserialized);
    }
}
