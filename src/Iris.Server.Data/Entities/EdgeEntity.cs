namespace Iris.Server.Data.Entities;

/// <summary>
/// A generic directed edge <c>Source → Target</c> of a named <see cref="Kind"/>. One table backs every
/// relationship store (follows, likes, announces, replies, relays, community membership/follows, and the
/// community moderation edges), so adding a new relationship kind is a new enum value, not a new table.
/// </summary>
/// <remarks>
/// Each kind stores exactly one directed edge per (kind, source, target) — enforced by the unique index
/// on (<see cref="Kind"/>, <see cref="Source"/>, <see cref="Target"/>). The reverse index (e.g.
/// "who follows this actor") is a query on <see cref="Target"/>; a non-clustered index on
/// (<see cref="Kind"/>, <see cref="Target"/>) keeps that direction O(log n).
/// </remarks>
public sealed class EdgeEntity
{
    /// <summary>
    /// The kind of the edge.
    /// </summary>
    public EdgeKind Kind { get; set; }

    /// <summary>
    /// The source IRI (the follower, liker, announcer, replier, blocker, muter, flagger, or community).
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The target IRI (the followed actor, liked object, announced object, parent object, relay, or
    /// moderated actor).
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// When the edge was recorded (a stable sort key).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The kinds of directed relationship edge persisted in <see cref="EdgeEntity"/>.
/// </summary>
public enum EdgeKind
{
    /// <summary>
    /// A follow edge: follower → followed (the actor's followers/following collections).
    /// </summary>
    Follow = 0,

    /// <summary>
    /// A like edge: liker → liked object (the actor's liked collection + the object's likes reverse index).
    /// </summary>
    Like = 1,

    /// <summary>
    /// An announce (boost) edge: announcer → announced object (the actor's boosts + the object's shares
    /// reverse index).
    /// </summary>
    Announce = 2,

    /// <summary>
    /// A reply edge: parent object → reply object (an object's replies collection, F-12).
    /// </summary>
    Reply = 3,

    /// <summary>
    /// A relay-subscription edge: subscribing actor → relay (the actor's relays / star set, F-06).
    /// </summary>
    Relay = 4,

    /// <summary>
    /// A person block edge: blocker → blocked (the actor's blocks collection, F-07).
    /// </summary>
    Block = 5,

    /// <summary>
    /// A person flag edge: flagger → flagged (the actor's flags collection, F-07).
    /// </summary>
    Flag = 6,

    /// <summary>
    /// A person mute edge: muter → muted (the actor's mutes collection, F-07).
    /// </summary>
    Mute = 7,

    /// <summary>
    /// A community membership edge: community → member.
    /// </summary>
    CommunityMember = 8,

    /// <summary>
    /// A pending community join request: community → requesting actor (19.5.2).
    /// </summary>
    CommunityJoinRequest = 9,

    /// <summary>
    /// A community follow edge: community → followed actor (the community's federated feed, F-24).
    /// </summary>
    CommunityFollow = 10,

    /// <summary>
    /// A community follower edge: follower → community (the community's followers collection, F-24).
    /// </summary>
    CommunityFollower = 11,

    /// <summary>
    /// A community block edge: community → blocked actor (community moderation, 19.5.4).
    /// </summary>
    CommunityBlock = 12,

    /// <summary>
    /// A community flag edge: community → flagged actor (community moderation, 19.5.4).
    /// </summary>
    CommunityFlag = 13,

    /// <summary>
    /// A community mute edge: community → muted actor (community moderation, 19.5.4).
    /// </summary>
    CommunityMute = 14,

    /// <summary>
    /// A media source-URL index edge: external source URL → media id (the media proxy's client-facing
    /// key, Phase 20.4 (d)).
    /// </summary>
    MediaSourceUrl = 15,

    /// <summary>
    /// A media content-hash dedupe edge: SHA-256 hex of the bytes → media id (the server-internal
    /// dedupe index, Phase 20.4 (d)).
    /// </summary>
    MediaContentHash = 16,
}
