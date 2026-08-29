using KristofferStrube.ActivityStreams;

namespace Iris.Core.Collections;

/// <summary>
/// An Iris wrapper around a single fetched <see cref="OrderedCollectionPage"/>: the library page
/// plus its flattened items and the boundary-converted <see cref="Iri"/> next/prev links.
/// </summary>
/// <remarks>
/// This is an allowed Iris **wrapper** (coding-style Rule 6) — it *contains* the library type, it
/// does not re-declare it. <see cref="Items"/> is <see cref="IReadOnlyList{T}"/> of
/// <see cref="IObjectOrLink"/> so callers pattern-match each item (Rule 8).
/// <see cref="NextPage"/>/<see cref="PrevPage"/> are <see cref="Iri"/> (nullable; Rule 4),
/// converted from the library's <c>Link.Href</c> (<see cref="Uri"/>) at the boundary.
/// <see cref="TotalItems"/> is <see cref="int"/> (nullable; the library's <c>uint</c> narrowed for
/// ergonomics). Lives in <c>Iris.Core</c> (not <c>Iris.Client</c>) so both the client and the
/// server's remote-collection fetcher can build it from the shared
/// <see cref="CollectionPageFactory"/> without a cross-project dependency.
/// </remarks>
public sealed class CollectionPage
{
    /// <summary>
    /// Gets the underlying library page.
    /// </summary>
    public required OrderedCollectionPage Page { get; init; }

    /// <summary>
    /// Gets the flattened items of the page (empty when the page has none).
    /// </summary>
    public required IReadOnlyList<IObjectOrLink> Items { get; init; }

    /// <summary>
    /// Gets the IRI of the next page, or null when this is the last page.
    /// </summary>
    public Iri? NextPage { get; init; }

    /// <summary>
    /// Gets the IRI of the previous page, or null when this is the first page.
    /// </summary>
    public Iri? PrevPage { get; init; }

    /// <summary>
    /// Gets the total number of items in the collection, or null when the server did not report it.
    /// </summary>
    public int? TotalItems { get; init; }

    /// <summary>
    /// Gets the IRI of the page (the page document's own <c>id</c>), or null when absent.
    /// </summary>
    public Iri? PageId { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is the last page (no <see cref="NextPage"/>).
    /// </summary>
    public bool IsLastPage => NextPage is null;
}

/// <summary>
/// Shared factory that flattens a deserialized <see cref="OrderedCollectionPage"/> into a
/// <see cref="CollectionPage"/>.
/// </summary>
/// <remarks>
/// Single source of truth for the page-flatten logic, shared by the client's
/// <c>IActivityPubClient.GetCollectionAsync</c> and the server's
/// <c>IrisRemoteCollectionFetcher</c> (previously copy-pasted in both).
/// </remarks>
public static class CollectionPageFactory
{
    /// <summary>
    /// Builds a <see cref="CollectionPage"/> from a fetched page document.
    /// </summary>
    /// <param name="obj">The fetched page document (deserialized into the range interface, then cast).</param>
    /// <returns>The flattened page, or null when <paramref name="obj"/> is not an
    /// <see cref="OrderedCollectionPage"/>.</returns>
    public static CollectionPage? FromOrderedCollectionPage(IObject? obj)
    {
        if (obj is not OrderedCollectionPage page)
        {
            return null;
        }

        var items = page.Items is { } itemsEnumerable ? itemsEnumerable.ToList() : [];
        return new CollectionPage
        {
            Page = page,
            Items = items,
            NextPage = page.Next.ResolveCollectionIri(),
            PrevPage = page.Prev.ResolveCollectionIri(),
            TotalItems = page.TotalItems is { } total ? (int)total : null,
            PageId = page.Id is { Length: > 0 } id ? new Iri(id) : null,
        };
    }
}
