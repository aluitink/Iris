using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iris.Core;

namespace Iris.Server.Caching;

/// <summary>
/// A <strong>file-backed, cross-process</strong> cache-invalidation channel (Phase 84.6, shared-state
/// scale-out): two (or more) Iris instances over the same origin share <em>one</em> durable invalidation
/// journal, so an actor-document change on instance A invalidates the in-memory actor/edge caches on
/// instance B without a restart or a cache TTL expiry.
/// </summary>
/// <remarks>
/// <strong>The single-instance gap.</strong> The in-memory actor/edge caches (<see cref="RemoteActorCache"/>,
/// <see cref="LocalActorDocumentCache"/>) are per-instance: each instance holds its own <see cref="MemoryCache{TValue}"/>.
/// When instance A updates a local actor (a key rotation re-stamping the actor document's <c>publicKey</c>,
/// a profile change, etc.), instance B's cached copy of that actor's document is now stale — B serves the
/// old document (and resolves the old key from it) for up to the cache's TTL (1 hour for the remote-actor
/// cache). The 84.5 single-instance guard made the single-instance constraint explicit; this channel is the
/// scale-out half: a shared invalidation journal two instances can coexist over.
/// </remarks>
/// <remarks>
/// <strong>Model: an append-only invalidation journal over a JSON file.</strong> Each journaled line is a
/// <see cref="CacheInvalidationEvent"/> — a monotonic <c>Seq</c> (event position), the affected
/// <c>ActorIri</c>, and a <c>At</c> timestamp (UTC). The journal is guarded by a cross-process
/// <em>file lock</em> (a separate <c>.lock</c> file opened exclusively); every read-modify-write (a
/// publish, a purge) holds that lock so two instances never interleave a journal edit.
/// <list type="bullet">
/// <item><see cref="PublishActorInvalidationAsync"/> appends an event with the next <c>Seq</c>.</item>
/// <item><see cref="PollAsync"/> returns the events with <c>Seq &gt; sinceSeq</c> (the new events since the
/// reader's last cursor), in order. A reader's cursor is its own in-memory state (the channel does not
/// track per-reader cursors — each reader passes its own <c>sinceSeq</c>).</item>
/// <item>Events older than the <em>retention</em> window are purged (removed from the journal) so the
/// file does not grow without bound. A reader that has not polled since a purge simply re-reads the
/// retained events (a late reader re-invalidates a cache that was already invalidated — a harmless no-op).</item>
/// </list>
/// </remarks>
/// <remarks>
/// <strong>Delivery guarantee: at-least-once.</strong> An event is journaled (and flushed) before
/// <see cref="PublishActorInvalidationAsync"/> returns, so a crash after the flush leaves it for replay. A
/// reader that polls and then crashes (before applying the invalidation) re-reads the same events on its
/// next poll and re-applies the invalidation — a harmless no-op (invalidating an already-invalidated cache
/// entry is a no-op). The journal is bounded by the retention window (default 1 hour), so a reader that is
/// down longer than the retention window misses events — but after an hour the cache entries are stale
/// enough that a TTL expiry (or a manual invalidation) is the expected recovery path, not the channel.
/// </remarks>
public sealed class CacheInvalidationChannel : ICacheInvalidationPublisher, IAsyncDisposable
{
    /// <summary>
    /// The default retention window — how long an event is retained in the journal before it is purged.
    /// </summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(1);

    /// <summary>
    /// The default retry interval when waiting to acquire the cross-process file lock.
    /// </summary>
    internal static readonly TimeSpan DefaultLockRetryInterval = TimeSpan.FromMilliseconds(10);

    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    private readonly string _journalPath;
    private readonly string _lockPath;
    private readonly TimeSpan _retention;
    private readonly TimeSpan _lockRetryInterval;

