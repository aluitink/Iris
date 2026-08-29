using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// The context for an inbound activity that has been signature-validated and is being processed by
/// the <see cref="IInboxProcessor"/>.
/// </summary>
/// <remarks>
/// A delivery is the (recipient, activity) pair the inbox endpoint produces: the local actor whose
/// inbox received the activity, and the validated activity itself. Handlers receive this to learn
/// <em>who</em> the activity was addressed to (the <see cref="RecipientIri"/>) in addition to the
/// activity's own <c>actor</c>/<c>object</c> fields. The <see cref="RecipientIri"/> is authoritative
/// for the target of an inbound follow — it is the inbox the activity was delivered to.
/// </remarks>
/// <param name="RecipientIri">The local actor whose inbox received the activity.</param>
/// <param name="Activity">The validated activity (always an <see cref="Activity"/> with a non-null <c>Id</c>).</param>
public sealed record InboxDelivery(Iri RecipientIri, Activity Activity)
{
    /// <summary>
    /// The local actor whose inbox received the activity.
    /// </summary>
    public Iri RecipientIri { get; init; } = RecipientIri;

    /// <summary>
    /// The validated activity (always an <see cref="Activity"/> with a non-null <c>Id</c>).
    /// </summary>
    public Activity Activity { get; init; } = Activity;
}
