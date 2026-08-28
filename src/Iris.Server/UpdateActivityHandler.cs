using Iris.Core;
using KristofferStrube.ActivityStreams;

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
public sealed class UpdateActivityHandler : ActivityHandlerBase<Update>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="UpdateActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IObjectStore"/>).</param>
    /// <param name="localActors">Resolves whether the updating actor is a local actor.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public UpdateActivityHandler(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
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

        // Owner guard: only a local actor may update an object stored on this instance.
        if (!await _localActors.IsLocalActorAsync(actorIri.Value, ct).ConfigureAwait(false))
        {
            return;
        }

        // Refresh only an object this instance actually stores (one created by a Create, or previously
        // stored). An object with no local record is not this instance's to update.
        if (!await _persistence.Objects.TryGetObjectAsync(objectIri.Value, out _, ct).ConfigureAwait(false))
        {
            return;
        }

        await _persistence.Objects.PutObjectAsync(updated, ct).ConfigureAwait(false);
    }
}
