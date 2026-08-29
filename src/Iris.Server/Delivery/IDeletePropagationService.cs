using Iris.Core;

namespace Iris.Server.Delivery;

/// <summary>
/// Propagates an object <see cref="KristofferStrube.ActivityStreams.Update"/> or
/// <see cref="KristofferStrube.ActivityStreams.Delete"/> to the remote actors that need to see it
/// (the federated write half of F-02/F-03).
/// </summary>
/// <remarks>
/// When a local actor edits or deletes an object this instance stores, the change is not enough to
/// land in the local <see cref="IObjectStore"/> (the <see cref="UpdateActivityHandler"/> /
/// <see cref="DeleteActivityHandler"/> do that): the actors on <em>other</em> instances that saw the
/// object must be told, or their copies go stale (a remote instance keeps serving the pre-edit
/// content, or the pre-delete content in place of a <see cref="KristofferStrube.ActivityStreams.Tombstone"/>).
/// This service is the single owner of that fan-out: it computes the propagation target set from the
/// object's <see cref="KristofferStrube.ActivityStreams.Object.AttributedTo"/>, the local
/// <see cref="IFollowStore"/>, and the local <see cref="IReplyStore"/>, and schedules the activity for
/// delivery to each remote target via <see cref="IDeliveryService"/> (signed as the local author).
/// </remarks>
/// <para>
/// <strong>Target computation.</strong> For an object owned by local actor <c>A</c>, the targets are:
/// <list type="number">
/// <item><c>A</c>'s <em>remote</em> followers (the actors who saw the object via
/// <see cref="CreateActivityHandler"/>'s outbound <c>Create</c> federation, Slice 11.7) — they need the
/// refreshed / tombstoned content to keep their copy consistent.</item>
/// <item>The object's <em>attributedTo</em> (the author) when the author is remote — a post published
/// to a remote author's outbox (a <c>Create</c> addressed to them) is theirs, so they need to know it
/// was edited or deleted (they serve it by IRI from their object store).</item>
/// <item>The object's <em>parents</em> (the objects it replies to) when a parent is owned by a remote
/// actor — the parent's instance holds the parent object's replies collection (F-12); when a reply
/// is deleted, that parent's replies edge is removed there. (When the parent is local the edge is
/// removed locally by the handler; a remote parent cannot be told to drop an edge it does not index
/// per-object, so only the parent's <em>owner</em> is targeted and the edge removal on this instance
/// is local-only.)</item>
/// </list>
/// Targets that are local actors are skipped: a local actor's copy is on this instance and is
/// refreshed / tombstoned locally by the handler (no cross-instance delivery).
/// </para>
/// <para>
/// <strong>Determinism.</strong> The propagation activity's <c>Id</c> is derived from the object IRI
/// (<c>{objectIri}/updates/{guid-N}</c> / <c>{objectIri}/deletes/{guid-N}</c>) so a re-delivered
/// propagation is deduplicated by the receiving instance's inbox pipeline (C-07).
/// </para>
public interface IDeletePropagationService
{
    /// <summary>
    /// Propagates an <see cref="KristofferStrube.ActivityStreams.Update"/> to the remote actors that
    /// need the refreshed object.
    /// </summary>
    /// <param name="authorIri">The IRI of the local actor who owns the object (the activity's
    /// <c>actor</c>).</param>
    /// <param name="objectIri">The IRI of the object being updated.</param>
    /// <param name="activity">The <see cref="KristofferStrube.ActivityStreams.Update"/> to deliver
    /// (its embedded object carries the refreshed content).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when every remote target has been scheduled for delivery.</returns>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public Task PropagateUpdateAsync(Iri authorIri, Iri objectIri, KristofferStrube.ActivityStreams.Update activity, CancellationToken ct = default);

    /// <summary>
    /// Propagates a <see cref="KristofferStrube.ActivityStreams.Delete"/> to the remote actors that
    /// need the tombstone.
    /// </summary>
    /// <param name="authorIri">The IRI of the local actor who owns the object (the activity's
    /// <c>actor</c>).</param>
    /// <param name="objectIri">The IRI of the object being deleted.</param>
    /// <param name="activity">The <see cref="KristofferStrube.ActivityStreams.Delete"/> to deliver
    /// (it references the deleted object by IRI).</param>
    /// <param name="parentObject">The parent object (the one the deleted object replies to), read from
    /// the stored object's <c>inReplyTo</c> <em>before</em> it is tombstoned (a <c>Tombstone</c> carries
    /// no <c>inReplyTo</c>, so it must be captured up front). When the parent is remote-owned (its
    /// <c>attributedTo</c> is not a local actor), the parent's owner is a propagation target — the
    /// parent's instance holds the parent's replies collection (F-12) and must drop the deleted
    /// reply's edge there. Null when the deleted object is not a reply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when every remote target has been scheduled for delivery.</returns>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public Task PropagateDeleteAsync(
        Iri authorIri,
        Iri objectIri,
        KristofferStrube.ActivityStreams.Delete activity,
        KristofferStrube.ActivityStreams.IObject? parentObject = null,
        CancellationToken ct = default);
}
