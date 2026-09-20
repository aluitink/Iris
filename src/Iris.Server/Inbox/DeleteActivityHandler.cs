using Iris.Core;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles an inbound <see cref="Delete"/> activity: when the deleting actor is authorized and the
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
/// <strong>Authorization guard (138.23).</strong> The deleting actor must be authorized to delete the
/// stored object. Two cases are accepted:
/// <list type="bullet">
/// <item><em>Author delete:</em> the actor is the object's <c>attributedTo</c> owner (or a local actor).
/// This is the permanent author-delete case.</item>
/// <item><em>Mod removal:</em> the actor is a member of a community referenced in the object's
/// <c>to</c>/<c>cc</c> array (the Lemmy moderator-removal case, where a community moderator deletes
/// another member's post). The tombstone records <c>iris:removedBy</c> so the UI can distinguish the
/// two cases.</item>
/// </list>
/// A remote actor that is neither the owner nor a member of an associated community is rejected.
/// </para>
/// <para>
/// <strong>Tombstone <c>formerType</c> and <c>iris:removedBy</c>.</strong> When the deleted object was
/// stored (an <see cref="IObject"/>), its AS2.0 <c>type</c> is recorded in the tombstone's
/// <c>formerType</c> so a client can tell what was deleted. The <c>iris:removedBy</c> extension records
/// the deleting actor's IRI, distinguishing an author delete (actor == attributedTo) from a mod removal
/// (actor is a community member, not the owner).
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
/// Conversely, when the deleted object is a <em>parent</em> (it has replies to it), the thread under it
/// is collapsed: each child's parent → child reply edge is removed so the tombstoned parent's
/// <c>replies</c> collection is empty (136.9 — without this, a deleted post leaves its replies orphaned
/// but still served under the now-tombstoned parent). The child objects remain stored (fetchable by
/// direct IRI); only the thread listing is collapsed.
/// </para>
public sealed class DeleteActivityHandler : ActivityHandlerBase<Delete>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;
    private readonly IDeletePropagationService _propagation;

    /// <summary>
    /// Initializes a new <see cref="DeleteActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IObjectStore"/>,
    /// <see cref="IReplyStore"/>, and <see cref="ICommunityStore"/>).</param>
    /// <param name="localActors">Resolves whether the deleting actor is a local actor.</param>
    /// <param name="propagation">The propagation service (schedules the <see cref="Delete"/> to the
    /// remote actors that need the tombstone, the federated half of F-03).</param>
    /// <param name="logger">The logger (records the handler outcome). May be null.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public DeleteActivityHandler(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        IDeletePropagationService propagation,
        ILogger<DeleteActivityHandler>? logger = null)
        : base(logger)
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
        // object first so the tombstone can record its formerType and so the authorization guard (below)
        // can check attribution / community membership for a federated delete.
        if (!await _persistence.Objects.TryGetObjectAsync(objectIri.Value, out var stored, ct).ConfigureAwait(false))
        {
            return;
        }

        // Authorization guard (138.23): the deleting actor must be authorized to delete the stored object.
        // (a) A local actor is always authorized.
        // (b) A remote actor is authorized if it is the object's attributedTo owner (author delete).
        // (c) A remote actor is authorized if it is a member of a community referenced in the object's
        //     to/cc array (mod removal — the Lemmy moderator-removal case).
        var actorIsLocal = await _localActors.IsLocalActorAsync(actorIri.Value, ct).ConfigureAwait(false);
        var isAuthor = stored is not null && IsAttributedTo(stored, actorIri);
        var isModRemoval = !actorIsLocal && !isAuthor && await IsCommunityMemberOfAssociatedCommunityAsync(stored, actorIri.Value, ct).ConfigureAwait(false);
        if (!actorIsLocal && !isAuthor && !isModRemoval)
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
        var tombstone = objectIri.Value.BuildTombstone(formerType);
        // 138.23: record the deleting actor's IRI on the tombstone so the UI can distinguish an
        // author delete (no removedBy) from a mod removal (removedBy present).
        if (!isAuthor && !actorIsLocal)
        {
            tombstone.ExtensionData ??= new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>();
            var removedBy = System.Text.Json.JsonDocument.Parse($"\"{actorIri.Value}\"").RootElement.Clone();
            tombstone.ExtensionData[IrisExtensionTerms.RemovedBy] = removedBy;
        }
        await _persistence.Objects
            .PutObjectAsync(tombstone, ct)
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

        // F-12 (136.9): when the deleted object is a <em>parent</em> (it has replies), collapse the thread
        // under it: remove each child's parent → child reply edge so the tombstoned parent's
        // <c>replies</c> collection is empty. Without this, deleting a post leaves its replies orphaned
        // but still listed (and served) under the now-tombstoned parent — a stale, still-visible thread.
        // The child objects themselves remain stored (fetchable by direct IRI); only the thread listing
        // is collapsed. This is local state; the federated half is the Delete propagation below, which
        // delivers the Delete to the remote instances holding copies so they apply the same cleanup.
        foreach (var childIri in await _persistence.Replies
                .GetRepliesAsync(objectIri.Value, ct)
                .ConfigureAwait(false))
        {
            await _persistence.Replies
                .RemoveReplyAsync(objectIri.Value, childIri, ct)
                .ConfigureAwait(false);
        }

        // Remove the deleted object's Create from the author's outbox so the outbox collection no longer
        // lists the deleted content (the inverse of the AddToOutboxAsync the Create handler recorded).
        // Decision 055: the Create IRI is resolved by lookup in the object → Create index (recorded at
        // Create time), not derived from the object IRI — the note's ULID and its Create's ULID are
        // independent, so the old "sibling by last segment" derivation no longer holds. A missing link
        // (object not created through a Create this instance recorded) is a no-op.
        // 138.23: for a mod-removal the deleting actor is the moderator, not the author. The outbox
        // entry was recorded under the author's IRI (the object's attributedTo), so resolve the
        // author from the stored object, not from the activity's actor.
        if (await _persistence.Creates
                .TryGetCreateIriAsync(objectIri.Value, ct)
                .ConfigureAwait(false) is { } createIri)
        {
            var outboxOwnerIri = ResolveOutboxOwnerIri(stored, actorIri);
            await _persistence.Activities
                .RemoveFromOutboxAsync(outboxOwnerIri, createIri, ct)
                .ConfigureAwait(false);
            await _persistence.Creates
                .RemoveAsync(objectIri.Value, ct)
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
    /// from a remote owner of a copy this instance holds (the author-delete case).
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

    /// <summary>
    /// Resolves the IRI of the actor whose outbox the deleted object's <c>Create</c> was recorded in.
    /// For an author delete (or a local delete) this is the deleting actor. For a mod-removal this is
    /// the object's <c>attributedTo</c> owner (the author), since the <c>Create</c> was recorded in the
    /// author's outbox, not the moderator's.
    /// </summary>
    private static Iri ResolveOutboxOwnerIri(IObject? stored, Iri? actorIri)
    {
        if (stored is ActivityObject obj)
        {
            var attributed = obj.AttributedTo?.FirstOrDefault();
            if (attributed is not null && attributed.ResolveObjectIri() is { } attrIri)
            {
                return attrIri;
            }
        }

        return actorIri ?? new Iri(string.Empty);
    }

    /// <summary>
    /// Reports whether <paramref name="actorIri"/> is a member of any community referenced in the
    /// stored object's <c>to</c> or <c>cc</c> array (the mod-removal case, 138.23). A Lemmy moderator
    /// who removes a community member's post sends a <c>Delete</c> with the moderator as actor; the
    /// stored object's <c>to</c>/<c>cc</c> names the community, and the moderator is a member of that
    /// community.
    /// </summary>
    private async Task<bool> IsCommunityMemberOfAssociatedCommunityAsync(
        IObject? stored, Iri actorIri, CancellationToken ct)
    {
        if (stored is not ActivityObject obj)
        {
            return false;
        }

        foreach (var audience in (obj.To ?? []) .Concat(obj.Cc ?? []))
        {
            var communityIri = audience.ResolveObjectIri();
            if (communityIri is not { } ci)
            {
                continue;
            }

            // Members are followers (change 221): membership is the community's followers set.
            var members = await _persistence.Communities.GetFollowersAsync(ci, ct).ConfigureAwait(false);
            if (members.Contains(actorIri))
            {
                return true;
            }
        }

        return false;
    }
}
