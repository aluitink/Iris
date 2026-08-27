using Iris.Core;
using Iris.Server;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.InMemory;

/// <summary>
/// An in-memory <see cref="IActivityStore"/> backed by a concurrent dictionary.
/// </summary>
/// <remarks>
/// Ephemeral: activities vanish on restart. The outbox is stored per-actor as a list of
/// <see cref="IObjectOrLink"/> (newest first). Thread-safe.
/// </remarks>
public sealed class InMemoryActivityStore : IActivityStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, IObject> _activities = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, List<IObjectOrLink>> _outboxes = new();

    /// <inheritdoc/>
    public Task<bool> TryGetActivityAsync(Iri activityIri, out IObject? activity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = _activities.TryGetValue(activityIri, out activity);
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task PutActivityAsync(IObject activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (string.IsNullOrWhiteSpace(activity.Id))
        {
            throw new ArgumentException("Activity must have a non-null Id.", nameof(activity));
        }

        ct.ThrowIfCancellationRequested();
        _activities[new Iri(activity.Id)] = activity;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IObjectOrLink>> GetOutboxAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var outbox = _outboxes.TryGetValue(actorIri, out var items) ? items : [];
        return Task.FromResult<IReadOnlyList<IObjectOrLink>>(outbox.ToList());
    }

    /// <summary>
    /// Adds an activity to an actor's outbox (newest first). Used by tests and the inbox pipeline.
    /// </summary>
    /// <param name="actorIri">The IRI of the actor whose outbox is updated.</param>
    /// <param name="item">The activity to add. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task AddToOutboxAsync(Iri actorIri, IObjectOrLink item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        lock (_outboxes)
        {
            if (!_outboxes.TryGetValue(actorIri, out var list))
            {
                list = [];
                _outboxes[actorIri] = list;
            }

            list.Insert(0, item); // newest first
        }

        return Task.CompletedTask;
    }
}
