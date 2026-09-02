using Iris.Core;
using Iris.Server;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.InMemory.Stores;

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
    public Task<bool> TryAddActivityAsync(IObject activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (string.IsNullOrWhiteSpace(activity.Id))
        {
            throw new ArgumentException("Activity must have a non-null Id.", nameof(activity));
        }

        ct.ThrowIfCancellationRequested();
        // ConcurrentDictionary.TryAdd stores iff the key is absent and reports exactly that, so it is the
        // atomic add-if-absent the inbox pipeline needs to detect a re-delivered (already-stored) activity.
        return Task.FromResult(_activities.TryAdd(new Iri(activity.Id), activity));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IObjectOrLink>> GetOutboxAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var outbox = _outboxes.TryGetValue(actorIri, out var items) ? items : [];
        return Task.FromResult<IReadOnlyList<IObjectOrLink>>(outbox.ToList());
    }

    /// <inheritdoc/>
    public Task AddToOutboxAsync(Iri actorIri, IObjectOrLink item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        var itemIri = ItemIri(item);
        lock (_outboxes)
        {
            if (!_outboxes.TryGetValue(actorIri, out var list))
            {
                list = [];
                _outboxes[actorIri] = list;
            }

            // Idempotent by IRI (F-1911-2): a re-recorded activity (at-least-once delivery, restart
            // replay) is not duplicated in the outbox.
            if (itemIri is not null && list.Any(existing => ItemIri(existing) == itemIri))
            {
                return Task.CompletedTask;
            }

            list.Insert(0, item); // newest first
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveFromOutboxAsync(Iri actorIri, Iri itemIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool removed = false;
        lock (_outboxes)
        {
            if (_outboxes.TryGetValue(actorIri, out var list))
            {
                removed = list.RemoveAll(item => ItemIri(item) == itemIri.Value) > 0;
            }
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IObject>> GetAllActivitiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IObject>>(_activities.Values.ToList());
    }

    /// <summary>
    /// Resolves the IRI of an outbox item (its <c>Id</c> when it is an object, otherwise the link's
    /// <c>href</c>) so a removal can match by IRI.
    /// </summary>
    private static string? ItemIri(IObjectOrLink item)
        => item is IObject obj ? obj.Id : (item as Link)?.Href?.AbsoluteUri;
}
