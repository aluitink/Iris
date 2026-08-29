using Iris.Core;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Server;

/// <summary>
/// Handles an inbound <see cref="Delete"/> activity: when the deleting actor is a local actor and the
/// referenced object is one this instance stores, the stored object is replaced by a
/// <see cref="Tombstone"/> (the AS2.0 "deleted" marker, F-03/F-10).
/// </summary>
/// <remarks>
/// A <see cref="Delete"/> references the object being deleted (a bare <see cref="Link"/> to its IRI is
/// the common case; an embedded object is also accepted). This handler is the server-side half of "a
/// user deletes their post and the change propagates": the object is not hard-removed but <em>replaced
/// by a tombstone</em> under the same IRI, so a later <c>GET</c> of the object's IRI serves the
/// <see cref="Tombstone"/> ({"type":"Tombstone","id":…,"formerType":[…]}), not a <c>404</c> — the
/// spec's "deleted" marker (F-10).
/// </remarks>
/// <para>
/// <strong>Owner guard.</strong> Only the object's owner (the activity's <c>actor</c>) may delete it. The
/// handler requires that the actor is a <em>local</em> actor on this instance <em>and</em> that an object
/// with the referenced IRI is actually stored here; otherwise it is a no-op (a delete for an object this
/// instance does not hold, or a delete purporting to be from a remote actor, is not this instance's
/// concern). This prevents a remote actor from tombstoning content it does not own.
/// </para>
/// <para>
/// <strong>Tombstone <c>formerType</c>.</strong> When the deleted object was stored (an
/// <see cref="IObject"/>), its AS2.0 <c>type</c> is recorded in the tombstone's <c>formerType</c> so a
/// client can tell what was deleted. When the stored object cannot be read (should not happen after the
/// guard), the tombstone omits <c>formerType</c>.
/// </para>
/// <para>
/// <strong>Federated propagation (the federated half of F-03).</strong> After tombstoning the object
/// locally, the handler propagates the <see cref="Delete"/> to the remote actors that need to see the
/// tombstone — the author's remote followers, the remote attributedTo, and (for a deleted reply) the
/// remote parent's owner — via <see cref="IDeletePropagationService"/>. A local copy is enough only
/// while the object lives on this instance; every remote instance that holds a copy (via the outbound
/// <c>Create</c> federation) must be told, or it keeps serving the pre-delete content instead of the
/// <see cref="Tombstone"/>.
/// </para>
/// <para>
/// <strong>Reply-edge cleanup (F-12).</strong> When the deleted object is a reply (it has an
/// <see cref="IriExtensions.GetParentIri"/>, i.e. its stored <c>inReplyTo</c> is set), the local
/// parent → child reply edge is removed from the <see cref="IReplyStore"/> so the parent's
/// <c>replies</c> collection no longer lists the deleted reply. (The remote parent's edge — if the
/// parent is remote-owned — is the target of the propagation; this instance's edge is local state.)
/// </para>
public sealed class DeleteActivityHandler : ActivityHandlerBase<Delete>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;
    private readonly IDeletePropagationService _propagation;

    /// <summary>
    /// Initializes a new <see cref="DeleteActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IObjectStore"/> and
    /// <see cref="IReplyStore"/>).</param>
    /// <param name="localActors">Resolves whether the deleting actor is a local actor.</param>
    /// <param name="propagation">The propagation service (schedules the <see cref="Delete"/> to the
    /// remote actors that need the tombstone, the federated half of F-03).</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public DeleteActivityHandler(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        IDeletePropagationService propagation)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        ArgumentNullException.ThrowIfNull(propagation);
        _persistence = persistence;
        _localActors = localActors;
        _propagation = propagation;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Delete activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(activity);

        // The deleting actor is the activity's actor.
        var actorIri = activity.Actor?.FirstOrDefault()?.ResolveObjectIri();
        if (!actorIri.HasValue)
        {
            return;
        }

        // The object being deleted: a bare link reference (the common case) or an embedded object.
        var objectIri = activity.Object?.FirstOrDefault()?.ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            return;
        }

        // Tombstone only an object this instance actually stores (one created by a Create, or previously
        // stored). An object with no local record is not this instance's to delete. Read the stored
        // object first so the tombstone can record its formerType and so the owner guard (below) can
        // check attribution for a federated delete.
        if (!await _persistence.Objects.TryGetObjectAsync(objectIri.Value, out var stored, ct).ConfigureAwait(false))
        {
            return;
        }

        // Owner guard: the deleting actor must own the stored object. A <em>local</em> author is always
        // the owner of an object this instance stores (it created it); a <em>remote</em> author is
        // accepted only when this instance holds a copy of the author's object (stored via the outbound
        // <c>Create</c> federation) and the stored object is attributed to that author. This is the
        // federated half of F-03: a remote instance that received an author's post stores a copy and must
        // apply the author's later <c>Delete</c> to it (the actor is remote, so it is not "local" here,
        // but it is the owner of this instance's copy). A remote actor deleting an object it does not own
        // is rejected.
        var actorIsLocal = await _localActors.IsLocalActorAsync(actorIri.Value, ct).ConfigureAwait(false);
        if (!actorIsLocal && (stored is null || !IsAttributedTo(stored, actorIri)))
        {
            return;
        }

        // Capture the deleted object's parent <em>object</em> (its inReplyTo, read from the store)
        // <em>before</em> tombstoning: a Tombstone carries no inReplyTo, and the propagation (F-12)
        // needs the parent's owner (its attributedTo) to tell the remote parent's owner the reply is
        // gone. The inReplyTo on the deleted object is typically a bare Link (no attributedTo), so the
        // parent object is fetched from the store to resolve the owner.
        Iri? parentIri = stored?.GetParentIri();
        IObject? parentObject = null;
        if (parentIri is { } parentIriValue
            && await _persistence.Objects
                .TryGetObjectAsync(parentIriValue, out var parentStored, ct)
                .ConfigureAwait(false))
        {
            parentObject = parentStored;
        }

        var formerType = stored?.Type?.FirstOrDefault();
        await _persistence.Objects
            .PutObjectAsync(objectIri.Value.BuildTombstone(formerType), ct)
            .ConfigureAwait(false);

        // F-12: when the deleted object is a reply, remove the local parent → child reply edge so the
        // parent's replies collection no longer lists it. (The edge was recorded by the
        // CreateActivityHandler from the object's inReplyTo.)
        if (parentIri is { } parent)
        {
            await _persistence.Replies
                .RemoveReplyAsync(parent, objectIri.Value, ct)
                .ConfigureAwait(false);
        }

        // F-03 (federated half): propagate the Delete to the remote actors that hold a copy of the
        // object (the author's remote followers, the remote attributedTo, and the remote parent's
        // owner when the object is a reply) so their copies are tombstoned too. Only the author's
        // <em>home</em> instance (where the actor is local) re-propagates: a remote instance that
        // received the Delete has already been told by the home instance, so re-propagating here would
        // fan out the delete again (and this instance does not own the author's follower set).
        if (actorIsLocal)
        {
            await _propagation
                .PropagateDeleteAsync(actorIri.Value, objectIri.Value, activity, parentObject, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports whether the stored object is attributed to <paramref name="actorIri"/> (its
    /// <c>attributedTo</c> link resolves to that IRI). Used to accept a federated <see cref="Delete"/>
    /// from a remote owner of a copy this instance holds.
    /// </summary>
    private static bool IsAttributedTo(IObject stored, Iri? actorIri)
    {
        if (actorIri is not { } actor)
        {
            return false;
        }

        var attributed = (stored as ActivityObject)?.AttributedTo?.FirstOrDefault();
        if (attributed is null)
        {
            return false;
        }

        var iri = attributed.ResolveObjectIri();
        return iri is { } a && string.Equals(a.Value, actor.Value, StringComparison.Ordinal);
    }
}
