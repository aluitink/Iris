using Iris.Core;
using Iris.Server.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Shared helpers for the follow lifecycle: the <c>Accept</c>/<c>Reject</c> response to a follow, and the
/// <c>Undo</c> of a follow (an un-follow). (Resolving an actor or object IRI from an
/// <see cref="IObjectOrLink"/> lives in <see cref="IriExtensions.ResolveObjectIri(IObjectOrLink?)"/>.)
/// </summary>
/// <remarks>
/// The <see cref="FollowActivityHandler"/> builds these on the followed side; the
/// <see cref="AcceptActivityHandler"/> and <see cref="RejectActivityHandler"/> use the same builders so
/// that an <c>Accept</c> delivered back to the follower references the very <c>Follow</c> that prompted
/// it, and the follower can recognize its own follow.
/// </remarks>
/// <remarks>
/// <strong>Id model (decision 055).</strong> The response activity's <em>own</em> id is minted by the
/// server (an unguessable ULID in the <c>accepts</c>/<c>rejects</c>/<c>undos</c> namespace) — the followed
/// instance is the authority for the id of the activity it authors in response. The response's
/// <c>object</c>, however, references the <em>inbound</em> follow by its <c>original</c> id (the
/// follower's, kept verbatim — this instance is a replica of the follow, not its originator), so the
/// follower resolves exactly the follow that prompted the response.
/// </remarks>
public static class FollowIris
{
    /// <summary>
    /// Builds the <see cref="Accept"/> response to a follow: the local actor accepts the original follow
    /// activity (object = the original follow, by its original IRI). The Accept's <em>own</em> id is
    /// minted by the server (an unguessable ULID under <c>{localActorIri}/accepts/{ulid}</c>).
    /// </summary>
    /// <param name="idMinter">The server-side id authority (mints the Accept's own id).</param>
    /// <param name="localActorIri">The IRI of the local actor being followed (the Accept's actor).</param>
    /// <param name="follow">The original follow activity (referenced by its original id).</param>
    /// <returns>The constructed <see cref="Accept"/>.</returns>
    public static Accept BuildAccept(IdMinter idMinter, Iri localActorIri, Follow follow) => new()
    {
        Id = idMinter.Mint(localActorIri, "accepts").Value,
        Actor = [new Link { Href = new Uri(localActorIri.Value) }],
        Object = [new Link { Href = new Uri(follow.Id!) }],
    };

    /// <summary>
    /// Builds the <see cref="Reject"/> response to a follow: the local actor rejects the original follow
    /// activity (object = the original follow, by its original IRI). The Reject's <em>own</em> id is
    /// minted by the server (an unguessable ULID under <c>{localActorIri}/rejects/{ulid}</c>).
    /// </summary>
    /// <param name="idMinter">The server-side id authority (mints the Reject's own id).</param>
    /// <param name="localActorIri">The IRI of the local actor being followed (the Reject's actor).</param>
    /// <param name="follow">The original follow activity (referenced by its original id).</param>
    /// <returns>The constructed <see cref="Reject"/>.</returns>
    public static Reject BuildReject(IdMinter idMinter, Iri localActorIri, Follow follow) => new()
    {
        Id = idMinter.Mint(localActorIri, "rejects").Value,
        Actor = [new Link { Href = new Uri(localActorIri.Value) }],
        Object = [new Link { Href = new Uri(follow.Id!) }],
    };

    /// <summary>
    /// Builds the <see cref="Undo"/> of a follow (an un-follow): the local actor (the follower) undoes the
    /// original follow activity (object = the original follow, by its original IRI). The Undo's <em>own</em>
    /// id is minted by the server (an unguessable ULID under <c>{localActorIri}/undos/{ulid}</c>).
    /// </summary>
    /// <param name="idMinter">The server-side id authority (mints the Undo's own id).</param>
    /// <param name="localActorIri">The IRI of the local actor undoing the follow (the Undo's actor — the
    /// follower).</param>
    /// <param name="follow">The original follow activity being undone (referenced by its original id).</param>
    /// <returns>The constructed <see cref="Undo"/>.</returns>
    public static Undo BuildUndo(IdMinter idMinter, Iri localActorIri, Follow follow) => new()
    {
        Id = idMinter.Mint(localActorIri, "undos").Value,
        Actor = [new Link { Href = new Uri(localActorIri.Value) }],
        Object = [new Link { Href = new Uri(follow.Id!) }],
    };
}
