namespace Iris.Server;

/// <summary>
/// Wire-format constants for the Iris server (route prefix, meta headers, content types).
/// </summary>
/// <remarks>
/// No magic strings (CODING_STYLE §"General C# Conventions"). The route prefix is the authoritative
/// versioning mechanism (Resolved Decision #10); the <c>Iris-Version</c> header is meta information
/// for observability/interop.
/// </remarks>
public static class ActivityPubServerConstants
{
    /// <summary>
    /// The versioned route prefix for ActivityPub endpoints (e.g. <c>/ap/v1</c>).
    /// New major versions add a new prefix; existing prefixes stay stable (Resolved Decision #10).
    /// </summary>
    public const string RoutePrefix = "/ap/v1";

    /// <summary>
    /// The name of the meta version header emitted on responses.
    /// </summary>
    public const string VersionHeaderName = "Iris-Version";

    /// <summary>
    /// The current Iris API version (the value of <see cref="VersionHeaderName"/>).
    /// </summary>
    public const string ApiVersion = "1";

    /// <summary>
    /// The name of the non-standard extension property that carries the owner-only PEM private key
    /// on the authenticated actor document (Resolved Decision #2).
    /// </summary>
    public const string PrivateKeyExtensionName = "privateKey";

    /// <summary>
    /// The name of the non-standard extension property that carries the key algorithm label
    /// (<c>rsa</c> / <c>ecdsa-p256</c>) alongside <see cref="PrivateKeyExtensionName"/>
    /// (Resolved Decision #20).
    /// </summary>
    public const string KeyAlgorithmExtensionName = "keyAlgorithm";

    /// <summary>
    /// The value of <see cref="KeyAlgorithmExtensionName"/> for an RSA key.
    /// </summary>
    public const string KeyAlgorithmRsa = "rsa";

    /// <summary>
    /// The value of <see cref="KeyAlgorithmExtensionName"/> for an EC P-256 key.
    /// </summary>
    public const string KeyAlgorithmEcP256 = "ecdsa-p256";

    /// <summary>
    /// The value of <see cref="KeyAlgorithmExtensionName"/> for an Ed25519 key.
    /// </summary>
    public const string KeyAlgorithmEd25519 = "ed25519";

    /// <summary>
    /// The ActivityPub actor property <c>manuallyApprovesFollowers</c>: when <c>true</c> on a local
    /// actor, an inbound follow is <em>not</em> auto-accepted — the actor (operator) must respond with an
    /// explicit <c>Accept</c> or <c>Reject</c>. The library's <c>Actor</c> type does not model this
    /// property, so it is carried in the actor's <see cref="KristofferStrube.ActivityStreams.Object.ExtensionData"/>
    /// (seeded by the host) and echoed onto the public actor document (Resolved Decision #46).
    /// </summary>
    public const string ManuallyApprovesFollowersExtensionName = "manuallyApprovesFollowers";

    /// <summary>
    /// The <c>iris:capabilities</c> extension property name (Resolved Decision #11).
    /// The full IRI is <c>{NamespaceIri}capabilities</c>; this is the local term.
    /// </summary>
    public const string CapabilitiesTerm = "capabilities";

    /// <summary>
    /// The canonical default <c>iris:</c> namespace base IRI (Resolved Decision #9; Open Question #1
    /// resolved) used when a deployment does not override <see cref="ActivityPubServerOptions.NamespaceIri"/>.
    /// The base is configurable per-deployment; this is the out-of-the-box default.
    /// </summary>
    public const string DefaultCapabilitiesNamespaceIri = "https://iris.example/ns#";

    /// <summary>
    /// The capability value advertised for the community's unified feed (the
    /// <c>GET /c/{name}/feed</c> specialized collection).
    /// </summary>
    public const string CapabilityFeed = "feed";

    /// <summary>
    /// The capability value advertised for the community's members collection (the
    /// <c>GET /c/{name}/members</c> endpoint).
    /// </summary>
    public const string CapabilityMembers = "members";

    /// <summary>
    /// The capability value advertised for the community's search (the
    /// <c>GET /c/{name}/search</c> specialized collection).
    /// </summary>
    public const string CapabilitySearch = "search";

    /// <summary>
    /// The name of the HTTP caching directive header emitted on cacheable responses.
    /// </summary>
    public const string CacheControlHeaderName = "Cache-Control";

    /// <summary>
    /// The query parameter name that forces a cache bypass on cached endpoints (ARCHITECTURE.md:
    /// <c>?refresh=true</c>).
    /// </summary>
    public const string RefreshQueryParameterName = "refresh";

    /// <summary>
    /// The <c>Cache-Control</c> value for cacheable actor documents (ARCHITECTURE.md:
    /// <c>max-age=60, stale-while-revalidate=300</c>).
    /// </summary>
    public const string ActorCacheControl = "max-age=60, stale-while-revalidate=300";

    /// <summary>
    /// The <c>Cache-Control</c> value for responses that must not be stored (owner-only / private data).
    /// </summary>
    public const string NoStoreCacheControl = "no-store";

    /// <summary>
    /// The <c>Cache-Control</c> value emitted when a <c>?refresh=true</c> bypass is honored (the response
    /// was re-fetched; intermediates must not serve a stale copy).
    /// </summary>
    public const string NoCacheCacheControl = "no-cache";

    /// <summary>
    /// The default page size for paged collection endpoints (<c>outbox</c>/<c>followers</c>/<c>following</c>)
    /// when the request does not supply a <c>?limit</c>.
    /// </summary>
    public const int DefaultCollectionPageSize = 20;

    /// <summary>
    /// The maximum <c>?limit</c> honored by paged collection endpoints (bounds a single page's size).
    /// </summary>
    public const int MaxCollectionPageSize = 100;

    /// <summary>
    /// The <c>Cache-Control</c> value for paged collection documents (mirrors the actor document:
    /// <c>max-age=60, stale-while-revalidate=300</c>).
    /// </summary>
    public const string CollectionCacheControl = "max-age=60, stale-while-revalidate=300";

    /// <summary>
    /// The query parameter name that carries a 0-based offset on the <c>GET /c/{name}/search</c>
    /// specialized collection (the shared <c>limit</c>/<c>offset</c> pagination shape, Resolved
    /// Decision #6).
    /// </summary>
    public const string OffsetQueryParameterName = "offset";

    /// <summary>
    /// The route segment for the proxy endpoint (the <c>POST /ap/v1/proxy/{target}</c> proxy
    /// fallback, Phase 6). Mapped as <c>{RoutePrefix}/proxy/{target}</c> — i.e. the proxy lives
    /// under the versioned prefix, like every other endpoint (Resolved Decision #10).
    /// </summary>
    public const string ProxyRouteSegment = "proxy";

    /// <summary>
    /// The default per-actor rate limit for the proxy endpoint (requests per minute) when
    /// <see cref="ActivityPubServerOptions.ProxySettings"/> does not override it.
    /// </summary>
    public const int DefaultProxyMaxRequestsPerMinute = 60;

    /// <summary>
    /// The route segment for the health-check endpoint (the <c>GET /ap/v1/health</c> observability
    /// endpoint, Phase 17). Mapped as <c>{RoutePrefix}/health</c> — i.e. the health endpoint lives under
    /// the versioned prefix, like every other endpoint (Resolved Decision #10).
    /// </summary>
    public const string HealthRouteSegment = "health";
}
