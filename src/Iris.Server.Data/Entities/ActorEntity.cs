namespace Iris.Server.Data.Entities;

/// <summary>
/// An actor (a <c>Person</c> or a <c>Group</c>) row. The relational columns index the actor's identity;
/// the full ActivityStreams actor document (including the owner-only <c>privateKey</c> extension) is
/// carried in <see cref="Document"/> (a <c>jsonb</c> column) and is the source of truth for content.
/// </summary>
/// <remarks>
/// A <c>Group</c> is an actor, so communities live here too (their membership/follow edges live in the
/// community edge tables). The document is stored exactly as the server would serve it (via
/// <see cref="Iris.Core.ActivityJson"/>), so a store read deserializes it straight back to an
/// <c>Actor</c> with no lossy relational reconstruction.
/// </remarks>
public sealed class ActorEntity
{
    /// <summary>
    /// The actor's IRI (primary key; the value of the document's <c>id</c>).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The actor's handle (the local name, e.g. <c>alice</c>). Indexed for WebFinger / directory lookups.
    /// Null when the actor was not provisioned with a handle (e.g. a remote stand-in).
    /// </summary>
    public string? Handle { get; set; }

    /// <summary>
    /// The actor's ActivityStreams <c>type</c> (e.g. <c>Person</c>, <c>Group</c>).
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// When the row was created (a stable sort key).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The full actor document, as canonical ActivityStreams JSON (a <c>jsonb</c> column).
    /// </summary>
    public string Document { get; set; } = string.Empty;
}
