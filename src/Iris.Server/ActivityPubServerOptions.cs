using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Options for an Iris ActivityPub server instance.
/// </summary>
/// <remarks>
/// The <see cref="NamespaceIri"/> is the configurable <c>iris:</c> namespace base (Resolved Decision
/// #9). The <see cref="InstanceName"/> is the human-readable name of the instance (used in NodeInfo
/// and the actor document's <c>name</c>). The <see cref="CachePolicies"/> override the default
/// <see cref="Iris.Core.CachePolicy"/> TTLs for the server-side caches (Resolved Decision #8).
/// </remarks>
public sealed class ActivityPubServerOptions
{
    /// <summary>
    /// The <c>iris:</c> namespace base IRI (e.g. <c>https://iris.example/ns#</c>).
    /// Defaults to a canonical Iris IRI when not set (see Resolved Decision #9).
    /// </summary>
    public Iri? NamespaceIri { get; set; }

    /// <summary>
    /// The human-readable instance name (e.g. <c>my-iris-instance</c>).
    /// Used in NodeInfo and as a default for actor <c>name</c>.
    /// </summary>
    public string? InstanceName { get; set; }

    /// <summary>
    /// The base URL of this instance (e.g. <c>https://a.domain.local</c>).
    /// Used to build absolute IRIs for local actors and in WebFinger.
    /// </summary>
    public Iri? BaseUri { get; set; }

    /// <summary>
    /// The server-side cache policies (override defaults). When null, the defaults from
    /// <see cref="Iris.Core.CachePolicy"/> are used.
    /// </summary>
    public ServerCachePolicies? CachePolicies { get; set; }
}
