using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Persistance;

/// <summary>
/// Phase 86.1 integration tests: the PieFed <c>Feed</c> → standard <c>Group</c> community-type
/// mapping. PieFed (a Pleroma fork) emits <c>"type":"Feed"</c> for its community/group actor where
/// standard ActivityPub/Pleroma emits <c>"type":"Group"</c>. Before 86.1 a <c>Feed</c> document
/// deserialized to a generic <c>Object</c>, so every <c>as Group</c> / <c>is Group</c> cast site
/// (the community stores, the community Update/Create handlers) failed to recognize it as a
/// community. After 86.1 the polymorphic converter materializes a <c>Feed</c> actor as a
/// <see cref="Group"/>, and the <see cref="FileBackedCommunityStore"/> round-trips it — a stored
/// <c>Feed</c> community is found by <see cref="ICommunityStore.TryGetCommunityAsync"/> and re-emits
/// its original <c>Feed</c> wire type (no data loss).
/// </summary>
public sealed class FeedCommunityMappingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("iris-feed-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// A genuine PieFed community actor document: <c>"type":"Feed"</c> with the Pleroma-family
    /// <c>moderators</c>, <c>childFeeds</c>, <c>publicKey</c>, and <c>endpoints.sharedInbox</c> fields.
    /// Mirrors the captured <c>piefed-group-actor.json</c> interop fixture.
    /// </summary>
    private const string FeedActorJson = """
        {
          "@context": ["https://www.w3.org/ns/activitystreams", "https://w3id.org/security/v1"],
          "id": "https://piefed.example/f/piefed",
          "type": "Feed",
          "preferredUsername": "piefed",
          "name": "PieFed",
          "inbox": "https://piefed.example/f/piefed/inbox",
          "outbox": "https://piefed.example/f/piefed/outbox",
          "followers": "https://piefed.example/f/piefed/followers",
          "following": "https://piefed.example/f/piefed/following",
          "moderators": "https://piefed.example/f/piefed/moderators",
          "childFeeds": [],
          "endpoints": { "sharedInbox": "https://piefed.example/inbox" },
          "publicKey": {
            "id": "https://piefed.example/f/piefed#main-key",
            "owner": "https://piefed.example/f/piefed",
            "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAoTZXtPutyXKt5GBb74U9\n-----END PUBLIC KEY-----\n"
          }
        }
        """;

    [Fact]
    public void FeedActor_Deserializes_ToGroup()
    {
        var payload = ActivityJson.Deserialize<IObjectOrLink>(FeedActorJson)!;

        // The polymorphic converter maps the PieFed "Feed" wire type to the standard Group class.
        Assert.IsAssignableFrom<Group>(payload);
        var group = (Group)payload!;
        Assert.Equal("https://piefed.example/f/piefed", group.Id);
        Assert.Equal("piefed", group.PreferredUsername);
    }

    [Fact]
    public void FeedActor_RoundTrips_PreservesFeedWireType()
    {
        var payload = ActivityJson.Deserialize<IObjectOrLink>(FeedActorJson)!;

        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);

        // Even though the payload is a Group, the library preserves the original "Feed" Type on
        // re-serialization, so a re-emitted PieFed community stays a Feed (no wire-type loss).
        Assert.Equal("Feed", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("piefed", doc.RootElement.GetProperty("preferredUsername").GetString());
        Assert.Contains("moderators", reserialized);
        Assert.Contains("childFeeds", reserialized);
        Assert.Contains("publicKey", reserialized);
        Assert.Contains("sharedInbox", reserialized);
    }

    [Fact]
    public async Task FileBackedCommunityStore_FeedDocument_RoundTrips_AsGroup()
    {
        var path = Path.Combine(_dir, "communities.json");
        var store = new FileBackedCommunityStore(path);
        var iri = new Iri("https://piefed.example/f/piefed");

        // Ingest a real PieFed Feed actor and persist it through the community store's write path.
        var group = ActivityJson.Deserialize<IObjectOrLink>(FeedActorJson) as Group;
        Assert.NotNull(group);
        await store.PutCommunityAsync(group!);

        // A re-read (simulating a process restart over the same file) finds the community as a Group.
        var found = await store.TryGetCommunityAsync(iri, out var retrieved);
        Assert.True(found);
        Assert.NotNull(retrieved);
        Assert.Equal("piefed", retrieved!.PreferredUsername);
        Assert.Equal("PieFed", retrieved.Name!.First());
    }

    [Fact]
    public async Task FileBackedCommunityStore_FeedDocument_SurvivesRestart()
    {
        var path = Path.Combine(_dir, "communities-restart.json");
        var iri = new Iri("https://piefed.example/f/piefed");

        // Process 1: ingest + persist the Feed community.
        var store1 = new FileBackedCommunityStore(path);
        var group = ActivityJson.Deserialize<IObjectOrLink>(FeedActorJson) as Group;
        await store1.PutCommunityAsync(group!);
        store1.Dispose();

        // Process 2: a fresh store over the same file replays the state. The Feed community is found
        // as a Group and re-emits its original Feed wire type.
        var store2 = new FileBackedCommunityStore(path);
        var found = await store2.TryGetCommunityAsync(iri, out var retrieved);
        Assert.True(found);
        Assert.NotNull(retrieved);

        var reserialized = ActivityJson.Serialize(retrieved!);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("Feed", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task FileBackedCommunityStore_ListsFeedCommunity()
    {
        var path = Path.Combine(_dir, "communities-list.json");
        var store = new FileBackedCommunityStore(path);
        var iri = new Iri("https://piefed.example/f/piefed");

        var group = ActivityJson.Deserialize<IObjectOrLink>(FeedActorJson) as Group;
        await store.PutCommunityAsync(group!);

        var all = await store.GetAllCommunityIrisAsync();
        Assert.Contains(iri, all);
    }
}
