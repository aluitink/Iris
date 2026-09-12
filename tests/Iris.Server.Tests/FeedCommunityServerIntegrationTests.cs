using System.Text.Json;
using Iris.Core;
using Iris.Server.Caching;
using Iris.Server.Delivery;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Iris.Server.Media;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Server-level integration tests for the <strong>PieFed <c>Feed</c> community mapping</strong>
/// (Phase 86.1): a PieFed community actor is served as <c>"type":"Feed"</c> where standard
/// Pleroma/ActivityPub emits <c>"type":"Group"</c>. Iris registers <c>"Feed" → Group</c> in the
/// polymorphic <see cref="ActivityJson"/> type registry so a <c>Feed</c> document deserializes to the
/// library's <see cref="Group"/> and is therefore recognized as a community everywhere a
/// <c>Group</c> is. These tests drive a <em>real</em> PieFed <c>Feed</c> document (the captured
/// <c>piefed-group-actor.json</c> fixture) through the server's community handlers and stores and
/// assert it flows correctly at every seam the mapping touches:
/// <list type="number">
/// <item>The <c>Feed</c> document deserializes to a <see cref="Group"/> (the polymorphic converter
/// recognizes the <c>Feed</c> type and materializes the concrete <c>Group</c>).</item>
/// <item>A <c>Feed</c> community stored and re-read through the <see cref="ICommunityStore"/> round-trips
/// as a <see cref="Group"/> (the store's <c>as Group</c> cast on read succeeds for a <c>Feed</c> doc —
/// the seam the file-backed and EF community stores rely on).</item>
/// <item>An inbound <see cref="Follow"/> addressed to a <c>Feed</c> community is recognized as a
/// community follow: the follow/follower edges are recorded in the community's follows/followers sets,
/// the follow is surfaced in the community's outbox, and an <see cref="Accept"/> is scheduled back to
/// the follower (the <see cref="FollowActivityHandler"/> community branch, gated on
/// <c>TryGetCommunityAsync</c> recognizing the <c>Feed</c> community).</item>
/// <item>The wire-level cast sites: a real <c>Feed</c> document, deserialized through the polymorphic
/// converter, matches every <c>as Group</c> / <c>is Group</c> / <c>is not Group</c> site the mapping
/// fixes, and a <c>Feed</c> document stored + re-read round-trips as a <see cref="Group"/> with its
/// original <c>Feed</c> wire type preserved (the store's <c>as Group</c> read cast for a persisted
/// <c>Feed</c> community).</item>
/// </list>
/// These are the server-side counterparts of the core-level mapping test
/// (<c>PleromaFamilyInteropRoundTripTests.PleromaFamily_FeedCommunityActor_DeserializesAndRoundTrips</c>)
/// and the store-level mapping test (<c>FeedCommunityMappingTests</c>); together they prove a PieFed
/// community is a first-class citizen of the server's community pipeline.
/// </summary>
public sealed class FeedCommunityServerIntegrationTests
{
    // The PieFed community's IRI (as captured in the real fixture). The handler tests below treat it as
    // a LOCAL community (seeded into the in-memory community store) so the community branches fire; the
    // IRI value itself is the foreign host, which is what makes the test meaningful — the server
    // recognizes the *type* (Feed → Group), not the host.
    private static readonly Iri FeedCommunity = new("https://piefed.social/f/piefed");
    private static readonly Iri RemoteFollower = new("https://a.domain.local/ap/v1/u/alice");

    // A trimmed copy of the real captured fixture (tests/Iris.Core.Tests/InteropFixtures/
    // piefed-group-actor.json). The only field that matters for the mapping is "type":"Feed"; the rest
    // is included so the document is a faithful, realistic PieFed community (a full publicKey block, the
    // Pleroma-family moderators/childFeeds, the sharedInbox endpoint) rather than a bare stub. The
    // publicKeyPem is truncated to keep the test source compact — it is not parsed in these tests.
    private const string FeedCommunityJson = """
        {
          "@context": ["https://www.w3.org/ns/activitystreams", "https://w3id.org/security/v1"],
          "id": "https://piefed.social/f/piefed",
          "type": "Feed",
          "preferredUsername": "piefed",
          "name": "PieFed",
          "inbox": "https://piefed.social/f/piefed/inbox",
          "outbox": "https://piefed.social/f/piefed/outbox",
          "followers": "https://piefed.social/f/piefed/followers",
          "following": "https://piefed.social/f/piefed/following",
          "moderators": "https://piefed.social/f/piefed/moderators",
          "attributedTo": "https://piefed.social/f/piefed/moderators",
          "childFeeds": [],
          "publicKey": {
            "id": "https://piefed.social/f/piefed#main-key",
            "owner": "https://piefed.social/f/piefed",
            "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMIIBIjANBg...\n-----END PUBLIC KEY-----"
          },
          "endpoints": { "sharedInbox": "https://piefed.social/inbox" },
          "url": "https://piefed.social/f/piefed",
          "sensitive": false,
          "published": "2025-03-01T02:19:09.88Z",
          "updated": "2025-03-01T02:19:09.88Z"
        }
        """;

