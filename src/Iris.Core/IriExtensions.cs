using KristofferStrube.ActivityStreams;

namespace Iris.Core;

/// <summary>
/// Boundary-conversion and derivation helpers for <see cref="Iri"/>.
/// These are the only place Iris converts between the ActivityStreams library's
/// <c>string?</c>/<c>Uri?</c> identity representations and the <see cref="Iri"/> value type.
/// </summary>
public static class IriExtensions
{
    /// <summary>
    /// Derives the inbox IRI for an actor (or object) by appending <c>/inbox</c>.
    /// </summary>
    /// <param name="iri">The actor or object IRI. Must be absolute.</param>
    /// <returns>The inbox IRI (e.g. <c>https://a.domain.local/u/alice/inbox</c>).</returns>
    /// <exception cref="ArgumentException">When <paramref name="iri"/> is not absolute.</exception>
    public static Iri InboxOf(this Iri iri) => AppendSegment(iri, "inbox");

    /// <summary>
    /// Derives the outbox IRI for an actor (or object) by appending <c>/outbox</c>.
    /// </summary>
    /// <param name="iri">The actor or object IRI. Must be absolute.</param>
    /// <returns>The outbox IRI (e.g. <c>https://a.domain.local/u/alice/outbox</c>).</returns>
    /// <exception cref="ArgumentException">When <paramref name="iri"/> is not absolute.</exception>
    public static Iri OutboxOf(this Iri iri) => AppendSegment(iri, "outbox");

    /// <summary>
    /// Derives the followers-collection IRI by appending <c>/followers</c>.
    /// </summary>
    /// <param name="iri">The actor IRI. Must be absolute.</param>
    /// <returns>The followers IRI.</returns>
    /// <exception cref="ArgumentException">When <paramref name="iri"/> is not absolute.</exception>
    public static Iri FollowersOf(this Iri iri) => AppendSegment(iri, "followers");

    /// <summary>
    /// Derives the following-collection IRI by appending <c>/following</c>.
    /// </summary>
    /// <param name="iri">The actor IRI. Must be absolute.</param>
    /// <returns>The following-collection IRI.</returns>
    /// <exception cref="ArgumentException">When <paramref name="iri"/> is not absolute.</exception>
    public static Iri FollowingOf(this Iri iri) => AppendSegment(iri, "following");

    /// <summary>
    /// Derives the community-feed IRI by appending <c>/feed</c>.
    /// </summary>
    /// <param name="iri">The community IRI. Must be absolute.</param>
    /// <returns>The community-feed IRI (e.g. <c>https://a.domain.local/ap/v1/c/iris/feed</c>).</returns>
    /// <exception cref="ArgumentException">When <paramref name="iri"/> is not absolute.</exception>
    public static Iri FeedOf(this Iri iri) => AppendSegment(iri, "feed");

    /// <summary>
    /// Converts a library <c>string?</c> IRI (e.g. an object's <c>Id</c>) to an <see cref="Iri"/>.
    /// </summary>
    /// <param name="value">The IRI string. May be null.</param>
    /// <returns>The <see cref="Iri"/>, or <see langword="null"/> when <paramref name="value"/> is null or empty.</returns>
    public static Iri? ToIri(this string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Iri.TryParse(value, out var iri) ? iri : null;
    }

    /// <summary>
    /// Converts a library <c>Uri?</c> (e.g. a <see cref="Link.Href"/>) to an <see cref="Iri"/>.
    /// </summary>
    /// <param name="value">The URI. May be null.</param>
    /// <returns>The <see cref="Iri"/>, or <see langword="null"/> when <paramref name="value"/> is null.</returns>
    public static Iri? ToIri(this Uri? value) => value is null ? null : new Iri(value);

    /// <summary>
    /// Converts an <see cref="Iri"/> to the library's <c>string?</c> form (for setting an object's <c>Id</c>).
    /// </summary>
    /// <param name="iri">The IRI. May be the default value.</param>
    /// <returns>The absolute URI string, or <see langword="null"/> for the default <see cref="Iri"/>.</returns>
    public static string? ToLibraryId(this Iri? iri) => iri.HasValue ? iri.Value.Value : null;

    /// <summary>
    /// Converts an <see cref="Iri"/> to the library's <c>Uri?</c> form (for setting a <see cref="Link.Href"/>).
    /// </summary>
    /// <param name="iri">The IRI. May be the default value.</param>
    /// <returns>The <see cref="Uri"/>, or <see langword="null"/> for the default <see cref="Iri"/>.</returns>
    public static Uri? ToLinkHref(this Iri? iri) => iri.HasValue ? iri.Value.Uri : null;

    private static Iri AppendSegment(Iri iri, string segment)
    {
        if (!iri.IsAbsolute)
        {
            throw new ArgumentException(
                $"Cannot derive '{segment}' from a relative IRI: '{iri}'.",
                nameof(iri));
        }

        // Combine against the absolute URI, ensuring the parent path ends with a slash
        // so the segment appends rather than replacing the final path segment.
        var baseUri = iri.Uri;
        var builder = new UriBuilder(baseUri);
        var path = builder.Path;
        if (path.Length == 0 || !path.EndsWith('/'))
        {
            path += "/";
        }

        builder.Path = path + segment;
        return new Iri(builder.Uri);
    }
}
