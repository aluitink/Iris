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
}
