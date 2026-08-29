using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Delivery;

/// <summary>
/// A single outbound federation delivery: an <see cref="Activity"/> to POST to a recipient's inbox.
/// </summary>
/// <remarks>
/// This is the unit of work the <see cref="IDeliveryQueue"/> carries and the
/// <see cref="DeliveryService"/> produces. The <c>InboxIri</c> is the absolute IRI of the recipient's
/// inbox endpoint (e.g. <c>https://a.domain.local/ap/v1/u/alice/inbox</c>); the worker POSTs the
/// serialized <see cref="Activity"/> there. The <c>ActorIri</c> is the local actor the delivery is
/// signed as (the actor performing the automated event); when it is null the worker falls back to
/// the instance actor (the "system key for automated events").
/// </remarks>
/// <param name="InboxIri">The absolute IRI of the recipient's inbox endpoint to deliver to.</param>
/// <param name="Activity">The activity to deliver (must be a non-null <see cref="Activity"/>).</param>
/// <param name="ActorIri">The local actor to sign the delivery as, or null to sign as the
/// instance actor (the system key for automated events).</param>
/// <param name="Attempts">The number of delivery attempts already made (0 for a fresh job). The
/// <see cref="DeliveryWorker"/> (F-22) tracks attempts as it retries a failed delivery and records the
/// count on the <see cref="DeadLetterEntry"/> when the retry budget is exhausted.</param>
public sealed record DeliveryJob(Iri InboxIri, Activity Activity, Iri? ActorIri = null, int Attempts = 0);
