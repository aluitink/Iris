using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Shared helpers for the <c>Announce</c> (boost/repost) lifecycle: the <see cref="Announce"/> activity
/// a local actor uses to propagate a boost to a follower. (Resolving an object IRI from an
/// <see cref="IObjectOrLink"/> lives in <see cref="IriExtensions.ResolveObjectIri(IObjectOrLink?)"/>.)
/// </summary>
/// <remarks>
/// An <c>Announce</c> is a local actor re-sharing an object (a boost/repost). When the local actor's
/// followers live on other instances, the local instance must propagate the <c>Announce</c> to each
/// local follower's inbox so the follower's client can display the boosted content.
/// </remarks>
/// <para>
/// <strong>Id model (decision 055, "mint once at record-time, reuse for all deliveries").</strong> The
/// boost's id is minted <em>once</em> — by the outbox write path (an unguessable ULID) or, for an inbound
/// boost, taken verbatim from the remote originator — and every propagated copy to each follower <em>
/// reuses that same id</em> (only the per-follower <c>to</c>/<c>cc</c> addressing differs). Reusing one
/// id keeps the activity idempotent: the same boost delivered to two followers — or redelivered on retry
/// — is the same activity, so a follower that stores by IRI does not accumulate duplicates. The
/// announcer's own outbox keeps the original announce (the same id), so the boost is discoverable from
/// the announcer's outbox and from each follower's inbox.
/// </para>
/// <para>
/// The propagated <c>Announce</c> is addressed directly to each local follower (<c>to</c> = the
/// follower's actor IRI, <c>cc</c> = the announcer's actor IRI) and carries the original announce's id.
/// </para>
public static class AnnounceIris
{
    /// <summary>
    /// Builds the <see cref="Announce"/> a local actor uses to propagate a boost to a follower:
    /// the announcer re-shares the announced object, addressed to <paramref name="followerIri"/>
    /// (<c>to</c>) and cc'd to the announcer (<c>cc</c>), carrying <paramref name="announceIri"/> — the
    /// boost's single id (minted once at record-time and reused for every propagated copy, so a follower
    /// that stores by IRI dedupes the boost rather than seeing one copy per delivery).
    /// </summary>
    /// <param name="announceIri">The boost's id — the id of the original <see cref="Announce"/> (minted
    /// once by the outbox write path, or the inbound announce's originator id). Every propagated copy
    /// reuses this id.</param>
    /// <param name="announcerIri">The IRI of the local actor performing the announce (the
    /// <c>actor</c>/<c>attributedTo</c> of the activity).</param>
    /// <param name="objectIri">The IRI of the object being announced (the <c>object</c> of the
    /// activity).</param>
    /// <param name="followerIri">The IRI of the local follower the activity is addressed to
    /// (the <c>to</c> audience; the recipient of this propagated copy).</param>
    /// <returns>The constructed <see cref="Announce"/>.</returns>
    public static Announce BuildAnnounce(Iri announceIri, Iri announcerIri, Iri objectIri, Iri followerIri) => new()
    {
        Id = announceIri.Value,
        Actor = [new Link { Href = new Uri(announcerIri.Value) }],
        AttributedTo = [new Link { Href = new Uri(announcerIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
        To = [new Link { Href = new Uri(followerIri.Value) }],
        Cc = [new Link { Href = new Uri(announcerIri.Value) }],
    };
}
