using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// The default <see cref="IInboxProcessor"/>: stores the delivered activity, then dispatches it to
/// the registered <see cref="IActivityHandler"/> whose <see cref="IActivityHandler.HandledActivityType"/>
/// matches the activity's runtime type.
/// </summary>
/// <remarks>
/// Dispatch prefers an <em>exact</em> type match; when no exact match is registered, it walks the
/// activity's type hierarchy and dispatches to the closest base-type handler (so a handler registered
/// for <c>Activity</c> catches any activity that has no more specific handler). An activity with no
/// matching handler is still stored. The processor never swallows a handler exception: it propagates
/// so the endpoint can decide the response (the inbox endpoint maps a handler failure to 500).
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

        // The processor is the single owner of "receive an activity": store it first (so it can be
        // re-read even if interpretation later fails or is unsupported), then interpret it.
        await _persistence.Activities.PutActivityAsync(delivery.Activity, ct).ConfigureAwait(false);

        var handler = FindHandler(delivery.Activity);
        if (handler is null)
        {
            // No registered handler for this activity type: it is stored, but nothing is dispatched.
            return;
        }

        await handler.DispatchAsync(delivery, delivery.Activity, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the handler for the activity's runtime type: an exact match, or the closest base-type
    /// handler up the hierarchy.
    /// </summary>
    private IActivityHandler? FindHandler(Activity activity)
    {
        // Walk the type hierarchy from most specific to least, returning the first registered match.
        for (Type? type = activity.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            var match = Handlers.FirstOrDefault(h => h.HandledActivityType == type);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
