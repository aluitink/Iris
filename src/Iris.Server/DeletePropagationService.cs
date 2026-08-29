using Iris.Core;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Server;

/// <summary>
/// The default <see cref="IDeletePropagationService"/>: computes the remote propagation target set
/// for an object <see cref="Update"/> / <see cref="Delete"/> (the author's remote followers, the
/// remote attributedTo, and the remote parents of a deleted reply) and schedules the activity for
/// delivery to each via <see cref="IDeliveryService"/>.
/// </summary>
/// <remarks>
/// See <see cref="IDeletePropagationService"/> for the target-computation rules and the determinism
/// notes. This implementation reads the object's <c>attributedTo</c> (the author) and
/// <c>inReplyTo</c> (the parents, <see cref="IriExtensions.GetParentIri"/>) from the stored object,
/// enumerates the author's followers from the local <see cref="IFollowStore"/>, and resolves each
/// candidate target with the <see cref="ILocalActorResolver"/> (local targets are skipped — their
/// copies are refreshed / tombstoned locally by the calling handler).
/// </remarks>
public sealed class DeletePropagationService : IDeletePropagationService
{
    private readonly IPersistenceProvider _persistence;
    private readonly IDeliveryService _delivery;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="DeletePropagationService"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/> and
    /// <see cref="IReplyStore"/>).</param>
    /// <param name="delivery">The delivery service (schedules the activity to each remote target's
    /// inbox, signed as the author).</param>
    /// <param name="localActors">Resolves whether a candidate target is a local actor (local targets
    /// are skipped).</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public DeletePropagationService(
        IPersistenceProvider persistence,
        IDeliveryService delivery,
        ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _delivery = delivery;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public async Task PropagateUpdateAsync(Iri authorIri, Iri objectIri, Update activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);

        // The targets of an Update are the author's remote followers (the actors who saw the object
        // via the outbound Create federation). An Update does not change the object's reply edges, so
        // no parent targets.
        var targets = new List<Iri>();
        await AddRemoteFollowersAsync(authorIri, targets, ct).ConfigureAwait(false);

        await DeliverToTargetsAsync(targets, activity, authorIri, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task PropagateDeleteAsync(
        Iri authorIri,
        Iri objectIri,
        Delete activity,
        IObject? parentObject = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var targets = new List<Iri>();
        await AddRemoteFollowersAsync(authorIri, targets, ct).ConfigureAwait(false);

        // A deleted reply also needs to tell the parent's owner (when the parent is remote-owned)
        // that the reply is gone (the parent's replies collection, F-12). The parent object is read
        // from the stored object's inReplyTo <em>before</em> the object is tombstoned (a Tombstone
        // carries no inReplyTo), so the handler passes it in; the Delete itself references the object
        // by IRI only. The parent's owner is its attributedTo (the actor who owns the parent object and
        // therefore holds its replies collection). A local parent's owner is skipped (the edge is
        // removed locally by the handler).
        var parentOwner = parentObject is { } parent ? GetOwnerIri(parent) : null;
        if (parentOwner is { } owner
            && !await IsLocalAsync(owner, ct).ConfigureAwait(false))
        {
            targets.Add(owner);
        }

        await DeliverToTargetsAsync(targets, activity, authorIri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves an object's owner (the IRI its <c>attributedTo</c> resolves to), or null when the object
    /// has no resolvable <c>attributedTo</c>.
    /// </summary>
    private static Iri? GetOwnerIri(IObject obj)
    {
        var attributedTo = (obj as ActivityObject)?.AttributedTo?.FirstOrDefault();
        return attributedTo is not null ? attributedTo.ResolveObjectIri() : null;
    }

    /// <summary>
    /// Adds the author's remote followers to the target set (local followers are skipped — their copy
    /// is on this instance).
    /// </summary>
    private async Task AddRemoteFollowersAsync(Iri authorIri, List<Iri> targets, CancellationToken ct)
    {
        var followers = await _persistence.Follows
            .GetFollowersAsync(authorIri, ct)
            .ConfigureAwait(false);
        foreach (var followerIri in followers)
        {
            if (await IsLocalAsync(followerIri, ct).ConfigureAwait(false))
            {
                continue;
            }

            targets.Add(followerIri);
        }
    }

    /// <summary>
    /// Delivers the activity to each target (deduplicated), signed as the author.
    /// </summary>
    private async Task DeliverToTargetsAsync(List<Iri> targets, Activity activity, Iri authorIri, CancellationToken ct)
    {
        foreach (var targetIri in targets.Distinct())
        {
            await _delivery
                .DeliverToActorAsync(targetIri, activity, authorIri, ct)
                .ConfigureAwait(false);
        }
    }

    private Task<bool> IsLocalAsync(Iri iri, CancellationToken ct)
        => _localActors.IsLocalActorAsync(iri, ct);
}
