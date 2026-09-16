namespace Iris.Server.Data.Entities;

/// <summary>
/// An ActivityStreams activity (a <c>Create</c>, <c>Follow</c>, <c>Like</c>, <c>Announce</c>, …) row.
/// The full activity document is the source of truth, carried in <see cref="Document"/> (a
/// <c>jsonb</c> column); the relational columns index it for outbox/inbox queries and filtering.
/// </summary>
/// <remarks>
/// An activity is stored once (keyed by its own IRI, <see cref="Id"/>). Its presence in an actor's
/// outbox or inbox is recorded separately in the <c>OutboxItem</c> / <c>InboxItem</c> tables, which
/// reference the activity's IRI and carry the per-collection ordering.
/// </remarks>
public sealed class ActivityEntity
{
    /// <summary>
    /// The activity's IRI (primary key; the value of the document's <c>id</c>).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The activity's ActivityStreams <c>type</c> (e.g. <c>Create</c>, <c>Follow</c>), for filtering.
    /// </summary>
    public string? ActivityType { get; set; }

    /// <summary>
    /// The IRI of the object this activity references (the activity's <c>object</c>), extracted for
    /// per-object lookups (e.g. "find all Likes of object X"). Null when the activity has no resolvable
    /// object reference (e.g. a bare-link <c>Undo</c> whose target is not stored).
    /// </summary>
    public string? ObjectIri { get; set; }

    /// <summary>
    /// When the row was created (a stable sort key).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The full activity document, as canonical ActivityStreams JSON (a <c>jsonb</c> column).
    /// </summary>
    public string Document { get; set; } = string.Empty;
}
