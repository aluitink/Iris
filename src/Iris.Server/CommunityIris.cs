using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Shared helpers for the community-following lifecycle: the deterministic IRIs for the
/// <c>Accept</c> response to a follow of a community, the <c>Accept</c> activity itself, and resolving
/// an object IRI from an <see cref="IObjectOrLink"/>.
/// </summary>
/// <remarks>
/// A community follows another actor (community or person) the same way a person does, so the
/// follow/accept wire shape is identical to a person's — only the <em>actor</em> is a <see cref="Group"/>
/// and the <em>target</em> may be a remote community. The <see cref="FollowActivityHandler"/> builds
/// these on the followed side (when the recipient is a community); the deterministic
/// <see cref="AcceptIri(Iri, Follow)"/> IRI lets the community's instance recognize its own follow's
/// acceptance. The <see cref="ResolveObjectIri(IObjectOrLink)"/> helper is shared with the content
/// handlers that propagate followed content to the community's members.
/// </remarks>
public static class CommunityIris
{
    /// <summary>
    /// Builds the deterministic IRI of the <see cref="Accept"/> response to a follow of a community.
    /// </summary>
    /// <param name="localActorIri">The IRI of the local community being followed (the Accept's actor).</param>
    /// <param name="follow">The original follow activity.</param>
    /// <returns>The Accept's IRI (<c>{localActorIri}/accepts/{followId}</c>).</returns>
    public static Iri AcceptIri(Iri localActorIri, Follow follow)
        => new($"{localActorIri}/accepts/{follow.Id}");

    /// <summary>
    /// Builds the <see cref="Accept"/> response to a follow of a community: the community accepts the
    /// original follow activity (object = the original follow, by IRI).
    /// </summary>
    /// <param name="localActorIri">The IRI of the local community being followed (the Accept's actor).</param>
    /// <param name="follow">The original follow activity.</param>
    /// <returns>The constructed <see cref="Accept"/>.</returns>
    public static Accept BuildAccept(Iri localActorIri, Follow follow) => new()
    {
        Id = AcceptIri(localActorIri, follow).Value,
        Actor = [new Link { Href = new Uri(localActorIri.Value) }],
        Object = [new Link { Href = new Uri(follow.Id!) }],
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
