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

    /// <summary>
    /// The IRI of the local actor the instance signs outbound federation requests as (actor-document
    /// fetches for inbound signature validation, and Phase 4 activity delivery). Defaults to the
    /// instance's first registered actor when not set. See Resolved Decision #27.
    /// </summary>
    public Iri? InstanceActorId { get; set; }

    /// <summary>
    /// The proxy-fallback settings (Phase 6) for the <c>POST /ap/v1/proxy/{target}</c> endpoint: the
    /// target allowlist and the per-actor rate limit. When null, the defaults apply: an empty
    /// allowlist (every target is allowed) and <see cref="ActivityPubServerConstants.DefaultProxyMaxRequestsPerMinute"/>
    /// requests per actor per minute.
    /// </summary>
    public ProxySettings? ProxySettings { get; set; }
}

/// <summary>
/// Settings for the proxy-fallback endpoint (<c>POST /ap/v1/proxy/{target}</c>, Phase 6).
/// </summary>
/// <remarks>
/// The proxy signs an authenticated actor's requests with that actor's own key and forwards them to
/// an arbitrary target, so it is a powerful capability and is bounded by two independent policies:
/// a <strong>target allowlist</strong> (which hosts may be proxied) and a <strong>per-actor rate
/// limit</strong> (how often a given actor may use the proxy). Both default to permissive (empty
/// allowlist = all targets allowed; <see cref="ActivityPubServerConstants.DefaultProxyMaxRequestsPerMinute"/>
/// /minute) so a host that does not configure <see cref="ActivityPubServerOptions.ProxySettings"/>
/// gets a working proxy out of the box, and a production host tightens the allowlist.
/// </remarks>
public sealed class ProxySettings
{
    /// <summary>
    /// The hostnames an authenticated actor may proxy to (e.g. <c>b.domain.local</c>). When empty (the
    /// default), every target host is allowed. Matching is case-insensitive and exact (no wildcards).
    /// </summary>
    public IReadOnlyCollection<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// The maximum number of proxy requests a single actor may issue per minute. Defaults to
    /// <see cref="ActivityPubServerConstants.DefaultProxyMaxRequestsPerMinute"/>.
    /// </summary>
    public int MaxRequestsPerMinute { get; set; } = ActivityPubServerConstants.DefaultProxyMaxRequestsPerMinute;
}
