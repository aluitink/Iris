namespace Iris.Client;

/// <summary>
/// Route constants for the <strong>local, non-federated moderation</strong> surface (a mute, F-07, and a
/// relay subscription, F-06).
/// </summary>
/// <remarks>
/// A mute and a relay subscription are not ActivityStreams activities, so they are not part of the
/// <c>/ap/v1</c> ActivityPub route tree (which is reserved for the AP protocol: outbox/inbox, object and
/// collection documents, search, and the specialized proxy relay). They live under a dedicated
/// <see cref="LocalRoutePrefix"/> tree on the same host, keyed by the acting actor's (or community's)
/// path, so the <c>/ap/v1</c> POST surface stays 100% AP. The corresponding *reads* (the actor's
/// <c>mutes</c>/<c>relays</c> collections) remain on <c>/ap/v1</c> because they are ordinary
/// ActivityStreams collection reads.
/// </remarks>
public static class LocalModerationConstants
{
    /// <summary>
    /// The route prefix for the local-moderation write tree: <c>/local/v1</c>. Mutes and relay
    /// subscriptions are mapped as <c>{LocalRoutePrefix}/u/{handle}/mutes/{target}</c>,
    /// <c>{LocalRoutePrefix}/u/{handle}/relays/{target}</c>, and
    /// <c>{LocalRoutePrefix}/c/{name}/mutes/{target}</c>.
    /// </summary>
    public const string LocalRoutePrefix = "/local/v1";

    /// <summary>
    /// The route segment for a person actor's path under <see cref="LocalRoutePrefix"/>.
    /// </summary>
    public const string ActorSegment = "u";

    /// <summary>
    /// The route segment for a community's path under <see cref="LocalRoutePrefix"/>.
    /// </summary>
    public const string CommunitySegment = "c";

    /// <summary>
    /// The mute-collection route segment (the local-moderation write path under an actor's local path).
    /// </summary>
    public const string MutesSegment = "mutes";

    /// <summary>
    /// The relay-collection route segment (the local-moderation write path under an actor's local path).
    /// </summary>
    public const string RelaysSegment = "relays";
}
