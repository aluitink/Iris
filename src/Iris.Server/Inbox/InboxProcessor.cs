using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// The default <see cref="IInboxProcessor"/>: stores the delivered activity, then dispatches it to
/// the registered <see cref="IActivityHandler"/> whose <see cref="IActivityHandler.HandledActivityType"/>
/// is the closest ancestor of the activity's runtime type.
/// </summary>
/// <remarks>
/// Dispatch resolves the <em>most specific</em> matching handler: it measures each handler's
/// <see cref="IActivityHandler.HandledActivityType"/> by its distance (in base-type steps) from the
/// activity's runtime type and picks the closest. An exact type match (distance 0) is most specific; a
/// handler registered for the base <c>Activity</c> type (the largest distance) catches any activity that
/// no more specific handler covers. When two handlers are equally close (e.g. both registered for
/// <c>Activity</c>), registration order breaks the tie (the earlier-registered handler wins). Dispatch is
/// therefore independent of registration order for distinct activity types. An activity with no matching
/// handler is still stored. The processor never swallows a handler exception: it propagates so the
/// endpoint can decide the response (the inbox endpoint maps a handler failure to 500).
/// </remarks>
public sealed class InboxProcessor : IInboxProcessor
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new <see cref="InboxProcessor"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (stores the activity).</param>
    /// <param name="handlers">The activity handlers to dispatch to.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> or <paramref name="handlers"/> is null.</exception>
    public InboxProcessor(IPersistenceProvider persistence, IEnumerable<IActivityHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(handlers);

        _persistence = persistence;
        Handlers = handlers.ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<IActivityHandler> Handlers { get; }

    /// <inheritdoc/>
    public async Task ProcessAsync(InboxDelivery delivery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        // The processor is the single owner of "receive an activity". Idempotent, at-least-once delivery
        // (C-07): store the activity add-if-absent so it can be re-read, and — when this is a re-delivery
        // (the IRI is already stored) — do NOT re-dispatch it to a handler. Re-dispatching a received
        // Create is what re-federates it to the author's remote followers, so this guard is the loop-safety
        // mechanism for the two-instance network (19.3.1/19.3.2): with mutual follows, the peer's echo of
        // our Create is delivered back to us; without the guard it would be re-fan-out forever (an
        // unbounded delivery storm). The first delivery stores (true) and is handled; every re-delivery
        // (false) is stored as a no-op and skipped.
        var firstDelivery = await _persistence.Activities
            .TryAddActivityAsync(delivery.Activity, ct)
            .ConfigureAwait(false);

        if (!firstDelivery)
        {
            return;
        }

        var handler = FindHandler(delivery.Activity);
        if (handler is null)
        {
            // No registered handler for this activity type: it is stored, but nothing is dispatched.
            return;
        }

        await handler.DispatchAsync(delivery, delivery.Activity, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the handler for the activity's runtime type: the handler whose
    /// <see cref="IActivityHandler.HandledActivityType"/> is the closest ancestor of the activity's
    /// runtime type. When several handlers match (e.g. two handlers registered for the base
    /// <see cref="Activity"/> type), the closest match wins; a tie is broken by registration order (the
    /// earlier-registered handler is preferred).
    /// </summary>
    /// <remarks>
    /// Specificity is measured by the distance (in base-type steps) from the activity's runtime type to
    /// the handler's <see cref="IActivityHandler.HandledActivityType"/>: an exact type match has
    /// distance 0, a direct base type has distance 1, and so on. This makes dispatch independent of
    /// registration order for distinct activity types (an <c>Add</c> and an <c>Invite</c> each reach
    /// their own specific handler), while still letting a single <c>Activity</c>-type catch-all handler
    /// interpret activities that no more specific handler covers.
    /// </remarks>
    private IActivityHandler? FindHandler(Activity activity)
    {
        var runtimeType = activity.GetType();

        IActivityHandler? best = null;
        var bestDistance = int.MaxValue;

        foreach (var handler in Handlers)
        {
            var distance = HierarchyDistance(runtimeType, handler.HandledActivityType);
            if (distance is null)
            {
                continue;
            }

            // A strictly smaller distance is more specific; on a tie the earlier-registered handler wins
            // (the loop visits handlers in registration order and only replaces on a strict improvement).
            if (distance.Value < bestDistance)
            {
                best = handler;
                bestDistance = distance.Value;
            }
        }

        return best;
    }

    /// <summary>
    /// Measures the distance (in base-type steps) from <paramref name="type"/> to
    /// <paramref name="handledType"/> when <paramref name="handledType"/> is an ancestor of (or
    /// identical to) <paramref name="type"/>; otherwise <c>null</c>.
    /// </summary>
    private static int? HierarchyDistance(Type type, Type handledType)
    {
        var distance = 0;
        for (Type? current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            if (current == handledType)
            {
                return distance;
            }

            distance++;
        }

        return null;
    }
}