    // --- 1. Deserialization: a Feed document materializes as a Group ---------------------

    [Fact]
    public void FeedCommunityDocument_Deserializes_ToGroup()
    {
        var payload = ActivityJson.Deserialize<IObjectOrLink>(FeedCommunityJson)!;

        // The polymorphic converter maps "type":"Feed" -> Group (Phase 86.1). Without the registration
        // this would be a generic Object/Actor, not a Group, and every downstream cast would fail.
        Assert.IsAssignableFrom<Group>(payload);
        var group = (Group)payload;

        // The real PieFed fields survive the round-trip onto the Group.
        Assert.Equal(FeedCommunity.Value, group.Id);
        Assert.Equal("piefed", group.PreferredUsername);
        Assert.NotNull(group.Name);
        Assert.Contains("PieFed", group.Name);
    }

    // --- 2. Store round-trip: a Feed community stored as a Group is read back as a Group --

    [Fact]
    public async Task FeedCommunity_StoreRoundTrip_ReadsBackAsGroup()
    {
        var persistence = new InMemoryPersistenceProvider();

        // Deserialize the Feed document and seed it into the community store (as a Group).
        var group = (Group)ActivityJson.Deserialize<IObjectOrLink>(FeedCommunityJson)!;
        await persistence.Communities.PutCommunityAsync(group);

        // Read it back: the store's `as Group` cast must succeed for a Feed-typed doc, and the identity
        // must be preserved.
        Assert.True(await persistence.Communities.TryGetCommunityAsync(FeedCommunity, out var retrieved));
        Assert.NotNull(retrieved);
        Assert.Equal(FeedCommunity.Value, retrieved!.Id);
        Assert.Equal("piefed", retrieved.PreferredUsername);

        // The community is enumerable from the store (a Feed community is listed alongside any Group).
        var all = await persistence.Communities.GetAllCommunityIrisAsync();
        Assert.Contains(FeedCommunity, all);
    }

    // --- 3. Follow flow: an inbound Follow of a Feed community is recognized -------------

    [Fact]
    public async Task Follow_OfFeedCommunity_RecognizedAsCommunityFollow_RecordsEdgesAndSchedulesAccept()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedFeedCommunityAsync(persistence);
        var (handler, delivery) = BuildFollowHandler(persistence);
        var follow = BuildFollow(RemoteFollower, FeedCommunity);

        await handler.HandleAsync(new InboxDelivery(FeedCommunity, follow), follow);

