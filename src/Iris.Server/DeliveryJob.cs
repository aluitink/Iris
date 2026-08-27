using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// A single outbound federation delivery: an <see cref="Activity"/> to POST to a recipient's inbox.
/// </summary>
/// <remarks>
/// This is the unit of work the <see cref="IDeliveryQueue"/> carries and the
/// <see cref="IDeliveryService"/> produces. The <c>InboxIri</c> is the absolute IRI of the recipient's
/// inbox endpoint (e.g. <c>https://a.domain.local/ap/v1/u/alice/inbox</c>); the worker POSTs the
/// serialized <see cref="Activity"/> there, signed with the instance actor's key.
/// </remarks>
/// <param name="InboxIri">The absolute IRI of the recipient's inbox endpoint to deliver to.</param>
/// <param name="Activity">The activity to deliver (must be a non-null <see cref="Activity"/>).</param>
public sealed record DeliveryJob(Iri InboxIri, Activity Activity);
