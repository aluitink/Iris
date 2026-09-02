using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Identity;

/// <summary>
/// The server-side authority for minting the <c>id</c> of an object or activity this instance creates
/// (decision 055). The authoring client sends the activity <em>shape</em> (type, actor, object
/// content/references) but <strong>not</strong> the id; the server mints a collision-resistant,
/// unguessable id in a fixed per-type namespace and stores the object under it.
/// </summary>
/// <remarks>
/// The id shape is <c>{actorBase}/{namespace}/{ulid}</c>, where:
/// <list type="bullet">
/// <item><c>actorBase</c> is the IRI of the actor that authors the object (the object is always
/// attributable to a single local actor).</item>
/// <item><c>namespace</c> is a fixed, type-specific path segment (<c>notes</c>, <c>follows</c>, …) so the
/// id is discoverable under the actor's own tree and its type is self-evident from the URL.</item>
/// <item><c>ulid</c> is a <see cref="Ulid"/>: 80 bits of entropy (unguessable, so no enumeration or
/// overwrite) and a 48-bit millisecond timestamp (lexicographically sortable by creation time).</item>
/// </list>
/// </remarks>
/// <remarks>
/// Monotonicity: the minter uses a <see cref="MonotonicUlid"/> so two ids minted in the same tick never
/// collide, even under high throughput.
/// </remarks>
public sealed class IdMinter
{
    private readonly MonotonicUlid _ulids = new();
    private readonly Func<DateTimeOffset>? _now;

    /// <summary>
    /// Initializes a new <see cref="IdMinter"/> that reads the current time from
    /// <see cref="DateTimeOffset.UtcNow"/> for each ULID.
    /// </summary>
    public IdMinter()
        : this(null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="IdMinter"/> with an injected clock (for deterministic tests).
    /// </summary>
    /// <param name="now">A function returning the current instant; <c>null</c> uses
    /// <see cref="DateTimeOffset.UtcNow"/>.</param>
    public IdMinter(Func<DateTimeOffset>? now)
    {
        _now = now;
    }

    /// <summary>
    /// Mints the id of an <see cref="Activity"/> authored by <paramref name="actorIri"/>, choosing the
    /// namespace from the activity's concrete type.
    /// </summary>
    /// <param name="actorIri">The IRI of the authoring actor (the id's base).</param>
    /// <param name="activity">The activity whose type selects the namespace. May be the same instance the
    /// caller will store/deliver (the minter does not mutate it).</param>
    /// <returns>The minted absolute IRI (<c>{actorIri}/{namespace}/{ulid}</c>).</returns>
    public Iri Mint(Iri actorIri, Activity activity)
        => Mint(actorIri, NamespaceFor(activity));

    /// <summary>
    /// Mints the id of an embedded <see cref="IObject"/> (a <see cref="Note"/>, <see cref="Group"/>, …)
    /// authored by <paramref name="actorIri"/>, choosing the namespace from the object's concrete type.
    /// </summary>
    /// <param name="actorIri">The IRI of the authoring actor (the id's base).</param>
    /// <param name="obj">The object whose type selects the namespace.</param>
    /// <returns>The minted absolute IRI.</returns>
    public Iri Mint(Iri actorIri, IObject obj)
        => Mint(actorIri, NamespaceFor(obj));

    /// <summary>
    /// Mints an id in an explicit namespace under <paramref name="actorIri"/>.
    /// </summary>
    /// <param name="actorIri">The IRI of the authoring actor (the id's base).</param>
    /// <param name="namespaceSegment">The fixed, type-specific path segment (e.g. <c>notes</c>).</param>
    /// <returns>The minted absolute IRI.</returns>
    public Iri Mint(Iri actorIri, string namespaceSegment)
        => new($"{actorIri.Value.TrimEnd('/')}/{namespaceSegment}/{_ulids.Next(_now?.Invoke())}");

    /// <summary>
    /// Returns the fixed, type-specific namespace segment for an <see cref="Activity"/>.
    /// </summary>
    /// <param name="activity">The activity to classify.</param>
    /// <returns>The namespace segment (e.g. <c>follows</c>), or <c>activities</c> for an unrecognized type.</returns>
    public static string NamespaceFor(Activity activity) => activity switch
    {
        Follow => "follows",
        Accept => "accepts",
        Reject => "rejects",
        Like => "likes",
        Announce => "announces",
        Delete => "deletes",
        Block => "blocks",
        Flag => "flags",
        Undo => "undos",
        Create => "creates",
        Add => "adds",
        Remove => "removes",
        _ => "activities",
    };

    /// <summary>
    /// Returns the fixed, type-specific namespace segment for an <see cref="IObject"/>.
    /// </summary>
    /// <param name="obj">The object to classify.</param>
    /// <returns>The namespace segment (e.g. <c>notes</c>), or <c>objects</c> for an unrecognized type.</returns>
    public static string NamespaceFor(IObject obj) => obj switch
    {
        Note => "notes",
        Group => "groups",
        Article => "articles",
        Image => "images",
        Video => "videos",
        Audio => "audios",
        Document => "documents",
        _ => "objects",
    };
}
