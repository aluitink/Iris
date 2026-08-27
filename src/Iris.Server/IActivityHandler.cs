using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// A handler for inbound activity deliveries that the <see cref="IInboxProcessor"/> dispatches to.
/// </summary>
/// <remarks>
/// Each concrete handler derives from <see cref="ActivityHandlerBase{TActivity}"/> (which implements
/// this interface) and provides a strongly-typed <see cref="IActivityHandler{TActivity}.HandleAsync"/>.
/// This non-generic surface exposes the <see cref="HandledActivityType"/> (so the processor can build
/// a <c>Type</c> → handler dispatch map) and the <see cref="DispatchAsync"/> entry point the processor
/// invokes (which threads the <see cref="CancellationToken"/> and hands the handler the activity
/// already validated and stored).
/// </remarks>
public interface IActivityHandler
{
    /// <summary>
    /// The ActivityStreams activity type this handler processes (e.g. <c>typeof(Follow)</c>).
    /// </summary>
    public Type HandledActivityType { get; }

    /// <summary>
    /// Dispatches an inbound activity to this handler. The activity is guaranteed to be assignable to
    /// this handler's activity type (the processor matched on the runtime type).
    /// </summary>
    /// <param name="delivery">The delivery context (the recipient actor and the full activity).</param>
    /// <param name="activity">The activity to interpret (already validated and stored).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the activity has been interpreted.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="delivery"/> or <paramref name="activity"/> is null.</exception>
    public Task DispatchAsync(InboxDelivery delivery, Activity activity, CancellationToken ct = default);
}
