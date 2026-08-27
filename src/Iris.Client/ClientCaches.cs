namespace Iris.Client;

/// <summary>
/// The optional client-side caches an <see cref="ActivityPubClient"/> and <see cref="WebFingerClient"/>
/// consult before hitting the network.
/// </summary>
/// <remarks>
/// Each field is optional. When a cache is null the corresponding read path goes straight to the
/// network (no caching). Caches are shared across the client's lifetime; a caller that wants
/// per-request isolation supplies a fresh <see cref="ClientCaches"/> (or null). The
/// <see cref="CollectionPage"/> cache is used for <c>GetCollectionAsync</c> page fetches and honors
/// <see cref="CollectionQuery.BypassCache"/>.
/// </remarks>
public sealed record ClientCaches(
    ActorCache? Actors = null,
    CollectionPageCache? CollectionPages = null,
    WebFingerCache? WebFinger = null);
