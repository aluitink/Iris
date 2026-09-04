using Iris.Core;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Server.Inbox;

/// <summary>
/// Applies an inbound <see cref="Tombstone"/> (the AS2.0 "deleted" marker, F-10) to this instance's
/// stores: replaces any stored object under the tombstone's IRI with the tombstone and cleans up the
/// local artifacts (the reply edge, the author's outbox <see cref="Create"/>, and the object →
/// <see cref="Create"/> index) that a prior copy of the object left behind.
/// </summary>
/// <remarks>
/// A peer signals that an object was deleted on <em>its</em> instance by delivering a
/// <see cref="Tombstone"/> — either a <em>standalone</em> <c>Tombstone</c> posted to a follower's inbox,
/// or a <see cref="Create"/> whose embedded <c>object</c> is a <c>Tombstone</c>. The serving side of
/// F-10 already works (the object endpoint serves a stored <c>Tombstone</c> under its IRI); this helper
/// is the missing <em>inbound</em> half — recognizing the tombstone and storing it under the object IRI
/// so a subsequent <c>GET</c> serves the tombstone, not stale content or a <c>404</c>.
/// <para>
/// The cleanup mirrors <see cref="DeleteActivityHandler"/>: the object is not hard-removed but
/// <em>replaced by a tombstone</em> under the same IRI, the reply edge (F-12) is removed so the parent's
/// <c>replies</c> collection no longer lists it, and the object's originating <see cref="Create"/> is
/// removed from the author's outbox + the object → Create index (decision 055) so the outbox collection
/// no longer lists the deleted content. The cleanup is gated on a prior copy having been stored here — a
/// tombstone for an object this instance never held stores the tombstone (so a <c>GET</c> serves it
/// rather than a <c>404</c>) but has no local artifacts to clean.
/// </para>
/// </remarks>
internal static class TombstoneInbound
{
    /// <summary>
    /// Stores the inbound <see cref="Tombstone"/> under its IRI (replacing any prior content) and cleans
    /// up the local artifacts a prior copy left behind (reply edge, the author's outbox
    /// <see cref="Create"/>, the object → <see cref="Create"/> index).
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IObjectStore"/>,
    /// <see cref="IReplyStore"/>, <see cref="ICreateIndex"/>, and <see cref="IActivityStore"/>).</param>
    /// <param name="tombstone">The inbound tombstone (its <c>Id</c> is the object IRI to tombstone).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the tombstone has been stored and the local artifacts cleaned.</returns>
    public static async Task ApplyAsync(IPersistenceProvider persistence, Tombstone tombstone, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(tombstone);

        var objectIri = new Iri(tombstone.Id!);

        // Read the prior stored object <em>before</em> replacing it: the cleanup (reply edge, outbox
        // Create, object → Create index) depends on what was there, and a tombstone-for-an-unknown-object
        // (no prior copy) stores the tombstone but has no local artifacts to clean.
        var hadPriorCopy = await persistence
            .Objects
            .TryGetObjectAsync(objectIri, out var prior, ct)
            .ConfigureAwait(false)
            && prior is not null
            && prior is not Tombstone;

        // F-10: store the tombstone under the object IRI (replacing any prior content) so a GET serves the
        // tombstone, not stale content or a 404.
        await persistence
            .Objects
            .PutObjectAsync(tombstone, ct)
            .ConfigureAwait(false);

        if (!hadPriorCopy)
        {
            return;
        }

        // F-12: when the deleted object is a reply (it has an inReplyTo), remove the local parent → child
        // reply edge so the parent's replies collection no longer lists it.
        var parentIri = prior!.GetParentIri();
        if (parentIri is { } parent)
        {
            await persistence
                .Replies
                .RemoveReplyAsync(parent, objectIri, ct)
                .ConfigureAwait(false);
        }

        // Remove the deleted object's originating Create from the author's outbox so the outbox collection
        // no longer lists the deleted content (the inverse of the AddToOutboxAsync the Create handler
        // recorded). The author is the prior object's attributedTo (the original content's author owns the
        // outbox entry). Decision 055: the Create IRI is resolved by lookup in the object → Create index,
        // not derived from the object IRI.
        var authorIri = (prior as ActivityObject)?.AttributedTo?.FirstOrDefault()?.ResolveObjectIri();
        if (authorIri is { } author
            && await persistence
                .Creates
                .TryGetCreateIriAsync(objectIri, ct)
                .ConfigureAwait(false) is { } createIri)
        {
            await persistence
                .Activities
                .RemoveFromOutboxAsync(author, createIri, ct)
                .ConfigureAwait(false);
            await persistence
                .Creates
                .RemoveAsync(objectIri, ct)
                .ConfigureAwait(false);
        }
    }
}
