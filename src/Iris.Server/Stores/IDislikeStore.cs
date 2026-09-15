using Iris.Core;

namespace Iris.Server.Stores;

/// <summary>
/// Records and queries dislike (downvote) relationships. Mirrors <see cref="ILikeStore"/> for
/// the AS2.0 <c>Dislike</c> activity — used for Lemmy-style downvotes.
/// </summary>
public interface IDislikeStore
{
    /// <summary>Records a dislike from <paramref name="dislikerIri"/> to <paramref name="objectIri"/>.</summary>
    public Task RecordDislikeAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default);

    /// <summary>Removes a dislike edge. Returns <see langword="true"/> when an edge was removed.</summary>
    public Task<bool> RemoveDislikeAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default);

    /// <summary>Returns whether <paramref name="dislikerIri"/> has disliked <paramref name="objectIri"/>.</summary>
    public Task<bool> HasDislikedAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default);

    /// <summary>Returns the IRIs of actors that have disliked <paramref name="objectIri"/>.</summary>
    public Task<IReadOnlyList<Iri>> GetDislikersAsync(Iri objectIri, CancellationToken ct = default);
}
