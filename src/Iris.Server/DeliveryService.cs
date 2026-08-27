using Iris.Core;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging;

namespace Iris.Server;

/// <summary>
/// The default <see cref="IDeliveryService"/>: enqueues a <see cref="DeliveryJob"/> on the
/// <see cref="IDeliveryQueue"/> for asynchronous, signed delivery by the <see cref="DeliveryWorker"/>.
/// </summary>
/// <remarks>
/// Enqueueing is the only thing this service does — it does not perform HTTP. The separation keeps the
/// handler path fast (a follow is interpreted and its <c>Accept</c> scheduled in a few memory
/// operations) and lets delivery failures (network, 5xx, signature rejection) be handled by the worker
/// without a 500 on the inbound request.
/// </remarks>
public sealed class DeliveryService : IDeliveryService
{
    private readonly IDeliveryQueue _queue;
    private readonly ILogger<DeliveryService> _logger;

    /// <summary>
    /// Initializes a new <see cref="DeliveryService"/>.
    /// </summary>
    /// <param name="queue">The delivery queue to enqueue jobs onto. Must not be null.</param>
    /// <param name="logger">The logger. Must not be null.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="queue"/> or <paramref name="logger"/> is null.</exception>
    public DeliveryService(IDeliveryQueue queue, ILogger<DeliveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task DeliverAsync(Iri inboxIri, Activity activity, CancellationToken ct = default)
        => DeliverAsync(inboxIri, activity, actorIri: null, ct);

    /// <inheritdoc/>
    public async Task DeliverAsync(Iri inboxIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (!inboxIri.IsAbsolute)
        {
            throw new ArgumentException("The inbox IRI must be an absolute IRI.", nameof(inboxIri));
        }

        await _queue
            .EnqueueAsync(new DeliveryJob(inboxIri, activity, actorIri), ct)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Enqueued delivery of activity {ActivityId} to {Inbox} as {Actor}",
            activity.Id,
            inboxIri.Value,
            actorIri?.Value ?? "<instance actor>");
    }

    /// <inheritdoc/>
    public Task DeliverToActorAsync(Iri recipientIri, Activity activity, CancellationToken ct = default)
        => DeliverToActorAsync(recipientIri, activity, actorIri: null, ct);

    /// <inheritdoc/>
    public Task DeliverToActorAsync(Iri recipientIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
    {
        if (!recipientIri.IsAbsolute)
        {
            throw new ArgumentException("The recipient IRI must be an absolute IRI.", nameof(recipientIri));
        }

        // The ActivityPub convention: an actor's inbox is the actor IRI with "/inbox" appended.
        return DeliverAsync(recipientIri.InboxOf(), activity, actorIri, ct);
    }
}
