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
    /// Derives the liked-collection IRI by appending <c>/liked</c>.
    /// </summary>
    /// <param name="iri">The actor IRI. Must be absolute.</param>
    /// <returns>The liked-collection IRI (e.g. <c>https://a.domain.local/u/alice/liked</c>).</returns>
    /// <exception cref="ArgumentException">When <paramref name="iri"/> is not absolute.</exception>
    public static Iri LikedOf(this Iri iri) => AppendSegment(iri, "liked");

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

    /// <summary>
    /// Resolves the IRI of an <see cref="IObjectOrLink"/>: a <see cref="Link"/> contributes its
    /// <c>Href</c>; an embedded object contributes its <c>Id</c>.
    /// </summary>
    /// <remarks>
    /// The single shared boundary conversion for turning an activity's <c>actor</c>/<c>object</c>
    /// (an <see cref="IObjectOrLink"/>) into an <see cref="Iri"/>. Used by the follow/accept/reject and
    /// announce handlers to resolve the IRI of the relevant party or target.
    /// </remarks>
    /// <param name="objOrLink">The object or link to resolve. May be null.</param>
    /// <returns>The resolved <see cref="Iri"/>, or <see langword="null"/> when the object/link carries no IRI.</returns>
    public static Iri? ResolveObjectIri(this IObjectOrLink? objOrLink)
    {
        if (objOrLink is ILink { Href: { } href })
        {
            return new Iri(href);
        }

        if (objOrLink is IObject { Id: { Length: > 0 } id })
        {
            return new Iri(id);
        }

        return null;
    }

    /// <summary>
    /// Resolves the IRI of an <see cref="ICollectionOrLink"/>: a <see cref="Link"/> contributes its
    /// <c>Href</c>; an embedded collection contributes its <c>Id</c>.
    /// </summary>
    /// <remarks>
    /// The shared boundary conversion for collection page <c>next</c>/<c>prev</c>/<c>first</c>
    /// links (an <see cref="ICollectionOrLink"/>). Same semantics as
    /// <see cref="ResolveObjectIri(IObjectOrLink?)" /> but typed for the collection range so callers
    /// don't need an upcast. Used by <see cref="CollectionPageFactory.FromOrderedCollectionPage"/>
    /// and the client's collection enumeration to resolve page-IRIs.
    /// </remarks>
    /// <param name="collectionOrLink">The collection or link to resolve. May be null.</param>
    /// <returns>The resolved <see cref="Iri"/>, or <see langword="null"/> when the collection/link carries no IRI.</returns>
    public static Iri? ResolveCollectionIri(this ICollectionOrLink? collectionOrLink)
    {
        if (collectionOrLink is ILink { Href: { } href })
        {
            return new Iri(href);
        }

        if (collectionOrLink is IObject { Id: { Length: > 0 } id })
        {
            return new Iri(id);
        }

        return null;
    }

    /// <summary>
    /// Builds the AS2.0 <see cref="Tombstone"/> for a deleted object, preserving the original object's
    /// <c>Id</c> and <c>formerType</c>.
    /// </summary>
    /// <remarks>
    /// When an object is deleted (the <c>Delete</c> activity, F-03/F-10), the IRI must still resolve and
    /// serve the "deleted" marker rather than a <c>404</c>: a <see cref="Tombstone"/> with the original
    /// object's <c>id</c> and <c>formerType</c>. This is the single boundary helper that constructs that
    /// marker so the <c>Delete</c> handler and any future deletion path emit an identical document.
    /// </remarks>
    /// <param name="objectIri">The IRI of the deleted object (becomes the tombstone's <c>id</c>).</param>
    /// <param name="formerType">The deleted object's AS2.0 type (e.g. <c>"Note"</c>), or <c>null</c> to omit <c>formerType</c>.</param>
    /// <returns>The <see cref="Tombstone"/> for the deleted object.</returns>
    public static Tombstone BuildTombstone(this Iri objectIri, string? formerType = null)
    {
        var tombstone = new Tombstone { Id = objectIri.Value, Deleted = DateTime.UtcNow };
        if (!string.IsNullOrWhiteSpace(formerType))
        {
            tombstone.FormerType = [formerType];
        }

        return tombstone;
    }

    /// <summary>
    /// Extracts the embedded content object from an activity's <c>object</c> (its first entry) when it is
    /// an <see cref="IObject"/> with an <c>Id</c>; returns <c>null</c> when the object is a bare
    /// <see cref="Link"/> reference (or absent).
    /// </summary>
    /// <remarks>
    /// Shared by the content-object paths: the <c>Create</c> handler stores the embedded object in the
    /// object store, and the <c>Update</c> handler stores the updated embedded object. A bare link
    /// reference (common in <c>Delete</c>) has no content to store, so callers fall back to the
    /// reference-only behavior.
    /// </remarks>
    /// <param name="activity">The activity whose <c>object</c> is read. May be null.</param>
    /// <returns>The embedded <see cref="IObject"/>, or <see langword="null"/> when absent or a link reference.</returns>
    public static IObject? ExtractEmbeddedObject(this Activity? activity)
    {
        var first = activity?.Object?.FirstOrDefault();
        return first is IObject { Id: { Length: > 0 } } obj ? obj : null;
    }

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
