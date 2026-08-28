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
    private readonly IActorDocumentFetcher? _actorDocuments;
    private readonly ILogger<DeliveryService> _logger;

    /// <summary>
    /// Initializes a new <see cref="DeliveryService"/>.
    /// </summary>
    /// <param name="queue">The delivery queue to enqueue jobs onto. Must not be null.</param>
    /// <param name="logger">The logger. Must not be null.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="queue"/> or <paramref name="logger"/> is null.</exception>
    public DeliveryService(IDeliveryQueue queue, ILogger<DeliveryService> logger)
        : this(queue, null, logger)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="DeliveryService"/>.
    /// </summary>
    /// <param name="queue">The delivery queue to enqueue jobs onto. Must not be null.</param>
    /// <param name="actorDocuments">The actor-document fetcher used to resolve a remote recipient's
    /// advertised <c>endpoints.sharedInbox</c> (F-01). May be null, in which case
    /// <see cref="DeliverToActorAsync(Iri, Activity, CancellationToken)"/> always falls back to the
    /// per-actor inbox (the ActivityPub convention).</param>
    /// <param name="logger">The logger. Must not be null.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="queue"/> or <paramref name="logger"/> is null.</exception>
    public DeliveryService(IDeliveryQueue queue, IActorDocumentFetcher? actorDocuments, ILogger<DeliveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _actorDocuments = actorDocuments;
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
    public async Task DeliverToActorAsync(Iri recipientIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (!recipientIri.IsAbsolute)
        {
            throw new ArgumentException("The recipient IRI must be an absolute IRI.", nameof(recipientIri));
        }

        // F-01: honor the recipient's advertised endpoints.sharedInbox (a shared inbox collects activity
        // for a whole instance). Read from the recipient's document via the fetcher (which reads through
        // the remote-actor cache, so a repeated delivery to the same recipient reuses the cached
        // document). When the document cannot be fetched or advertises no sharedInbox, fall back to the
        // ActivityPub convention: the actor IRI with "/inbox" appended.
        var inboxIri = await ResolveInboxAsync(recipientIri, ct).ConfigureAwait(false);
        await DeliverAsync(inboxIri, activity, actorIri, ct).ConfigureAwait(false);
    }

    private async Task<Iri> ResolveInboxAsync(Iri recipientIri, CancellationToken ct)
    {
        if (_actorDocuments is not null)
        {
            var actor = await _actorDocuments.GetActorAsync(recipientIri, ct).ConfigureAwait(false);
            if (actor is not null &&
                actor.Endpoints is Endpoints endpoints &&
                endpoints.SharedInbox is { } sharedInbox)
            {
                _logger.LogDebug(
                    "Delivering to shared inbox {SharedInbox} for recipient {Recipient}",
                    sharedInbox,
                    recipientIri.Value);
                return new Iri(sharedInbox);
            }
        }

        return recipientIri.InboxOf();
    }
}
