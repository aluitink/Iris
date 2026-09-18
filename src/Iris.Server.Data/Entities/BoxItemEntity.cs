namespace Iris.Server.Data.Entities;

/// <summary>
/// A single item in an actor's outbox or inbox collection. References an <see cref="ActivityEntity"/>
/// (the item is always an activity / object with a resolvable IRI) and carries the per-collection
/// ordering so collections can be served newest-first.
/// </summary>
/// <remarks>
/// The outbox and inbox share this table (they are the same shape: a per-actor ordered list of items).
/// A unique index on (<see cref="Direction"/>, <see cref="ActorId"/>, <see cref="ItemIri"/>) makes the
/// idempotent add (at-least-once delivery / restart replay) a no-op for a re-recorded item.
/// </remarks>
public sealed class BoxItemEntity
{
    /// <summary>
    /// Which collection this item belongs to: <c>0</c> = outbox, <c>1</c> = inbox.
    /// </summary>
    public int Direction { get; set; }

    /// <summary>
    /// The IRI of the actor whose outbox/inbox this is.
    /// </summary>
    public string ActorId { get; set; } = string.Empty;

    /// <summary>
    /// The IRI of the item (the activity's / object's <c>id</c>, or the link's <c>href</c>).
    /// </summary>
    public string ItemIri { get; set; } = string.Empty;

    /// <summary>
    /// A stable per-collection sequence number; lower is newer (items are served ascending).
    /// </summary>
    public long Position { get; set; }
}
