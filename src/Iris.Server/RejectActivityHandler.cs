using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Reject"/> activities: when a remote actor rejects a follow that a
/// <em>local</em> actor made, the local follow edge is removed (undone) from the
/// <see cref="IFollowStore"/>.
/// </summary>
/// <remarks>
/// Mirrors the <see cref="AcceptActivityHandler"/>: on the follower side a follow is provisional until
/// the followed side responds. A <c>Reject</c> delivered back to the follower's inbox removes the
/// <c>follower → target</c> edge — but only when the follower is a local actor. The target is resolved
/// from the original <c>Follow</c> (referenced by IRI in the Reject's object, fetched from the local
/// activity store). A missing target (the follow was never stored) is a no-op (there is no edge to
/// remove).
/// </remarks>
public sealed class RejectActivityHandler : ActivityHandlerBase<Reject>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="RejectActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/> and
    /// <see cref="IActivityStore"/>).</param>
    /// <param name="localActors">Resolves whether an actor IRI is a local actor.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> or
    /// <paramref name="localActors"/> is null.</exception>
    public RejectActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Reject reject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(reject);

        // The Reject is delivered to the follower's inbox (the Reject's actor is the followed side,
        // which rejected the follow). The follower — the local actor whose follow is being rejected —
        // is the delivery's recipient (the inbox the Reject was delivered to).
        var followerIri = delivery.RecipientIri;

        // Undo only a local actor's own follow (a remote follower's follow is the remote instance's
        // concern).
        if (!await _localActors.IsLocalActorAsync(followerIri, ct).ConfigureAwait(false))
        {
            return;
        }

        // The Reject's object references the original Follow (by IRI). Resolve the follow's target
        // from the local activity store (the follower stored the follow when it sent it).
        var targetIri = await ResolveFollowTargetAsync(reject.Object?.FirstOrDefault(), ct).ConfigureAwait(false);
        if (!targetIri.HasValue)
        {
            return;
        }

        await _persistence.Follows
            .RemoveFollowAsync(followerIri, targetIri.Value, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the target of the <see cref="Follow"/> that a <see cref="Reject"/> references, by
    /// fetching the original follow (referenced by IRI in the Reject's object) from the local activity
    /// store and reading its object (the followed actor).
    /// </summary>
    /// <param name="rejectObject">The Reject's object (a reference to the original Follow, by IRI).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the followed actor's IRI, or null when it could not be
    /// resolved (the follow was never stored, or carries no target).</returns>
    private async Task<Iri?> ResolveFollowTargetAsync(IObjectOrLink? rejectObject, CancellationToken ct)
    {
        var followIri = FollowIris.ResolveActorIri(rejectObject);
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
