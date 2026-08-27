using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Shared helpers for the follow lifecycle: the deterministic IRIs for the <c>Accept</c>/<c>Reject</c>
/// response to a follow, the <c>Accept</c>/<c>Reject</c> activities themselves, and resolving an actor
/// IRI from an <see cref="IObjectOrLink"/>.
/// </summary>
/// <remarks>
/// The <see cref="FollowActivityHandler"/> builds these on the followed side; the
/// <see cref="AcceptActivityHandler"/> and <see cref="RejectActivityHandler"/> use the same builders so
/// that an <c>Accept</c> delivered back to the follower references the very <c>Follow</c> that prompted
/// it (matching the deterministic IRI), and the follower can recognize its own follow.
/// </remarks>
public static class FollowIris
{
    /// <summary>
    /// Builds the deterministic IRI of the <see cref="Accept"/> response to a follow.
    /// </summary>
    /// <param name="localActorIri">The IRI of the local actor being followed (the Accept's actor).</param>
    /// <param name="follow">The original follow activity.</param>
    /// <returns>The Accept's IRI (<c>{localActorIri}/accepts/{followId}</c>).</returns>
    public static Iri AcceptIri(Iri localActorIri, Follow follow)
        => new($"{localActorIri}/accepts/{follow.Id}");

    /// <summary>
    /// Builds the deterministic IRI of the <see cref="Reject"/> response to a follow.
    /// </summary>
    /// <param name="localActorIri">The IRI of the local actor being followed (the Reject's actor).</param>
    /// <param name="follow">The original follow activity.</param>
    /// <returns>The Reject's IRI (<c>{localActorIri}/rejects/{followId}</c>).</returns>
    public static Iri RejectIri(Iri localActorIri, Follow follow)
        => new($"{localActorIri}/rejects/{follow.Id}");

    /// <summary>
    /// Builds the <see cref="Accept"/> response to a follow: the local actor accepts the original follow
    /// activity (object = the original follow, by IRI).
    /// </summary>
    /// <param name="localActorIri">The IRI of the local actor being followed (the Accept's actor).</param>
    /// <param name="follow">The original follow activity.</param>
    /// <returns>The constructed <see cref="Accept"/>.</returns>
    public static Accept BuildAccept(Iri localActorIri, Follow follow) => new()
    {
        Id = AcceptIri(localActorIri, follow).Value,
        Actor = [new Link { Href = new Uri(localActorIri.Value) }],
        Object = [new Link { Href = new Uri(follow.Id!) }],
    };

    /// <summary>
    /// Builds the <see cref="Reject"/> response to a follow: the local actor rejects the original follow
    /// activity (object = the original follow, by IRI).
    /// </summary>
    /// <param name="localActorIri">The IRI of the local actor being followed (the Reject's actor).</param>
    /// <param name="follow">The original follow activity.</param>
    /// <returns>The constructed <see cref="Reject"/>.</returns>
    public static Reject BuildReject(Iri localActorIri, Follow follow) => new()
    {
        Id = RejectIri(localActorIri, follow).Value,
        Actor = [new Link { Href = new Uri(localActorIri.Value) }],
        Object = [new Link { Href = new Uri(follow.Id!) }],
    };

    /// <summary>
    /// Resolves the IRI of an <see cref="IObjectOrLink"/>: a <see cref="Link"/> contributes its
    /// <c>Href</c>; an embedded object contributes its <c>Id</c>. Returns null when neither is set.
    /// </summary>
    /// <param name="objOrLink">The object or link to resolve.</param>
    /// <returns>The resolved IRI, or null when the object/link carries no IRI.</returns>
    public static Iri? ResolveActorIri(IObjectOrLink? objOrLink)
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
