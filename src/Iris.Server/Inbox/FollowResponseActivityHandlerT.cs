using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Base class for activity handlers that respond to a <see cref="Follow"/> — i.e. inbound
/// <see cref="Accept"/> and <see cref="Reject"/> activities. Encapsulates the shared logic:
/// resolve the follower from the delivery, guard for local-actor, resolve the original follow's
/// target from the activity store, and invoke the derived class's store operation.
/// </summary>
/// <typeparam name="TActivity">The activity type (either <see cref="Accept"/> or <see cref="Reject"/>).</typeparam>
public abstract class FollowResponseActivityHandler<TActivity> : ActivityHandlerBase<TActivity>
    where TActivity : Activity
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    protected IPersistenceProvider Persistence => _persistence;

    protected FollowResponseActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
    }

    /// <summary>
    /// Applies the follow-response store operation (record or remove the follow edge).
    /// </summary>
    /// <param name="followerIri">The IRI of the local actor whose follow is being finalized or undone.</param>
    /// <param name="targetIri">The IRI of the actor the follow is directed at.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the store operation has been applied.</returns>
    protected abstract Task ApplyAsync(Iri followerIri, Iri targetIri, CancellationToken ct);

    /// <summary>
    /// Reports whether the follow-response's recipient (the follower) is local. The default is the
    /// person-store check (<see cref="ILocalActorResolver.IsLocalActorAsync(Iri, CancellationToken)"/>);
    /// a derived handler widens this when the follower may also be a local community (a
    /// <see cref="Group"/> actor not in the person store) — see <see cref="AcceptActivityHandler"/>.
    /// </summary>
    /// <param name="followerIri">The IRI of the follower (the delivery's recipient).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the follower is local (the follow-response is this instance's
    /// concern); otherwise <see langword="false"/> (a remote follower's follow is owned elsewhere).</returns>
    protected virtual Task<bool> IsLocalRecipientAsync(Iri followerIri, CancellationToken ct)
        => _localActors.IsLocalActorAsync(followerIri, ct);

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, TActivity activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(activity);

        var followerIri = delivery.RecipientIri;

        if (!await IsLocalRecipientAsync(followerIri, ct).ConfigureAwait(false))
        {
            return;
        }

        var targetIri = await ResolveFollowTargetAsync(activity.Object?.FirstOrDefault(), ct).ConfigureAwait(false);
        if (!targetIri.HasValue)
        {
            return;
        }

        await ApplyAsync(followerIri, targetIri.Value, ct).ConfigureAwait(false);
    }

    private async Task<Iri?> ResolveFollowTargetAsync(IObjectOrLink? responseObject, CancellationToken ct)
    {
        var followIri = responseObject.ResolveObjectIri();
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

        return follow.Object?.FirstOrDefault().ResolveObjectIri();
    }
}
