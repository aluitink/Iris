using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Accept"/> activities: when a remote actor accepts a follow that a
/// <em>local</em> actor made, the local follow edge is finalized (recorded in the
/// <see cref="IFollowStore"/>).
/// </summary>
/// <remarks>
/// On the follower side, a follow is <em>provisional</em> until the followed side accepts it. The
/// <see cref="FollowActivityHandler"/> (on the followed side) schedules the <c>Accept</c>; when that
/// <c>Accept</c> is delivered back to the follower's inbox (this instance), this handler finalizes the
/// follow by recording the <c>follower → target</c> edge — but only when the follower is a local actor
/// (the local actor's own follow). The followed side's acceptance of a <em>remote</em> follower's
/// follow is owned by the remote instance, so a remote follower's <c>Accept</c> is a no-op here.
/// The <c>Accept</c>'s object references the original <c>Follow</c> (by IRI); the target is resolved
/// from that follow (fetched from the local activity store — the follower stored it when it sent the
/// follow). A missing target (the follow was never stored) is a no-op.
/// </remarks>
public sealed class AcceptActivityHandler : ActivityHandlerBase<Accept>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="AcceptActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/> and
    /// <see cref="IActivityStore"/>).</param>
    /// <param name="localActors">Resolves whether an actor IRI is a local actor.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> or
    /// <paramref name="localActors"/> is null.</exception>
    public AcceptActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Accept accept, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(accept);

        // The Accept is delivered to the follower's inbox (the Accept's actor is the followed side,
        // which accepted the follow). The follower — the local actor whose follow is being accepted —
        // is the delivery's recipient (the inbox the Accept was delivered to).
        var followerIri = delivery.RecipientIri;

        // Finalize only a local actor's own follow (a remote follower's follow is the remote
        // instance's concern).
        if (!await _localActors.IsLocalActorAsync(followerIri, ct).ConfigureAwait(false))
        {
            return;
        }

        // The Accept's object references the original Follow (by IRI). Resolve the follow's target
        // from the local activity store (the follower stored the follow when it sent it).
        var targetIri = await ResolveFollowTargetAsync(accept.Object?.FirstOrDefault(), ct).ConfigureAwait(false);
        if (!targetIri.HasValue)
        {
            return;
        }

        await _persistence.Follows
            .RecordFollowAsync(followerIri, targetIri.Value, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the target of the <see cref="Follow"/> that an <see cref="Accept"/> references, by
    /// fetching the original follow (referenced by IRI in the Accept's object) from the local activity
    /// store and reading its object (the followed actor).
    /// </summary>
    /// <param name="acceptObject">The Accept's object (a reference to the original Follow, by IRI).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the followed actor's IRI, or null when it could not be
    /// resolved (the follow was never stored, or carries no target).</returns>
    private async Task<Iri?> ResolveFollowTargetAsync(IObjectOrLink? acceptObject, CancellationToken ct)
    {
        var followIri = FollowIris.ResolveActorIri(acceptObject);
        if (!followIri.HasValue)
        {
            return null;
        }

        if (!await _persistence.Activities.TryGetActivityAsync(followIri.Value, out var storedFollow, ct)
            .ConfigureAwait(false) ||
            storedFollow is not Follow follow)
        {
            return null;
        }

        return FollowIris.ResolveActorIri(follow.Object?.FirstOrDefault());
    }
}
