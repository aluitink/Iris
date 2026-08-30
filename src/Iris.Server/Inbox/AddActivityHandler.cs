using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Add"/> activities: the ActivityStreams primitive for adding an item to a
/// collection. When the <em>recipient</em> (the inbox the activity was delivered to) is a local
/// <see cref="Group"/> (community), the activity's <c>object</c> (the item being added) is added to the
/// community's member set via the <see cref="ICommunityStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope — community membership.</strong> <c>Add</c> is the spec's generic collection-modification
/// primitive (AS2.0). Its most common federation use is a server that represents a community's membership
/// as an <c>Add</c> of a member to the community's <c>followers</c> (or <c>members</c>) collection, in
/// contrast to the <see cref="FollowActivityHandler"/>-based membership Iris otherwise records (F-09).
/// This handler interprets that case: an <c>Add</c> whose recipient is a local community adds the
/// <c>object</c> as a member.
/// </para>
/// <para>
/// <strong>Recipient is the community (the collection's owner).</strong> A collection-modifying activity
/// is delivered to the <em>owner of the collection being modified</em> — here, the community (the
/// <see cref="InboxDelivery.RecipientIri"/>). The <c>actor</c> is the server performing the edit (often
/// the same community, or a federating peer) and the <c>target</c>/<c>instrument</c> is the collection;
/// neither gates the interpretation — only the recipient must be a local community. When the recipient is
/// a local <em>person</em> (not a community) the activity is a no-op: a person's <c>followers</c> is
/// maintained by the follow lifecycle (<see cref="FollowActivityHandler"/> /
/// <see cref="AcceptActivityHandler"/>), not by <c>Add</c>. When the recipient is remote (neither a local
/// community nor person) the activity is not this instance's concern.
/// </para>
/// <para>
/// <strong>Exact-type dispatch.</strong> This handler derives from <see cref="ActivityHandlerBase{Add}"/>,
/// so the <see cref="InboxProcessor"/> dispatches an <c>Add</c> to it by an exact type match (distance 0)
/// — it does not contend with the <see cref="MembershipActivityHandler"/> (registered for the base
/// <see cref="Activity"/> type) for the activity.
/// </para>
/// <para>
/// <strong>Idempotent.</strong> <see cref="ICommunityStore.AddMemberAsync"/> is idempotent, so a
/// re-delivered <c>Add</c> (at-least-once delivery, C-07) is safe to re-apply.
/// </para>
/// </remarks>
public sealed class AddActivityHandler : ActivityHandlerBase<Add>
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new <see cref="AddActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="ICommunityStore"/>).</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> is null.</exception>
    public AddActivityHandler(IPersistenceProvider persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _persistence = persistence;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Add add, CancellationToken ct = default)
    {
        if (!await IsLocalCommunityAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
        {
            return;
        }

        var memberIri = add.Object?.FirstOrDefault().ResolveObjectIri();
        if (memberIri is not { } resolvedMember)
        {
            return;
        }

        await _persistence.Communities
            .AddMemberAsync(delivery.RecipientIri, resolvedMember, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether <paramref name="recipientIri"/> is a local community (the only recipient type this
    /// handler interprets — the collection being modified must be owned by this instance).
    /// </summary>
    private Task<bool> IsLocalCommunityAsync(Iri recipientIri, CancellationToken ct)
        => _persistence.Communities.TryGetCommunityAsync(recipientIri, out _, ct);
}
