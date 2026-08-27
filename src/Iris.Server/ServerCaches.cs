namespace Iris.Server;

/// <summary>
/// The server-side object caches the server consults before hitting the network during federation.
/// </summary>
/// <remarks>
/// Each field is optional. When a cache is null the corresponding read path goes straight to the
/// network (no caching). Caches are shared across the server's lifetime. The four caches cover the
/// remote object types the server fetches: remote actors, remote public keys, remote collection
/// pages, and WebFinger resolutions. These are populated by the server's outbound federation paths
/// (inbound signature validation, object delivery) in later phases; they are registered now so the
/// seam is in place and unit-testable.
/// </remarks>
public sealed record ServerCaches(
    RemoteActorCache? RemoteActors = null,
    RemoteKeyCache? RemoteKeys = null,
    CollectionPageCache? CollectionPages = null,
    WebFingerCache? WebFinger = null);
