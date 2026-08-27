using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Shared helpers for the <c>Announce</c> (boost/repost) lifecycle: the deterministic IRI for an
/// <see cref="Announce"/> and the <see cref="Announce"/> activity itself, plus resolving an object
/// IRI from an <see cref="IObjectOrLink"/>.
/// </summary>
/// <remarks>
/// An <c>Announce</c> is a local actor re-sharing an object (a boost/repost). When the local actor's
/// followers live on other instances, the local instance must propagate the <c>Announce</c> to each
/// local follower's inbox so the follower's client can display the boosted content. The
/// <see cref="AnnounceActivityHandler"/> builds the propagated activity with these builders.
/// </remarks>
/// <para>
/// The propagated <c>Announce</c> is addressed directly to each local follower (<c>to</c> = the
/// follower's actor IRI, <c>cc</c> = the announcer's actor IRI) and <em>reuses the original
/// announce's IRI</em> (deterministic: <c>{announcer}/announces/{objectIri}</c>). Reusing the IRI
/// keeps the activity idempotent: the same boost delivered to two followers — or redelivered on
/// retry — is the same activity, so a follower that stores by IRI does not accumulate duplicates.
/// The announcer's own outbox keeps the original announce (the same IRI), so the boost is
/// discoverable from the announcer's outbox and from each follower's inbox.
/// </para>
public static class AnnounceIris
{
    /// <summary>
    /// Builds the deterministic IRI of an <see cref="Announce"/>: <c>{announcerIri}/announces/{objectIri}</c>.
    /// </summary>
    /// <param name="announcerIri">The IRI of the local actor performing the announce (the
    /// <c>actor</c>/<c>attributedTo</c> of the activity).</param>
    /// <param name="objectIri">The IRI of the object being announced (the <c>object</c> of the
    /// activity).</param>
    /// <returns>The Announce's IRI.</returns>
    public static Iri AnnounceIri(Iri announcerIri, Iri objectIri)
        => new($"{announcerIri}/announces/{objectIri}");

    /// <summary>
    /// Builds the <see cref="Announce"/> a local actor uses to propagate a boost to a follower:
    /// the announcer re-shares the announced object, addressed to <paramref name="followerIri"/>
    /// (<c>to</c>) and cc'd to the announcer (<c>cc</c>), carrying the deterministic
    /// <see cref="AnnounceIri(Iri, Iri)"/>.
    /// </summary>
    /// <param name="announcerIri">The IRI of the local actor performing the announce.</param>
    /// <param name="objectIri">The IRI of the object being announced.</param>
    /// <param name="followerIri">The IRI of the local follower the activity is addressed to
    /// (the <c>to</c> audience; the recipient of this propagated copy).</param>
    /// <returns>The constructed <see cref="Announce"/>.</returns>
    public static Announce BuildAnnounce(Iri announcerIri, Iri objectIri, Iri followerIri) => new()
    {
        Id = AnnounceIri(announcerIri, objectIri).Value,
        Actor = [new Link { Href = new Uri(announcerIri.Value) }],
        AttributedTo = [new Link { Href = new Uri(announcerIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
        To = [new Link { Href = new Uri(followerIri.Value) }],
        Cc = [new Link { Href = new Uri(announcerIri.Value) }],
    };

    /// <summary>
    /// Resolves the IRI of an <see cref="IObjectOrLink"/>: a <see cref="Link"/> contributes its
    /// <c>Href</c>; an embedded object contributes its <c>Id</c>. Returns null when neither is set.
    /// </summary>
    /// <param name="objOrLink">The object or link to resolve.</param>
    /// <returns>The resolved IRI, or null when the object/link carries no IRI.</returns>
    public static Iri? ResolveObjectIri(IObjectOrLink? objOrLink)
    {
        if (objOrLink is ILink { Href: { } href })
        {
            return new Iri(href);
        }

        if (objOrLink is IObject { Id: { Length: > 0 } id })
        {
            return new Iri(id);
        }

        return null;
    }
}
