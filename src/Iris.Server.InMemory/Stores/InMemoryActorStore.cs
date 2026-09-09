using Iris.Core;
using Iris.Server;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="IActorStore"/> backed by a concurrent dictionary.
/// </summary>
/// <remarks>
/// Ephemeral: actors vanish on restart. Keys are the actor IRIs. Thread-safe.
/// </remarks>
public sealed class InMemoryActorStore : IActorStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, Actor> _actors = new();

    /// <summary>
    /// Removes all actors (test isolation / teardown).
    /// </summary>
    public void Clear() => _actors.Clear();

    /// <inheritdoc/>
    public Task<bool> TryGetActorAsync(Iri actorIri, out Actor? actor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = _actors.TryGetValue(actorIri, out actor);
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task PutActorAsync(Actor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.Id))
        {
            throw new ArgumentException("Actor must have a non-null Id.", nameof(actor));
        }

        ct.ThrowIfCancellationRequested();
        _actors[new Iri(actor.Id)] = actor;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_actors.TryRemove(actorIri, out _));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Actor>> ListActorsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Actor>>(_actors.Values.ToList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Actor>> SearchActorsAsync(string? query, int limit, int offset, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(normalized);

        var matches = _actors.Values
            .Where(a => !hasQuery || MatchesActor(a, normalized!))
            .OrderBy(a => a.Id ?? string.Empty, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<Actor>>(matches);
    }

    /// <inheritdoc/>
    public Task<int> CountSearchMatchesAsync(string? query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(normalized);

        var count = _actors.Values.Count(a => !hasQuery || MatchesActor(a, normalized!));
        return Task.FromResult(count);
    }

    /// <summary>
    /// Returns true when the actor's <c>name</c>, <c>preferredUsername</c>, or IRI contains
    /// <paramref name="query"/> as a case-insensitive substring (the same matching the global search
    /// service applies to the actor pass).
    /// </summary>
    private static bool MatchesActor(Actor actor, string query)
    {
        return ContainsInStrings(actor.Name, query)
            || (actor.PreferredUsername is { Length: > 0 } username && username.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (actor.Id is { Length: > 0 } id && id.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsInStrings(IEnumerable<string>? values, string query)
    {
        if (values is null)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
