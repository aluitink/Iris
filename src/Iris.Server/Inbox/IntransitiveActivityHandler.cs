using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles the ActivityStreams intransitive-activity family <see cref="Read"/>, <see cref="View"/>,
/// <see cref="Listen"/>, <see cref="Travel"/>, and <see cref="Arrive"/> (F-17): acknowledgment-of-receipt
/// activities that a server may emit after consuming another actor's object.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope — acknowledgment, no state change.</strong> Intransitive activities (AS2.0) are
/// "the actor did something to the object that does not result in a new object" — a signal that the
/// actor read/viewed/listened to, traveled to, or arrived at the object. Unlike the membership
/// primitives (<see cref="MembershipActivityHandler"/>) or the moderation activities (<see cref="Like"/>,
/// <see cref="Block"/>, <see cref="Flag"/>), an intransitive activity changes no persistent Iris state:
/// there is no member set, like edge, or block edge to update. The handler's job is to <em>accept</em>
/// the activity (so it is stored by the <see cref="InboxProcessor"/> and not rejected or 500'd) and
/// interpret it as a no-op — Iris does not record a "read receipt" (that would require a new store and
/// has no consumer in v1).
/// </para>
/// <para>
/// <strong>Dispatch.</strong> In the library, <see cref="Read"/>, <see cref="View"/>, and
/// <see cref="Listen"/> derive directly from the base <see cref="Activity"/> type, while
/// <see cref="Travel"/> and <see cref="Arrive"/> derive from <see cref="IntransitiveActivity"/> (which
/// itself derives from <see cref="Activity"/>). The five types share no single concrete base that a
/// single <see cref="ActivityHandlerBase{TActivity}"/> could be parameterized over, so this handler
/// derives from the non-generic <see cref="IActivityHandler"/> and is registered for the base
/// <see cref="Activity"/> type, pattern-matching the five types at dispatch. The
/// <see cref="InboxProcessor"/> resolves each activity to the most specific registered handler: the
/// exact-type handlers (<see cref="AddActivityHandler"/>, <see cref="LikeActivityHandler"/>, …) win
/// their activities, and this catch-all (registered before the
/// <see cref="MembershipActivityHandler"/>, which is also registered for <see cref="Activity"/>) wins
/// the intransitive family by registration-order tie-break.
/// </para>
/// <para>
/// <strong>Forwarding.</strong> Because this handler is registered first for the base
/// <see cref="Activity"/> type, it sees every activity no more specific handler covers — including the
/// <see cref="Offer"/>/<see cref="Invite"/>/<see cref="Join"/>/<see cref="Leave"/> membership primitives,
/// which the <see cref="MembershipActivityHandler"/> must interpret. A non-intransitive activity is
/// therefore <em>forwarded</em> to the injected <see cref="MembershipActivityHandler"/> (rather than
/// no-op'd), so the membership family is not swallowed by this handler's catch-all. The
/// <see cref="MembershipActivityHandler"/> is the ultimate catch-all: a genuinely-foreign activity (no
/// handler's concern) is a no-op in its own default case.
/// </para>
/// <para>
/// <strong>Idempotent / safe to re-apply.</strong> The handler performs no writes, so a re-delivered
/// activity (at-least-once delivery, C-07) is a no-op by construction.
/// </para>
/// </remarks>
public sealed class IntransitiveActivityHandler : IActivityHandler
{
    private readonly MembershipActivityHandler _membership;

    /// <summary>
    /// Initializes a new <see cref="IntransitiveActivityHandler"/>.
    /// </summary>
    /// <param name="membership">The <see cref="MembershipActivityHandler"/> that non-intransitive base-
    /// <see cref="Activity"/> activities (the <see cref="Offer"/>/<see cref="Invite"/>/<see cref="Join"/>/
    /// <see cref="Leave"/> membership primitives) are forwarded to — this handler is registered before it
    /// for the base <see cref="Activity"/> type, so it must delegate the membership family rather than
    /// swallow it.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="membership"/> is null.</exception>
    public IntransitiveActivityHandler(MembershipActivityHandler membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        _membership = membership;
    }

    /// <inheritdoc/>
    public Type HandledActivityType => typeof(Activity);

    /// <inheritdoc/>
    public Task DispatchAsync(InboxDelivery delivery, Activity activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(activity);

        // The processor resolves each activity to the most specific registered handler. The exact-type
        // handlers (AddActivityHandler, LikeActivityHandler, …) win their activities, so this catch-all
        // (registered for the base Activity type, BEFORE the MembershipActivityHandler) sees every
        // activity no more specific handler covers. In the library Read/View/Listen derive from Activity
        // and Travel/Arrive from IntransitiveActivity, so the cases are ordered with the
        // IntransitiveActivity derivatives first (most specific) and the base-Activity derivatives after.
        // All five are acknowledgments of receipt — no state is changed, so they return
        // Task.CompletedTask (the activity is stored by the processor; the interpretation is a no-op).
        //
        // A non-intransitive activity reaching this dispatch (the default case) is FORWARDED to the
        // MembershipActivityHandler (which interprets the Offer/Invite/Join/Leave membership primitives)
        // rather than no-op'd: this handler is registered first for the base Activity type, so without
        // the forward a membership activity would be swallowed here (its default) and the community's
        // member set would never be updated. A genuinely-foreign activity (no handler's concern) is a
        // no-op in the MembershipActivityHandler's own default case.
        switch (activity)
        {
            case Travel:
            case Arrive:
            case Read:
            case View:
            case Listen:
                return Task.CompletedTask;
            default:
                return _membership.DispatchAsync(delivery, activity, ct);
        }
    }
}
