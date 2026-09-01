using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Remove"/> activities: the ActivityStreams primitive for removing an item
/// from a collection. When the <em>recipient</em> (the inbox the activity was delivered to) is a local
/// <see cref="Group"/> (community), the activity's <c>object</c> (the item being removed) is removed from
/// the community's member set via the <see cref="ICommunityStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope — community membership (self-management).</strong> <c>Remove</c> is the spec's generic
/// collection-modification primitive (AS2.0). Its most common federation use is a server that represents
/// a community's membership as a <c>Remove</c> of a member from the community's <c>followers</c> (or
/// <c>members</c>) collection, in contrast to the <see cref="FollowActivityHandler"/>-based membership
/// Iris otherwise records (F-09). This handler interprets that case: a <c>Remove</c> whose recipient is a
/// local community and whose <em>actor is that same community</em> removes the <c>object</c> from its
/// member set.
/// </para>
/// <para>
/// <strong>The community manages its own membership (actor == recipient).</strong> A community's
/// membership is an act of the community's own management surface: the activity's <c>actor</c> must be the
/// recipient community itself (the community posts a <c>Remove</c> through its own inbox, as it publishes
/// Follows through its own outbox — the same self-management gate the community outbox publish endpoint
/// applies to its <c>actor</c>). A collection-modifying activity delivered to a local community whose
/// actor is <em>some other</em> actor is not treated as a membership edit here: only the community
/// edits its own <c>members</c>. This is the authorization Iris records for Phase 19.5.2 (membership
/// management). When the recipient is a local <em>person</em> (not a community) the activity is a no-op: a
/// person's <c>followers</c> is maintained by the follow lifecycle (<see cref="FollowActivityHandler"/> /
/// <see cref="AcceptActivityHandler"/>), not by <c>Remove</c>. When the recipient is remote (neither a
/// local community nor person) the activity is not this instance's concern.
/// </para>
/// <para>
/// <strong>Exact-type dispatch.</strong> This handler derives from <see cref="ActivityHandlerBase{Remove}"/>,
/// so the <see cref="InboxProcessor"/> dispatches a <c>Remove</c> to it by an exact type match (distance 0)
/// — it does not contend with the <see cref="MembershipActivityHandler"/> (registered for the base
/// <see cref="Activity"/> type) for the activity.
/// </para>
/// <para>
/// <strong>Idempotent.</strong> <see cref="ICommunityStore.RemoveMemberAsync"/> is idempotent, so a
/// re-delivered <c>Remove</c> (at-least-once delivery, C-07) is safe to re-apply.
/// </para>
/// </remarks>
public sealed class RemoveActivityHandler : ActivityHandlerBase<Remove>
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new <see cref="RemoveActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="ICommunityStore"/>).</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> is null.</exception>
    public RemoveActivityHandler(IPersistenceProvider persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _persistence = persistence;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Remove remove, CancellationToken ct = default)
    {
        if (!await IsLocalCommunityAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
        {
            return;
        }

        // The community manages its own membership: the activity's actor must be the recipient community
        // (self-management — the same gate the community outbox publish endpoint applies). A Remove posted
        // by any other actor is not a membership edit of this community.
        var actorIri = remove.Actor?.FirstOrDefault().ResolveObjectIri();
        if (actorIri is not { } actor || actor != delivery.RecipientIri)
        {
            return;
        }

        var memberIri = remove.Object?.FirstOrDefault().ResolveObjectIri();
        if (memberIri is not { } resolvedMember)
        {
            return;
        }

        await _persistence.Communities
            .RemoveMemberAsync(delivery.RecipientIri, resolvedMember, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether <paramref name="recipientIri"/> is a local community (the only recipient type this
    /// handler interprets — the collection being modified must be owned by this instance).
    /// </summary>
    private Task<bool> IsLocalCommunityAsync(Iri recipientIri, CancellationToken ct)
        => _persistence.Communities.TryGetCommunityAsync(recipientIri, out _, ct);
}
