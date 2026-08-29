using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// The strongly-typed contract for handling inbound activity deliveries of a specific ActivityStreams
/// activity type.
/// </summary>
/// <typeparam name="TActivity">The activity type this handler processes (e.g. <c>Follow</c>).</typeparam>
/// <remarks>
/// Concrete handlers implement this by deriving from <see cref="ActivityHandlerBase{TActivity}"/> and
/// overriding <see cref="HandleAsync(InboxDelivery,TActivity,CancellationToken)"/>. The base class
/// wires the typed <c>HandleAsync</c> into the non-generic <see cref="IActivityHandler.DispatchAsync"/>
/// that the <see cref="IInboxProcessor"/> invokes, so handlers only ever see their activity type.
/// </remarks>
public interface IActivityHandler<TActivity>
    where TActivity : Activity
{
    /// <summary>
    /// Handles an inbound activity of type <typeparamref name="TActivity"/>.
    /// </summary>
    /// <param name="delivery">The delivery context (the recipient actor and the full activity).</param>
    /// <param name="activity">The typed activity to interpret (already validated and stored).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the activity has been interpreted.</returns>
    public Task HandleAsync(InboxDelivery delivery, TActivity activity, CancellationToken ct = default);
}
