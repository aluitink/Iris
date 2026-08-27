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
    /// The <c>iris:capabilities</c> extension property name (Resolved Decision #11).
    /// The full IRI is <c>{NamespaceIri}capabilities</c>; this is the local term.
    /// </summary>
    public const string CapabilitiesTerm = "capabilities";

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
}