        // The follow is recognized as a community follow: the community follows the follower (the follows
        // set, so the follower's content reaches the community's members) ...
        Assert.Contains(RemoteFollower, await persistence.Communities.GetFollowsAsync(FeedCommunity));
        // ... and the follower follows the community (the followers set, F-24), so the community's
        // `followers` collection lists the follower ...
        Assert.Contains(RemoteFollower, await persistence.Communities.GetFollowersAsync(FeedCommunity));
        // ... and an Accept is scheduled back to the follower's inbox, signed as the community.
        var job = Assert.Single(delivery.Delivered);
        Assert.IsType<Accept>(job.Activity);
        Assert.Equal(FeedCommunity, job.ActorIri);
        Assert.Equal(RemoteFollower.InboxOf(), job.InboxIri);
    }

    [Fact]
    public async Task Follow_OfFeedCommunity_InboundFollowLandsInCommunityOutbox()
    {
        // An inbound follow of a Feed community is surfaced in the community's own outbox, so a UI can
        // enumerate it and offer the operator an Accept/Reject (the community's "Inbound follows"
        // surface). This exercises the same `TryGetCommunityAsync` recognition as the edge-recording test
        // but asserts the outbox side effect.
        var persistence = new InMemoryPersistenceProvider();
        await SeedFeedCommunityAsync(persistence);
        var (handler, _) = BuildFollowHandler(persistence);
        var follow = BuildFollow(RemoteFollower, FeedCommunity);

        await handler.HandleAsync(new InboxDelivery(FeedCommunity, follow), follow);

        var outbox = await persistence.Activities.GetOutboxAsync(FeedCommunity);
        Assert.Contains(outbox, a => a.Id == follow.Id);
    }

    // --- 4. Update flow: an inbound Update of a Feed community profile is merged ---------

    [Fact]
    public void FeedCommunity_WireDeserialization_MatchesGroupCastSites()
    {
        // The genuine proof of the mapping: a real "type":"Feed" document, run through the polymorphic
        // deserializer, must match every cast/pattern site the mapping fixes. Without the
        // "Feed" -> Group registration the deserializer would produce a generic Object and each of
        // these would be null / not-match. (A handler test that constructs a Group in code would NOT
        // prove this: Group : Actor, so the handler's `is Actor` dispatch fires for a Group regardless
        // of its wire type. The wire is where the type is decided.)
        var payload = ActivityJson.Deserialize<IObjectOrLink>(FeedCommunityJson)!;

        Assert.IsAssignableFrom<Group>(payload);            // `as Group` (FileBacked/EfCommunityStore read)
        Assert.True(payload is Group);                     // `is Group` (UpdateActivityHandler, Create path)
        Assert.False(payload is not Group);                // `is not Group` (community Update merge guard)
        Assert.Contains(ActivityJson.FeedCommunityType, ((IObject)payload).Type!); // wire type preserved as "Feed"
    }

    [Fact]
    public async Task FeedCommunity_WireDocument_StoredAndReadBack_AsGroup()
    {
        // The genuine wire path end-to-end: a Feed-typed document is deserialized (the mapping decides
        // it is a Group), stored through the community store's write path, and re-read (as a fresh
        // store would on restart) — the store's `as Group` cast on the persisted JSON must succeed, i.e.
        // a persisted Feed community is a first-class community, not "not found".
        var persistence = new InMemoryPersistenceProvider();
        var group = (Group)ActivityJson.Deserialize<IObjectOrLink>(FeedCommunityJson)!;
        await persistence.Communities.PutCommunityAsync(group);

        Assert.True(await persistence.Communities.TryGetCommunityAsync(FeedCommunity, out var retrieved));
        Assert.NotNull(retrieved);
        Assert.IsAssignableFrom<Group>(retrieved);
        Assert.Equal(FeedCommunity.Value, retrieved!.Id);
        Assert.Equal("piefed", retrieved.PreferredUsername);

        // The persisted document still carries the "Feed" wire type (no wire-type loss on the round-trip).
        var persistedJson = ActivityJson.Serialize(retrieved);
        using var doc = JsonDocument.Parse(persistedJson);
        Assert.Equal("Feed", doc.RootElement.GetProperty("type").GetString());
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Deserializes the PieFed <c>Feed</c> fixture and seeds it into the community store as a
    /// <see cref="Group"/>. This is the canonical way a test puts a <em>Feed-typed</em> (not
    /// <c>Group</c>-typed) community into the server so the handlers recognize it via
    /// <c>TryGetCommunityAsync</c>.
    /// </summary>
    private static Task SeedFeedCommunityAsync(IPersistenceProvider persistence)
    {
        var group = (Group)ActivityJson.Deserialize<IObjectOrLink>(FeedCommunityJson)!;
        return persistence.Communities.PutCommunityAsync(group);
    }

    private static (FollowActivityHandler Handler, RecordingDeliveryService Delivery) BuildFollowHandler(
        IPersistenceProvider persistence)
    {
        var delivery = new RecordingDeliveryService();
        var handler = new FollowActivityHandler(
            persistence, delivery, new DefaultLocalActorResolver(persistence), new IdMinter());
        return (handler, delivery);
    }

    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Id = $"https://{new Uri(followerIri.Value).Host}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// An <see cref="IDeliveryService"/> that records every scheduled delivery (instead of enqueuing) so
    /// a test can assert on <see cref="Delivered"/> — the target inbox, the activity, and the signing
    /// actor.
    /// </summary>
    private sealed class RecordingDeliveryService : IDeliveryService
    {
        public List<DeliveryJob> Delivered { get; } = [];

        public Task DeliverAsync(Iri inboxIri, Activity activity, CancellationToken ct = default)
            => DeliverAsync(inboxIri, activity, actorIri: null, ct);

        public Task DeliverAsync(Iri inboxIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
        {
            Delivered.Add(new DeliveryJob(inboxIri, activity, actorIri));
            return Task.CompletedTask;
        }

        public Task DeliverToActorAsync(Iri recipientIri, Activity activity, CancellationToken ct = default)
            => DeliverToActorAsync(recipientIri, activity, actorIri: null, ct);

        public Task DeliverToActorAsync(Iri recipientIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
            => DeliverAsync(recipientIri.InboxOf(), activity, actorIri, ct);
    }

}
