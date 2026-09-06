namespace Iris.Server.Data.Entities;

/// <summary>
/// A content object (a <c>Note</c>, <c>Article</c>, media object, or a <c>Tombstone</c> for a deleted
/// object) row. The full ActivityStreams object document is the source of truth, carried in
/// <see cref="Document"/> (a <c>jsonb</c> column); the relational columns index it for lookup.
/// </summary>
public sealed class ObjectEntity
{
    /// <summary>
    /// The object's IRI (primary key; the value of the document's <c>id</c>).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The IRI of the actor the object is attributed to (nullable). Indexed for content queries.
    /// </summary>
    public string? AttributedTo { get; set; }

    /// <summary>
    /// The object's ActivityStreams <c>type</c> (e.g. <c>Note</c>, <c>Tombstone</c>).
    /// </summary>
    public string? ObjectType { get; set; }

    /// <summary>
    /// Whether the object is a <c>Tombstone</c> (a deleted marker, F-10). Callers that search content
    /// skip tombstoned objects.
    /// </summary>
    public bool IsTombstoned { get; set; }

    /// <summary>
    /// When the row was created (a stable sort key).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The full object document, as canonical ActivityStreams JSON (a <c>jsonb</c> column).
    /// </summary>
    public string Document { get; set; } = string.Empty;
}
