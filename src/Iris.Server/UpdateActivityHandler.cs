using Iris.Core;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Server;

/// <summary>
/// Handles an inbound <see cref="Update"/> activity: when the updating actor is a local actor and the
/// referenced object is one this instance stores (in the <see cref="IObjectStore"/>), the stored object
/// is refreshed with the updated content.
/// </summary>
/// <remarks>
/// An <see cref="Update"/> carries the updated object in its <c>object</c> (either the full updated
/// object, embedded, or a reference to it). This handler is the server-side half of "a user edits their
/// post (or profile) and the change propagates" (F-02): the <see cref="CreateActivityHandler"/> stored
/// the original object in the <see cref="IObjectStore"/>, and this handler replaces it in place so a
/// later <c>GET</c> of the object's IRI serves the updated content (not stale data).
/// </remarks>
/// <para>
/// <strong>Owner guard.</strong> Only the object's owner (the activity's <c>actor</c>) may update it. The
/// handler requires that the actor is a <em>local</em> actor on this instance <em>and</em> that an object
/// with the referenced IRI is actually stored here; otherwise it is a no-op (an update for an object this
/// instance does not hold, or an update purporting to be from a remote actor, is not this instance's
/// concern). This prevents a remote actor from rewriting content it does not own.
/// </para>
/// <para>
/// <strong>Embedded vs. reference.</strong> When the updated object is embedded (the common case — the
/// actor sends the full updated object), it is stored directly (replacing the stored object under the
/// same IRI). When it is a bare <see cref="Link"/> reference, the handler leaves the stored object
/// unchanged (there is no new content to apply; a reference-only <see cref="Update"/> is not
/// interpreted). The updated object's <c>Id</c> must match the stored object's IRI; a mismatch is a
/// no-op (the handler does not silently re-store under a different IRI).
/// </para>
/// <para>
/// <strong>Federated propagation (the federated half of F-02).</strong> After refreshing the stored
/// object locally, the handler propagates the <see cref="Update"/> to the author's remote followers
/// via <see cref="IDeletePropagationService"/>. A local refresh is enough only while the object lives
/// on this instance; every remote instance that holds a copy (via the outbound <c>Create</c>
/// federation, Slice 11.7) must be told, or it keeps serving the pre-edit content.
/// </para>
public sealed class UpdateActivityHandler : ActivityHandlerBase<Update>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;
    private readonly IDeletePropagationService _propagation;

    /// <summary>
    /// Initializes a new <see cref="UpdateActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IObjectStore"/>).</param>
    /// <param name="localActors">Resolves whether the updating actor is a local actor.</param>
    /// <param name="propagation">The propagation service (schedules the <see cref="Update"/> to the
    /// author's remote followers, the federated half of F-02).</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public UpdateActivityHandler(
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
    public override async Task HandleAsync(InboxDelivery delivery, Update activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(activity);

        // The updating actor is the activity's actor.
        var actorIri = activity.Actor?.FirstOrDefault()?.ResolveObjectIri();
        if (!actorIri.HasValue)
        {
            return;
        }

        // The updated object: an embedded object is stored in place; a bare link reference is not
        // interpreted (there is no new content to apply).
        var updated = activity.ExtractEmbeddedObject();
        if (updated is null)
        {
            return;
        }

        var objectIri = updated.Id.ToIri();
        if (!objectIri.HasValue)
        {
            return;
        }

        // Refresh only an object this instance actually stores (one created by a Create, or previously
        // stored). An object with no local record is not this instance's to update.
        if (!await _persistence.Objects.TryGetObjectAsync(objectIri.Value, out var stored, ct).ConfigureAwait(false))
        {
            return;
        }

        // Owner guard: the updating actor must own the stored object. A <em>local</em> author is always
        // the owner of an object this instance stores (it created it); a <em>remote</em> author is
        // accepted only when this instance holds a copy of the author's object (stored via the outbound
        // <c>Create</c> federation) and the stored object is attributed to that author. This is the
        // federated half of F-02: a remote instance that received an author's post stores a copy and must
        // apply the author's later <c>Update</c> to it (the actor is remote, so it is not "local" here,
        // but it is the owner of this instance's copy). A remote actor updating an object it does not own
        // is rejected.
        var actorIsLocal = await _localActors.IsLocalActorAsync(actorIri.Value, ct).ConfigureAwait(false);
        if (!actorIsLocal && (stored is null || !IsAttributedTo(stored, actorIri)))
        {
            return;
        }

        await _persistence.Objects.PutObjectAsync(updated, ct).ConfigureAwait(false);

        // F-02 (federated half): propagate the Update to the author's remote followers so their copies
        // of the object are refreshed (a local refresh alone leaves remote instances serving stale
        // pre-edit content). Only the author's <em>home</em> instance (where the actor is local)
        // re-propagates: a remote instance that received the Update has already been told by the home
        // instance, so re-propagating here would fan out the update again (and this instance does not
        // own the author's follower set).
        if (actorIsLocal)
        {
            await _propagation
                .PropagateUpdateAsync(actorIri.Value, objectIri.Value, activity, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports whether the stored object is attributed to <paramref name="actorIri"/> (its
    /// <c>attributedTo</c> link resolves to that IRI). Used to accept a federated <see cref="Update"/>
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
