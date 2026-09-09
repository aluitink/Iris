using Iris.Core;
using Iris.Server.Security;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Inbox;

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
    private readonly LocalActorDocumentCache? _actorDocumentCache;

    /// <summary>
    /// Initializes a new <see cref="UpdateActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IObjectStore"/>).</param>
    /// <param name="localActors">Resolves whether the updating actor is a local actor.</param>
    /// <param name="propagation">The propagation service (schedules the <see cref="Update"/> to the
    /// author's remote followers, the federated half of F-02).</param>
    /// <param name="actorDocumentCache">The local actor document cache, invalidated after the stored
    /// actor (or community) is refreshed so the public <c>GET /ap/v1/u/{handle}</c> serves the updated
    /// document (not a stale cached copy). May be null in unit-test seams that do not exercise the
    /// document-serving path (the invalidation is then skipped).</param>
    /// <param name="logger">The logger (records the handler outcome). May be null.</param>
    /// <exception cref="ArgumentNullException">When a required argument is null.</exception>
    public UpdateActivityHandler(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        IDeletePropagationService propagation,
        LocalActorDocumentCache? actorDocumentCache = null,
        ILogger<UpdateActivityHandler>? logger = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        ArgumentNullException.ThrowIfNull(propagation);
        _persistence = persistence;
        _localActors = localActors;
        _propagation = propagation;
        _actorDocumentCache = actorDocumentCache;
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

        // An actor updating their own profile document: the embedded object is an Actor and the
        // object IRI matches the updating actor's IRI. Refresh the stored actor (preserving publicKey
        // and other ExtensionData the update does not carry), then propagate to remote followers.
        if (updated is Actor updatedActor && actorIri is { } actorRef && objectIri is { } objRef && actorRef.Value == objRef.Value)
        {
            await HandleActorUpdateAsync(updatedActor, actorRef, activity, ct).ConfigureAwait(false);
            return;
        }

        // Refresh only an object this instance actually stores (one created by a Create, or previously
        // stored). An object with no local record is not this instance's to update.
        if (!await _persistence.Objects.TryGetObjectAsync(objectIri.Value, out var stored, ct).ConfigureAwait(false))
        {
            return;
        }

        // Owner guard: the updating actor must own the stored object. Both local and remote actors are
        // checked: the stored object must be <c>attributedTo</c> the updating actor. A local author who
        // created the object on this instance is attributed to it (the <c>CreateActivityHandler</c> /
        // outbox-publish path stores the embedded object with its <c>attributedTo</c> set to the
        // author). A remote author who is the owner of a federated copy (stored via the outbound
        // <c>Create</c> federation) is likewise attributed. An actor updating an object it does not own
        // (a local actor forging an update to a remote actor's federated copy, or a remote actor
        // updating an object attributed to someone else) is rejected.
        var actorIsLocal = await _localActors.IsLocalActorAsync(actorIri.Value, ct).ConfigureAwait(false);
        if (stored is null || !IsAttributedTo(stored, actorIri))
        {
            return;
        }

        // Stamp `updated` on content objects (non-actor) so remote clients can detect edits.
        // Actor profile updates go through HandleActorUpdateAsync which uses field-merge semantics
        // and does not set `updated`. The `updated` timestamp is meaningful for content objects
        // (Notes, Articles) that carry a `published` timestamp.
        if (updated is ActivityObject contentObj && contentObj is not Actor)
        {
            var now = DateTime.UtcNow;
            var published = contentObj.Published;
            contentObj.Updated = published is { } pub && now < pub ? pub : now;
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
    /// Handles an actor updating their own profile document. Refreshes the stored actor's mutable
    /// fields (name, summary, icon, endpoints) from the embedded update, preserving the
    /// <c>publicKey</c> and any other <c>ExtensionData</c> the update does not carry. Propagates to
    /// remote followers when the actor is local.
    /// </summary>
    private async Task HandleActorUpdateAsync(Actor updated, Iri actorIri, Update activity, CancellationToken ct)
    {
        // Check the Actors store first (Person actors). If not found, fall back to the Communities
        // store (Group actors / communities), which are stored separately.
        if (await _persistence.Actors.TryGetActorAsync(actorIri, out var stored, ct).ConfigureAwait(false)
            && stored is not null)
        {
            MergeActorFields(stored, updated);
            await _persistence.Actors.PutActorAsync(stored, ct).ConfigureAwait(false);
        }
        else if (updated is Group updatedGroup
                 && await _persistence.Communities.TryGetCommunityAsync(actorIri, out var community, ct).ConfigureAwait(false)
                 && community is not null)
        {
            // Merge the mutable fields from the update into the stored community, preserving the
            // publicKey and any ExtensionData entries the update does not override.
            if (updatedGroup.Name is { } name && name.Any())
            {
                community.Name = name;
            }

            if (updatedGroup.Summary is { } summary && summary.Any())
            {
                community.Summary = summary;
            }

            // Same icon-merge semantics as <see cref="MergeActorFields"/>: non-empty sets, empty clears,
            // missing leaves unchanged.
            if (updatedGroup.Icon is { } icon)
            {
                community.Icon = icon.Any() ? icon : null;
            }

            if (updatedGroup.Endpoints is not null)
            {
                community.Endpoints = updatedGroup.Endpoints;
            }

            if (updatedGroup.ExtensionData is { Count: > 0 } extData)
            {
                community.ExtensionData ??= [];
                foreach (var (key, value) in extData)
                {
                    community.ExtensionData[key] = value;
                }
            }

            await _persistence.Communities.PutCommunityAsync(community, ct).ConfigureAwait(false);
        }
        else
        {
            return;
        }

        // Invalidate the local actor document cache so the public GET /ap/v1/u/{handle} serves the
        // updated document (name, summary, icon, …) rather than a stale cached copy. Without this, a
        // profile edit (e.g. setting the actor's icon) persists to the store but the served document
        // keeps showing the pre-edit value until the cache entry expires (60s fresh / 300s stale).
        _actorDocumentCache?.Invalidate(actorIri);

        var actorIsLocal = await _localActors.IsLocalActorAsync(actorIri, ct).ConfigureAwait(false);
        if (actorIsLocal)
        {
            await _propagation
                .PropagateUpdateAsync(actorIri, actorIri, activity, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Merges the mutable fields from <paramref name="updated"/> into <paramref name="stored"/>,
    /// preserving the <c>publicKey</c> and any <c>ExtensionData</c> entries the update does not carry.
    /// </summary>
    private static void MergeActorFields(Actor stored, Actor updated)
    {
        if (updated.Name is { } name && name.Any())
        {
            stored.Name = name;
        }

        if (updated.Summary is { } summary && summary.Any())
        {
            stored.Summary = summary;
        }

        // Icon merge semantics: a non-empty icon array sets the icon; an **empty** icon array clears it
        // (an explicit "remove avatar"); a missing icon field (null) leaves the stored icon unchanged
        // (a partial update that does not touch the icon). This lets the edit-profile form both set and
        // clear the avatar via the same Update path.
        if (updated.Icon is { } icon)
        {
            stored.Icon = icon.Any() ? icon : null;
        }

        if (updated.Endpoints is not null)
        {
            stored.Endpoints = updated.Endpoints;
        }

        if (updated.ExtensionData is { Count: > 0 } extData)
        {
            stored.ExtensionData ??= [];
            foreach (var (key, value) in extData)
            {
                stored.ExtensionData[key] = value;
            }
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
