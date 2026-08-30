using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles the ActivityStreams community-membership primitives <see cref="Offer"/>, <see cref="Invite"/>,
/// <see cref="Join"/>, and <see cref="Leave"/> (F-16): an alternate membership lifecycle for a local
/// <see cref="Group"/> (community) that a server may use instead of the <see cref="Follow"/>-based
/// membership Iris otherwise records (F-09 / <see cref="AddActivityHandler"/> /
/// <see cref="RemoveActivityHandler"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope — community membership.</strong> These four activities are the spec's
/// membership primitives (AS2.0). Their most common federation use is a server that represents a
/// community's membership as an <c>Invite</c>/<c>Offer</c> (an invitation to join) or a
/// <c>Join</c>/<c>Leave</c> (the actor's own declaration of membership). This handler interprets that
/// case for a <em>local</em> community:
/// </para>
/// <list type="bullet">
/// <item><see cref="Offer"/> — the activity's <c>object</c> (the invited actor) is added to the
/// recipient community's member set (an invitation is accepted on receipt).</item>
/// <item><see cref="Invite"/> — the activity's <c>object</c> (the invited actor) is added to the
/// recipient community's member set (an invitation is accepted on receipt).</item>
/// <item><see cref="Join"/> — the activity's <c>object</c> (the joining actor) is added to the
/// recipient community's member set (the actor's declaration of membership).</item>
/// <item><see cref="Leave"/> — the activity's <c>object</c> (the leaving actor) is removed from the
/// recipient community's member set (the actor's declaration of departure).</item>
/// </list>
/// <para>
/// <strong>Recipient is the community (the membership's owner).</strong> A membership activity is
/// delivered to the <em>community whose membership it changes</em> — here, the community (the
/// <see cref="InboxDelivery.RecipientIri"/>). When the recipient is a local <em>person</em> (not a
/// community) the activity is a no-op: a person has no member set to add to (membership is a
/// community relationship). When the recipient is remote (neither a local community nor person) the
/// activity is not this instance's concern.
/// </para>
/// <para>
/// <strong>Dispatch.</strong> A single <see cref="ActivityHandlerBase{TActivity}"/> cannot be
/// parameterized over four activity types, so the handler derives from the non-generic
/// <see cref="IActivityHandler"/> and is registered for the base <see cref="Activity"/> type,
/// pattern-matching the <see cref="Offer"/>/<see cref="Invite"/>/<see cref="Join"/>/<see cref="Leave"/> at
/// dispatch. The <see cref="InboxProcessor"/> resolves each activity to the most specific registered
/// handler, so the exact-type <see cref="AddActivityHandler"/>/<see cref="RemoveActivityHandler"/>
/// (registered for <see cref="Add"/>/<see cref="Remove"/>) win their activities and this catch-all
/// interprets the four membership types — and any other activity that no more specific handler covers.
/// </para>
/// <para>
/// <strong>Idempotent.</strong> <see cref="ICommunityStore.AddMemberAsync"/> and
/// <see cref="ICommunityStore.RemoveMemberAsync"/> are idempotent, so a re-delivered activity
/// (at-least-once delivery, C-07) is safe to re-apply.
/// </para>
/// </remarks>
public sealed class MembershipActivityHandler : IActivityHandler
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new <see cref="MembershipActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="ICommunityStore"/>).</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> is null.</exception>
    public MembershipActivityHandler(IPersistenceProvider persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _persistence = persistence;
    }

    /// <inheritdoc/>
    public Type HandledActivityType => typeof(Activity);

    /// <inheritdoc/>
    public Task DispatchAsync(InboxDelivery delivery, Activity activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(activity);

        // The processor resolves each activity to the most specific registered handler. The exact-type
        // AddActivityHandler/RemoveActivityHandler (registered for Add/Remove) win their activities, so
        // this catch-all (registered for the base Activity type) interprets the four membership types —
        // and any other activity that no more specific handler covers. In the library Invite derives
        // from Offer (both are "invitation" activities), so a case Offer would subsume Invite — the
        // cases are ordered so Leave (the only remove-primitive) is matched distinctly and Offer/Invite
        // share the add path. A foreign activity reaching this dispatch (the fall-through case) is a
        // no-op, not a throw: throwing would turn a benign dispatch-order artifact into a 500 on a
        // validly-delivered activity.
        switch (activity)
        {
            case Leave leave:
                return RemoveMemberAsync(delivery, leave, ct);
            case Join join:
                return AddMemberAsync(delivery, join, ct);
            case Invite invite:
                return AddMemberAsync(delivery, invite, ct);
            case Offer offer:
                return AddMemberAsync(delivery, offer, ct);
            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Adds the activity's <c>object</c> to the recipient community's member set (for <see cref="Offer"/>,
    /// <see cref="Invite"/>, and <see cref="Join"/>).
    /// </summary>
    private async Task AddMemberAsync(InboxDelivery delivery, Activity activity, CancellationToken ct)
    {
        if (!await IsLocalCommunityAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
        {
            return;
        }

        var memberIri = activity.Object?.FirstOrDefault().ResolveObjectIri();
        if (memberIri is not { } resolvedMember)
        {
            return;
        }

        await _persistence.Communities
            .AddMemberAsync(delivery.RecipientIri, resolvedMember, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the activity's <c>object</c> from the recipient community's member set (for
    /// <see cref="Leave"/>).
    /// </summary>
    private async Task RemoveMemberAsync(InboxDelivery delivery, Leave leave, CancellationToken ct)
    {
        if (!await IsLocalCommunityAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
        {
            return;
        }

        var memberIri = leave.Object?.FirstOrDefault().ResolveObjectIri();
        if (!memberIri.HasValue)
        {
            return;
        }

        await _persistence.Communities
            .RemoveMemberAsync(delivery.RecipientIri, memberIri.Value, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether <paramref name="recipientIri"/> is a local community (the only recipient type this
    /// handler interprets — the membership being changed must be owned by this instance).
    /// </summary>
    private Task<bool> IsLocalCommunityAsync(Iri recipientIri, CancellationToken ct)
        => _persistence.Communities.TryGetCommunityAsync(recipientIri, out _, ct);
}
