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
    /// A real PieFed <c>Feed</c> (community) actor: it deserializes to a <see cref="Group"/> (Phase
    /// 86.1 maps the PieFed-specific <c>Feed</c> wire type to the standard AS <c>Group</c> class),
    /// with <c>preferredUsername</c>/<c>name</c> populated and the single-valued <c>outbox</c>
    /// resolving to a <see cref="ILink"/>. All fields survive a serialize round-trip, and the
    /// original <c>Feed</c> wire type is preserved on re-serialization (no data loss).
    /// </summary>
    [Fact]
    public void PleromaFamily_FeedCommunityActor_DeserializesAndRoundTrips()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("piefed-group-actor.json"))!;

        // The polymorphic converter now maps the PieFed-specific "Feed" wire type to the standard
        // Group class (Phase 86.1), so a PieFed community is recognized as a community by Iris's
        // `as Group` / `is Group` cast sites (the community stores, the community handlers).
        Assert.IsAssignableFrom<IObject>(payload);
        Assert.IsAssignableFrom<Group>(payload);
        var actor = (IObject)payload;

        Assert.Equal("https://piefed.social/f/piefed", actor.Id);

        // The core interop guarantee: the round-trip (deserialize -> serialize) preserves the wire
        // type ("Feed") + every Pleroma-family field — preferredUsername, name, outbox, moderators,
        // childFeeds, publicKey, endpoints. Even though the payload is now a Group (Phase 86.1), the
        // library preserves the original "Feed" Type on re-serialization, so Iris can ingest + re-emit
        // a real PieFed community actor without losing the non-standard wire type or any data.
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