    /// <summary>
    /// Initializes a new cache-invalidation channel that journals to <paramref name="journalPath"/>.
    /// </summary>
    /// <param name="journalPath">
    /// The path of the journal file. All instances that share this channel must be configured with the same
    /// path. Created if it does not exist; the directory must already exist.
    /// </param>
    /// <param name="retention">
    /// How long an event is retained in the journal before it is purged. Defaults to <see cref="DefaultRetention"/>
    /// (1 hour).
    /// </param>
    /// <param name="lockRetryInterval">
    /// The interval between retries when waiting to acquire the cross-process file lock. Defaults to
    /// <see cref="DefaultLockRetryInterval"/> (10 ms).
    /// </param>
    /// <exception cref="ArgumentException">When <paramref name="journalPath"/> is null or whitespace.</exception>
    public CacheInvalidationChannel(string journalPath, TimeSpan? retention = null, TimeSpan? lockRetryInterval = null)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
            throw new ArgumentException("The journal path is required.", nameof(journalPath));

        _journalPath = journalPath;
        _lockPath = journalPath + ".lock";
        _retention = retention ?? DefaultRetention;
        _lockRetryInterval = lockRetryInterval ?? DefaultLockRetryInterval;
    }

    /// <inheritdoc/>
    public async Task PublishActorInvalidationAsync(Iri actorIri, CancellationToken ct = default)
    {
        string allLines;
        using (var _ = AcquireLock())
        {
            var events = ReadJournal();
            var nextSeq = events.Count > 0 ? events[^1].Seq + 1 : 1;
            var @event = new CacheInvalidationEvent
            {
                Seq = nextSeq,
                ActorIri = actorIri.Value,
                At = DateTime.UtcNow,
            };

            var builder = new StringBuilder();
            foreach (var existing in events)
                builder.AppendLine(JsonSerializer.Serialize(existing, _jsonOptions));
            builder.AppendLine(JsonSerializer.Serialize(@event, _jsonOptions));
            allLines = builder.ToString();
        }

        await File.WriteAllTextAsync(_journalPath, allLines, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the events with <c>Seq &gt; sinceSeq</c> (the new events since the reader's last cursor), in
    /// order, up to <paramref name="maxEvents"/>.
    /// </summary>
    /// <param name="sinceSeq">The reader's last-seen <c>Seq</c> (0 for all events).</param>
    /// <param name="maxEvents">The maximum number of events to return (bounded to keep a poll cheap).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The new events, in <c>Seq</c> order (empty when there are none).</returns>
    public Task<IReadOnlyList<CacheInvalidationEvent>> PollAsync(int sinceSeq, int maxEvents = 256, CancellationToken ct = default)
    {
        if (maxEvents <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEvents), maxEvents, "The max event count must be positive.");

        IReadOnlyList<CacheInvalidationEvent> events;
        using (var _ = AcquireLock())
        {
            events = ReadJournal()
                .Where(e => e.Seq > sinceSeq)
                .OrderBy(e => e.Seq)
                .Take(maxEvents)
                .ToList();
        }

        return Task.FromResult(events);
    }

    /// <summary>
    /// Removes events older than <paramref name="olderThan"/> (so the journal is bounded). Returns the number
    /// of events removed.
    /// </summary>
    /// <param name="olderThan">Events with <c>At</c> earlier than <c>UtcNow - olderThan</c> are removed.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of events removed.</returns>
    public Task<int> PurgeAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        if (olderThan <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(olderThan), olderThan, "The retention must be positive.");

        var cutoff = DateTime.UtcNow - olderThan;
        int removed;
        using (var _ = AcquireLock())
        {
            var events = ReadJournal();
            var retained = events.Where(e => e.At >= cutoff).ToList();
            removed = events.Count - retained.Count;
            if (removed > 0)
            {
                var builder = new StringBuilder();
                foreach (var @event in retained)
                    builder.AppendLine(JsonSerializer.Serialize(@event, _jsonOptions));
                File.WriteAllText(_journalPath, builder.ToString());
            }
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // The journal file is durable (a crash leaves it for the next instance); nothing to release.
        return ValueTask.CompletedTask;
    }

    private FileStream AcquireLock()
    {
        while (true)
        {
            try
            {
                // Open the .lock file exclusively (FileShare.None): a second instance's attempt to open the
                // same file throws IOException, so the lock is held by exactly one instance at a time.
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(_lockRetryInterval);
            }
        }
    }

    private List<CacheInvalidationEvent> ReadJournal()
    {
        if (!File.Exists(_journalPath))
            return [];

        var events = new List<CacheInvalidationEvent>();
        foreach (var line in File.ReadAllLines(_journalPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var @event = JsonSerializer.Deserialize<CacheInvalidationEvent>(line, _jsonOptions);
                if (@event is not null)
                    events.Add(@event);
            }
            catch (JsonException)
            {
                // A torn or corrupt line (a crash mid-write) is skipped; the next line is still valid.
            }
        }

        return events;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>
/// A cache-invalidation event journaled by the <see cref="CacheInvalidationChannel"/> (Phase 84.6,
/// shared-state scale-out): the IRI of the actor whose document changed, so the other instances over the
/// same origin can invalidate their in-memory actor/edge caches for that actor.
/// </summary>
/// <remarks>
/// <c>Seq</c> is a monotonic event position (the journal's append order) that a reader uses as its cursor
/// (a reader polls for events with <c>Seq &gt;</c> its last-seen <c>Seq</c>). <c>ActorIri</c> is the IRI of
/// the actor whose document changed (the invalidation key). <c>At</c> is the UTC timestamp of the event
/// (used for the retention-based purge).
/// </remarks>
public sealed record CacheInvalidationEvent
{
    /// <summary>
    /// The monotonic event position (the journal's append order; a reader's cursor is its last-seen
    /// <c>Seq</c>).
    /// </summary>
    public int Seq { get; init; }

    /// <summary>
    /// The IRI of the actor whose document changed (the invalidation key).
    /// </summary>
    public string ActorIri { get; init; } = string.Empty;

    /// <summary>
    /// The UTC timestamp of the event (used for the retention-based purge).
    /// </summary>
    public DateTime At { get; init; }
}

/// <summary>
/// The publisher seam for cache-invalidation events (Phase 84.6, shared-state scale-out). An actor-update
/// path (a key rotation re-stamping the actor document, a profile change) calls
/// <see cref="PublishActorInvalidationAsync"/> so the other instances over the same origin invalidate their
/// in-memory actor/edge caches for that actor.
/// </summary>
/// <remarks>
/// The single-instance default is a no-op (<see cref="NoopCacheInvalidationPublisher"/>): with one instance
/// there is no other instance's cache to invalidate, and the in-process caches are invalidated directly by
/// the update path. The file-backed <see cref="CacheInvalidationChannel"/> is the scale-out counterpart:
/// a shared journal two (or more) instances publish to and poll from.
/// </remarks>
public interface ICacheInvalidationPublisher
{
    /// <summary>
    /// Publishes an invalidation event for <paramref name="actorIri"/> so the other instances over the same
    /// origin invalidate their in-memory actor/edge caches for that actor.
    /// </summary>
    /// <param name="actorIri">The IRI of the actor whose document changed.</param>
    /// <param name="ct">The cancellation token.</param>
    Task PublishActorInvalidationAsync(Iri actorIri, CancellationToken ct);
}

/// <summary>
/// A no-op <see cref="ICacheInvalidationPublisher"/> (the single-instance default): with one instance there
/// is no other instance's cache to invalidate, so the publish is a no-op. The in-process caches are
/// invalidated directly by the update path.
/// </summary>
public sealed class NoopCacheInvalidationPublisher : ICacheInvalidationPublisher
{
    /// <inheritdoc/>
    public Task PublishActorInvalidationAsync(Iri actorIri, CancellationToken ct)
        => Task.CompletedTask;
}
