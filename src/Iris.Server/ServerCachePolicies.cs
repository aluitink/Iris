using Iris.Core;

namespace Iris.Server;

/// <summary>
/// The configurable TTLs for the server-side caches.
/// </summary>
/// <remarks>
/// When a property is null, the corresponding default from <see cref="CachePolicy"/> is used
/// (Resolved Decision #8: TTLs are configurable per-deployment; the defaults are the starting point).
/// </remarks>
public sealed class ServerCachePolicies
{
    /// <summary>
    /// The policy for cached remote actors (default: <see cref="CachePolicy.Actor"/>).
    /// </summary>
    public CachePolicy? RemoteActor { get; set; }

    /// <summary>
    /// The policy for cached remote public keys (default: <see cref="CachePolicy.Key"/>).
    /// </summary>
    public CachePolicy? RemoteKey { get; set; }

    /// <summary>
    /// The policy for cached collection pages (default: <see cref="CachePolicy.CollectionPage"/>).
    /// </summary>
    public CachePolicy? CollectionPage { get; set; }

    /// <summary>
    /// The policy for cached WebFinger lookups (default: <see cref="CachePolicy.WebFinger"/>).
    /// </summary>
    public CachePolicy? WebFinger { get; set; }
}
