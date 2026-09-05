using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="IReplyStore"/> backed by a single concurrent dictionary
/// (parent-object IRI → set of reply-object IRIs).
/// </summary>
/// <remarks>
/// Ephemeral: reply edges vanish on restart. Thread-safe.
/// </remarks>
public sealed class InMemoryReplyStore : IReplyStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _replies = new();

    /// <summary>
    /// Removes all reply (thread) edges (test isolation / teardown).
    /// </summary>
    public void Clear() => _replies.Clear();

    /// <inheritdoc/>
    public Task RecordReplyAsync(Iri parentIri, Iri childIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _replies.AddOrUpdate(
            parentIri,
            _ => NewSet(childIri),
            (_, set) => { lock (set) { set.Add(childIri); } return set; });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveReplyAsync(Iri parentIri, Iri childIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool removed = false;
        if (_replies.TryGetValue(parentIri, out var set))
        {
            lock (set) { removed = set.Remove(childIri); }
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetRepliesAsync(Iri parentIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(parentIri));
    }

    /// <inheritdoc/>
    public Task<bool> HasReplyAsync(Iri parentIri, Iri childIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_replies.TryGetValue(parentIri, out var set) && IsIn(set, childIri));
    }

    private static HashSet<Iri> NewSet(Iri iri)
    {
        var set = new HashSet<Iri>();
        lock (set) { set.Add(iri); }
        return set;
    }

    private IReadOnlyList<Iri> Snapshot(Iri key)
    {
        if (!_replies.TryGetValue(key, out var set))
        {
            return [];
        }

        lock (set)
        {
            return set.ToList();
        }
    }

    private static bool IsIn(HashSet<Iri> set, Iri iri)
    {
        lock (set)
        {
            return set.Contains(iri);
        }
    }
}
