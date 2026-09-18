namespace Iris.Core.Identity;

/// <summary>
/// A fragment-aware <see cref="IEqualityComparer{T}"/> for <see cref="Iri"/>: two IRIs are equal only
/// when their <see cref="Iri.Value"/> (the canonical absolute-URI string, which <em>includes</em> the
/// fragment) are equal, case-sensitively per RFC 3986.
/// </summary>
/// <remarks>
/// Since Phase 83.1, <see cref="Iri"/> itself is fragment-aware: its <see cref="Iri.Equals(Iri)"/>
/// compares <see cref="Iri.Value"/> (which includes the fragment), so a key IRI (<c>{actor}#key-1</c>)
/// is distinct from the bare actor IRI and from a different fragment (<c>{actor}#key-2</c>). This
/// comparer is now an <em>explicit</em> declaration of that same fragment-aware, <see cref="Iri.Value"/>-based
/// semantics — it is the comparer the in-memory and file-backed key stores (and the durable Postgres
/// store's equivalent) pass to their <c>Iri</c>-keyed dictionaries so the fragment-aware behavior is
/// documented at the call site and does not depend on the struct's default equality being chosen
/// correctly. Its behavior is identical to the default <see cref="Iri"/> equality.
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
