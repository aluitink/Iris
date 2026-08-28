using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Shared helpers for the follow lifecycle: the deterministic IRIs for the <c>Accept</c>/<c>Reject</c>
/// response to a follow, and the <c>Accept</c>/<c>Reject</c> activities themselves. (Resolving an actor
/// or object IRI from an <see cref="IObjectOrLink"/> lives in
/// <see cref="IriExtensions.ResolveObjectIri(IObjectOrLink?)"/>.)
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
    /// Builds the deterministic IRI of the <see cref="Undo"/> of a follow (an un-follow).
    /// </summary>
    /// <param name="localActorIri">The IRI of the local actor undoing the follow (the Undo's actor — the
    /// follower).</param>
    /// <param name="follow">The original follow activity being undone.</param>
    /// <returns>The Undo's IRI (<c>{localActorIri}/undoes/{followId}</c>).</returns>
    public static Iri UndoIri(Iri localActorIri, Follow follow)
        => new($"{localActorIri}/undoes/{follow.Id}");

    /// <summary>
    /// Builds the <see cref="Undo"/> of a follow (an un-follow): the local actor (the follower) undoes the
    /// original follow activity (object = the original follow, by IRI).
    /// </summary>
    /// <param name="localActorIri">The IRI of the local actor undoing the follow (the Undo's actor — the
    /// follower).</param>
    /// <param name="follow">The original follow activity being undone.</param>
    /// <returns>The constructed <see cref="Undo"/>.</returns>
    public static Undo BuildUndo(Iri localActorIri, Follow follow) => new()
    {
        Id = UndoIri(localActorIri, follow).Value,
        Actor = [new Link { Href = new Uri(localActorIri.Value) }],
        Object = [new Link { Href = new Uri(follow.Id!) }],
    };
}
