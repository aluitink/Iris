using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="IBookmarkStore"/> (S111) backed by a concurrent dictionary: a forward index
/// (bookmarker IRI → set of bookmarked object IRIs).
/// </summary>
public sealed class InMemoryBookmarkStore : IBookmarkStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _bookmarks = new();

    /// <summary>
    /// Removes all bookmarks (test isolation / teardown).
    /// </summary>
    public void Clear() => _bookmarks.Clear();

    /// <inheritdoc/>
    public Task RecordBookmarkAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _bookmarks.AddOrUpdate(
            bookmarkerIri,
            _ => { var s = new HashSet<Iri>(); lock (s) { s.Add(objectIri); } return s; },
            (_, set) => { lock (set) { set.Add(objectIri); } return set; });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveBookmarkAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool removed = false;
        if (_bookmarks.TryGetValue(bookmarkerIri, out var set))
        {
            lock (set) { removed = set.Remove(objectIri); }
        }
        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetBookmarksAsync(Iri bookmarkerIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_bookmarks.TryGetValue(bookmarkerIri, out var set))
        {
            return Task.FromResult<IReadOnlyList<Iri>>([]);
        }
        lock (set)
        {
            return Task.FromResult<IReadOnlyList<Iri>>(
                set.OrderBy(iri => iri.Value, StringComparer.Ordinal).ToList());
        }
    }

    /// <inheritdoc/>
    public Task<bool> IsBookmarkedAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_bookmarks.TryGetValue(bookmarkerIri, out var set))
        {
            return Task.FromResult(false);
        }
        lock (set)
        {
            return Task.FromResult(set.Contains(objectIri));
        }
    }
}
