namespace Iris.Server;

/// <summary>
/// Processes an inbound activity: stores it and dispatches it to the registered
/// <see cref="IActivityHandler{TActivity}"/> for its type.
/// </summary>
/// <remarks>
/// The inbox endpoint calls <see cref="ProcessAsync"/> after validating the HTTP signature and
/// deserializing the body into an <c>Activity</c>. The processor is the single owner of
/// "receive an activity": it persists the activity (so it can be re-read from the outbox / by
/// handlers later) and then interprets it by dispatching to the handler whose
/// <see cref="IActivityHandler.HandledActivityType"/> matches the activity's runtime type. An
/// activity with no registered handler is still stored (unknown activity types are preserved, not
/// dropped), but nothing is dispatched.
/// </remarks>
public interface IInboxProcessor
{
    /// <summary>
    /// The activity handlers this processor can dispatch to.
    /// </summary>
    public IReadOnlyList<IActivityHandler> Handlers { get; }

    /// <summary>
    /// Stores the delivered activity and dispatches it to the matching handler (if any).
    /// </summary>
    /// <param name="delivery">The delivery (recipient + activity). Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the activity is stored and (if a handler matched) handled.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="delivery"/> is null.</exception>
    public Task ProcessAsync(InboxDelivery delivery, CancellationToken ct = default);
}
