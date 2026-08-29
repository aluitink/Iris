using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Add"/> and <see cref="Remove"/> activities: the ActivityStreams primitives
/// for modifying a collection. When the <em>recipient</em> (the inbox the activity was delivered to) is a
/// local <see cref="Group"/> (community), the activity's <c>object</c> (the item being added/removed) is
/// added to / removed from the community's member set via the <see cref="ICommunityStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope — community membership.</strong> <c>Add</c>/<c>Remove</c> are the spec's generic
/// collection-modification primitives (AS2.0). Their most common federation use is a server that
/// represents a community's membership as an <c>Add</c> of a member to the community's <c>followers</c>
/// (or <c>members</c>) collection, in contrast to the <see cref="Follow"/>-based membership Iris
/// otherwise records (F-09). This handler interprets that case: an <c>Add</c> whose recipient is a local
/// community adds the <c>object</c> as a member; a <c>Remove</c> whose recipient is a local community
/// removes it. A single <see cref="ActivityHandlerBase{TActivity}"/> cannot be parameterized over two
/// activity types, so the handler derives from the non-generic <see cref="IActivityHandler"/> and pattern
/// matches the <see cref="Add"/>/<see cref="Remove"/> at dispatch — the <see cref="InboxProcessor"/>
/// still prefers it over the <see cref="CommunityInboxActivityHandler"/> (registered for
/// <see cref="Activity"/>) because it is the most specific handler for both <c>Add</c> and <c>Remove</c>.
/// </para>
/// <para>
/// <strong>Recipient is the community (the collection's owner).</strong> A collection-modifying activity
/// is delivered to the <em>owner of the collection being modified</em> — here, the community (the
/// <see cref="InboxDelivery.RecipientIri"/>). The <c>actor</c> is the server performing the edit (often
/// the same community, or a federating peer) and the <c>target</c>/<c>instrument</c> is the collection;
/// neither gates the interpretation — only the recipient must be a local community. When the recipient is
/// a local <em>person</em> (not a community) the activity is a no-op: a person's <c>followers</c> is
/// maintained by the follow lifecycle (<see cref="FollowActivityHandler"/> /
/// <see cref="AcceptActivityHandler"/>), not by <c>Add</c>/<c>Remove</c>. When the recipient is remote (neither
/// a local community nor person) the activity is not this instance's concern.
/// </para>
/// <para>
/// <strong>Idempotent.</strong> <see cref="ICommunityStore.AddMemberAsync"/> and
/// <see cref="ICommunityStore.RemoveMemberAsync"/> are idempotent, so a re-delivered <c>Add</c>/<c>Remove</c>
/// (at-least-once delivery, C-07) is safe to re-apply.
/// </para>
/// </remarks>
public sealed class AddRemoveActivityHandler : IActivityHandler
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new <see cref="AddRemoveActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="ICommunityStore"/>).</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> is null.</exception>
    public AddRemoveActivityHandler(IPersistenceProvider persistence)
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

        // The processor only dispatches here for an Add or a Remove (the most specific registered handler
        // for both types), so any other activity reaching this dispatch is a programming error.
        switch (activity)
        {
            case Add add:
                return HandleAddAsync(delivery, add, ct);
            case Remove remove:
                return HandleRemoveAsync(delivery, remove, ct);
            default:
                throw new InvalidOperationException(
                    $"Activity of type {activity.GetType().Name} is not an Add or Remove.");
        }
    }

    /// <summary>
    /// Interprets an inbound <see cref="Add"/>: when the recipient is a local community, adds the
    /// activity's <c>object</c> to the community's member set.
    /// </summary>
    private async Task HandleAddAsync(InboxDelivery delivery, Add add, CancellationToken ct)
    {
        if (!await IsLocalCommunityAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
        {
            return;
        }

        var memberIri = add.Object?.FirstOrDefault().ResolveObjectIri();
        if (!memberIri.HasValue)
        {
            return;
        }

        await _persistence.Communities
            .AddMemberAsync(delivery.RecipientIri, memberIri.Value, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Interprets an inbound <see cref="Remove"/>: when the recipient is a local community, removes the
    /// activity's <c>object</c> from the community's member set.
    /// </summary>
    private async Task HandleRemoveAsync(InboxDelivery delivery, Remove remove, CancellationToken ct)
    {
        if (!await IsLocalCommunityAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
        {
            return;
        }

        var memberIri = remove.Object?.FirstOrDefault().ResolveObjectIri();
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
    /// handler interprets — the collection being modified must be owned by this instance).
    /// </summary>
    private Task<bool> IsLocalCommunityAsync(Iri recipientIri, CancellationToken ct)
        => _persistence.Communities.TryGetCommunityAsync(recipientIri, out _, ct);
}
