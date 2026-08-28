using Iris.Core;
using KristofferStrube.ActivityStreams;

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
public sealed class DeleteActivityHandler : ActivityHandlerBase<Delete>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="DeleteActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IObjectStore"/>).</param>
    /// <param name="localActors">Resolves whether the deleting actor is a local actor.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public DeleteActivityHandler(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
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

        // Owner guard: only a local actor may delete an object stored on this instance.
        if (!await _localActors.IsLocalActorAsync(actorIri.Value, ct).ConfigureAwait(false))
        {
            return;
        }

        // Tombstone only an object this instance actually stores (one created by a Create, or previously
        // stored). An object with no local record is not this instance's to delete. Read the stored
        // object first so the tombstone can record its formerType.
        if (!await _persistence.Objects.TryGetObjectAsync(objectIri.Value, out var stored, ct).ConfigureAwait(false))
        {
            return;
        }

        var formerType = stored?.Type?.FirstOrDefault();
        await _persistence.Objects
            .PutObjectAsync(objectIri.Value.BuildTombstone(formerType), ct)
            .ConfigureAwait(false);
    }
}
