using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Base class for activity handlers. Derive from this and override
/// <see cref="HandleAsync(InboxDelivery,TActivity,CancellationToken)"/> to interpret a specific
/// activity type; the base wires the typed handler into the non-generic
/// <see cref="IActivityHandler"/> surface the <see cref="IInboxProcessor"/> dispatches to.
/// </summary>
/// <typeparam name="TActivity">The activity type this handler processes (e.g. <c>Follow</c>).</typeparam>
/// <remarks>
/// The base implements <see cref="IActivityHandler.HandledActivityType"/> (<c>typeof(TActivity)</c>)
/// and <see cref="IActivityHandler.DispatchAsync"/> (which casts the activity to
/// <typeparamref name="TActivity"/> and calls the abstract <see cref="HandleAsync"/>). Handlers
/// therefore never see an untyped <see cref="Activity"/> or deal with dispatch.
/// </remarks>
public abstract class ActivityHandlerBase<TActivity> : IActivityHandler, IActivityHandler<TActivity>
    where TActivity : Activity
{
    /// <inheritdoc/>
    public Type HandledActivityType => typeof(TActivity);

    /// <summary>
    /// Handles an inbound activity of type <typeparamref name="TActivity"/>. Derived classes must
    /// override this to interpret the activity.
    /// </summary>
    /// <param name="delivery">The delivery context (the recipient actor and the full activity).</param>
    /// <param name="activity">The typed activity to interpret (already validated and stored).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the activity has been interpreted.</returns>
    public abstract Task HandleAsync(InboxDelivery delivery, TActivity activity, CancellationToken ct = default);

    /// <inheritdoc/>
    public Task DispatchAsync(InboxDelivery delivery, Activity activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(activity);

        if (activity is not TActivity typed)
        {
            // The processor only dispatches when the activity is assignable to this handler's type,
            // so this is a programming error (a handler registered for the wrong type).
            throw new InvalidOperationException(
                $"Activity of type {activity.GetType().Name} is not assignable to {typeof(TActivity).Name}.");
        }

        return HandleAsync(delivery, typed, ct);
    }
}
