namespace Iris.Core.Identity;

/// <summary>
/// A fragment-aware <see cref="IEqualityComparer{T}"/> for <see cref="Iri"/>: two IRIs are equal only
/// when their <see cref="Iri.Value"/> (the canonical absolute-URI string, which <em>includes</em> the
/// fragment) are equal, case-sensitively per RFC 3986.
/// </summary>
/// <remarks>
/// <see cref="Iri"/> itself uses the default <see cref="System.Uri"/> equality, which is
/// fragment-<em>insensitive</em> by design (a URI fragment is not part of the resource identifier under
/// the W3C definition). That is the right default for most ActivityPub uses — the public-audience IRI
/// (<c>…#Public</c>) and collection IRIs are compared without regard to incidental fragment spelling.
/// But it is <em>wrong</em> for a key store: a key IRI's fragment (<c>{actor}#key-1</c>,
/// <c>{actor}#key-2</c>) is semantically significant — different fragments name different keys. With the
/// fragment-blind default, an <c>Iri</c>-keyed <c>Dictionary</c> conflates <c>#key-1</c> with
/// <c>#key-2</c> and with the bare actor IRI, so a key stored under one fragment is "found" for a
/// different fragment (a silent key-management corruption). The durable Postgres store
/// (<c>EfKeyStore</c>) already compares by <c>Iri.Value</c> (string-based, fragment-aware); this
/// comparer gives the in-memory and file-backed stores the same fragment-aware behavior.
/// </remarks>
public sealed class IriEqualityComparer : IEqualityComparer<Iri>
{
    /// <summary>
    /// The shared instance.
    /// </summary>
    public static readonly IriEqualityComparer Instance = new();

    /// <inheritdoc/>
    public bool Equals(Iri x, Iri y) => string.Equals(x.Value, y.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public int GetHashCode(Iri obj) => obj.Value.GetHashCode(StringComparison.Ordinal);
}
