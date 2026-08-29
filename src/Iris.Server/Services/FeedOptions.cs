namespace Iris.Server.Services;

/// <summary>
/// Options for the <see cref="IFollowFeedService"/> (F-14, the actor's followed feed / home timeline).
/// </summary>
/// <remarks>
/// Bounding the fetch keeps the endpoint responsive: a followed actor's outbox is walked at most
/// <see cref="PagesPerActor"/> pages, and the merged feed returns at most <see cref="MaxItems"/> items.
/// Both default to values that are large enough for a typical timeline while small enough to keep a
/// single feed request cheap. A host may tighten them for a high-fan-out actor.
/// </remarks>
public sealed class FeedOptions
{
    /// <summary>
    /// The number of outbox pages to walk per followed actor when assembling the feed. The first page of
    /// each followed actor's outbox is the newest content, so a small value (the default) already covers
    /// the recent timeline.
    /// </summary>
    public int PagesPerActor { get; init; } = 1;

    /// <summary>
    /// The maximum number of items the merged feed returns. When the union of the walked outboxes
    /// exceeds this, the extra (oldest) items are dropped.
    /// </summary>
    public int MaxItems { get; init; } = 200;
}
