using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Delivery;

/// <summary>
/// A <strong>shared, file-backed</strong> <see cref="IDeliveryQueue"/> (Phase 84.6, shared-state scale-out):
/// two (or more) Iris instances over the same origin enqueue into and consume from <em>one</em> durable
/// journal, so a delivery scheduled on instance A is delivered by A-or-B — not dropped — and a single
/// journal is the source of truth for the federation edge.
/// </summary>
/// <remarks>
/// <strong>The single-instance gap.</strong> The default <see cref="InMemoryDeliveryQueue"/> and the
/// restart-durable <see cref="FileBackedDeliveryQueue"/> are both <em>per-instance</em>: each instance
/// holds its own in-memory channel, and (for the file-backed one) replays the journal into its own channel
/// <em>at construction</em>. Two instances over one origin therefore each try to deliver the same journal
/// (double-delivery), and — worse — a delivery enqueued on A <em>after</em> B started is never visible to
/// B (B's channel was populated at B's startup, before A's enqueue). The 84.5 single-instance guard made
/// this constraint explicit; this queue is the scale-out half: a shared queue two instances can coexist
/// over.
/// </remarks>
/// <remarks>
/// <strong>Model: a visibility-timeout queue over a JSON journal.</strong> Each journaled line is a
/// <see cref="DeliveryQueueRecord"/> — a <see cref="DeliveryJob"/> plus a monotonic <c>Seq</c> (queue
/// position), a <c>Status</c> (<c>Pending</c> or <c>Claimed</c>), and a claim stamp (<c>ClaimedBy</c> —
/// the claiming instance's id, and <c>ClaimedAt</c> — when it was claimed). The journal is guarded by a
/// cross-process <em>file lock</em> (a separate <c>.lock</c> file opened exclusively); every
/// read-modify-write (an enqueue, a claim, a purge) holds that lock so two instances never interleave a
/// journal edit.
/// <list type="bullet">
/// <item><see cref="EnqueueAsync"/> appends a <c>Pending</c> record with the next <c>Seq</c>.</item>
/// <item><see cref="TryDequeueAsync"/> finds the lowest-<c>Seq</c> record that is <c>Pending</c> or
/// <c>Claimed</c> <em>past its visibility timeout</em> (reclaimable — its claimer crashed or is slow),
/// marks it <c>Claimed</c> (stamping this instance's id + the current time), and returns the job. The
/// record stays in the journal (the claim is a <em>visibility</em> change, not a removal).</item>
/// <item>Records <c>Claimed</c> past the <em>drop</em> horizon are purged (removed from the journal) so
/// the file does not grow without bound.</item>
/// </list>
/// </remarks>
/// <remarks>
/// <strong>Delivery guarantee: at-least-once (the same as <see cref="FileBackedDeliveryQueue"/>).</strong>
/// A job is journaled (and flushed) before it is handed to a consumer, so a crash after the flush leaves
/// it for replay. A consumer that claims a job and crashes (or is slow past the visibility timeout) leaves
/// the job reclaimable — another instance re-claims and re-delivers it. The receiving instance dedupes a
/// re-delivered activity by its <c>Id</c> (C-07), so a re-delivery (a job delivered successfully and then
/// re-claimed when its visibility window lapses) is a harmless no-op. The worker's own F-22 retry /
/// dead-letter policy (Phase 17.3) handles per-delivery failures; the queue only gets the job to a worker
/// at least once.
/// </remarks>
/// <remarks>
/// <strong>Visibility vs. drop horizons.</strong> <see cref="DefaultVisibilityTimeout"/> (10 min) is how
/// long a claim is <em>exclusive</em>: a job claimed by a live instance is not re-claimed by another
/// instance within that window. It is deliberately longer than the worst-case F-22 retry budget
/// (~15 s of backoff across 5 attempts + 5 network calls), so a slow-but-healthy delivery is not
/// re-delivered while it is still in flight. <see cref="DefaultDropHorizon"/> (24 h) is how long a
/// <c>Claimed</c> record is retained before it is purged: after a day, re-delivery is no longer useful and
/// the record is dropped to keep the journal small. Both are configurable per queue.
/// </remarks>
public sealed class SharedDeliveryQueue : IDeliveryQueue, IAsyncDisposable
{
    /// <summary>
    /// The default visibility timeout — how long a claim is exclusive before the job is reclaimable.
    /// </summary>
    public static readonly TimeSpan DefaultVisibilityTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The default drop horizon — how long a <c>Claimed</c> record is retained before it is purged from the
    /// journal.
    /// </summary>
    public static readonly TimeSpan DefaultDropHorizon = TimeSpan.FromHours(24);

    /// <summary>
    /// The default retry interval when waiting to acquire the cross-process file lock.
    /// </summary>
    internal static readonly TimeSpan DefaultLockRetryInterval = TimeSpan.FromMilliseconds(10);

    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    private readonly string _journalPath;
    private readonly string _lockPath;
    private readonly TimeSpan _visibilityTimeout;
    private readonly TimeSpan _dropHorizon;
    private readonly TimeSpan _lockRetryInterval;
    private readonly Guid _instanceId;
    private bool _completed;

    /// <summary>
    /// Initializes a new shared delivery queue that journals to <paramref name="journalPath"/>.
    /// </summary>
    /// <param name="journalPath">
    /// The path of the journal file. All instances that share this queue must be configured with the same
    /// path. Created if it does not exist; the directory must already exist.
    /// </param>
    /// <param name="visibilityTimeout">
    /// How long a claim is exclusive before the job is reclaimable by another instance. Defaults to
    /// <see cref="DefaultVisibilityTimeout"/> (10 min).
    /// </param>
    /// <param name="dropHorizon">
    /// How long a <c>Claimed</c> record is retained before it is purged from the journal. Defaults to
    /// <see cref="DefaultDropHorizon"/> (24 h). Must be greater than or equal to <paramref
    /// name="visibilityTimeout"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">When <paramref name="journalPath"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="visibilityTimeout"/> is
    /// non-positive or <paramref name="dropHorizon"/> is less than <paramref name="visibilityTimeout"/>.</exception>
    public SharedDeliveryQueue(
        string journalPath,
        TimeSpan? visibilityTimeout = null,
        TimeSpan? dropHorizon = null)
        : this(journalPath, visibilityTimeout, dropHorizon, DefaultLockRetryInterval)
    {
    }

    internal SharedDeliveryQueue(
        string journalPath,
        TimeSpan? visibilityTimeout,
        TimeSpan? dropHorizon,
        TimeSpan lockRetryInterval)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            throw new ArgumentNullException(nameof(journalPath));
        }

        var visibility = visibilityTimeout ?? DefaultVisibilityTimeout;
        var drop = dropHorizon ?? DefaultDropHorizon;
        if (visibility <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(visibilityTimeout), visibility, "The visibility timeout must be greater than zero.");
        }

        if (drop < visibility)
        {
            throw new ArgumentOutOfRangeException(nameof(dropHorizon), drop, "The drop horizon must be greater than or equal to the visibility timeout.");
        }

        if (lockRetryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lockRetryInterval), lockRetryInterval, "The lock retry interval must be greater than zero.");
        }

        _journalPath = journalPath;
        _lockPath = journalPath + ".lock";
        _visibilityTimeout = visibility;
        _dropHorizon = drop;
        _lockRetryInterval = lockRetryInterval;
        _instanceId = Guid.NewGuid();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The pending count is read from the shared journal (under the cross-process lock) so it reflects
    /// the true pending pool — not a per-instance cache (which would be stale when another instance
    /// enqueues or claims). This is a file read + lock acquisition, so it is not intended for a hot loop;
    /// it is for observability (the <c>IDeliveryQueue.Count</c> contract is "approximate," but the shared
    /// queue provides an exact read of the shared state).
    /// </remarks>
    public int Count => ReadPendingCountSync();

    /// <summary>
    /// The path of the journal file (for inspection).
    /// </summary>
    public string JournalPath => _journalPath;

    /// <summary>
    /// The unique identifier of this queue instance (recorded in the claim stamp so an operator can tell
    /// which instance claimed a job).
    /// </summary>
    public Guid InstanceId => _instanceId;

    /// <inheritdoc/>
    public async Task EnqueueAsync(DeliveryJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_completed)
        {
            throw new InvalidOperationException("The queue is complete; no further jobs can be enqueued.");
        }

        await WithLockAsync(async () =>
        {
            var records = await ReadJournalAsync(ct).ConfigureAwait(false);
            var nextSeq = records.Count == 0 ? 1 : records.Max(r => r.Seq) + 1;
            records.Add(new DeliveryQueueRecord
            {
                Seq = nextSeq,
                Job = job,
                Status = DeliveryRecordStatus.Pending,
            });
            await WriteJournalAsync(records, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DeliveryJob?> TryDequeueAsync(CancellationToken ct = default)
    {
        if (_completed)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        DeliveryJob? claimed = null;
        await WithLockAsync(async () =>
        {
            // Purge records claimed past the drop horizon FIRST (they are delivered and re-delivery is no
            // longer useful — this is what keeps the journal bounded). A record past the drop horizon is
            // also past the visibility timeout, so it must not be selected as a candidate; pruning it
            // before the candidate selection is what makes "purged, not re-claimed" hold.
            var records = await ReadJournalAsync(ct).ConfigureAwait(false);
            var pruned = records
                .Where(r => !(r.Status == DeliveryRecordStatus.Claimed && r.ClaimedAt is { } at && now - at >= _dropHorizon))
                .ToList();

            var candidate = pruned
                .Where(r =>
                    r.Status == DeliveryRecordStatus.Pending ||
                    (r.Status == DeliveryRecordStatus.Claimed && r.ClaimedAt is { } at && now - at >= _visibilityTimeout))
                .OrderBy(r => r.Seq)
                .FirstOrDefault();

            if (candidate is not null)
            {
                candidate.Status = DeliveryRecordStatus.Claimed;
                candidate.ClaimedBy = _instanceId.ToString();
                candidate.ClaimedAt = now;
                claimed = candidate.Job;
            }

            await WriteJournalAsync(pruned, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return claimed;
    }

    /// <inheritdoc/>
    public Task CompleteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _completed = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _completed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reads the pending count from the shared journal (under the cross-process lock, synchronously) so
    /// <see cref="Count"/> reflects the true pending pool across all instances — not a per-instance cache.
    /// A synchronous <see cref="FileStream"/> open with <see cref="FileShare.None"/> throws
    /// <see cref="IOException"/> (rather than blocking) when another instance holds the lock, so this spins
    /// on a short sleep until the lock is acquired (a bounded, approximate wait — the journal is small and
    /// the critical section is short).
    /// </summary>
    private int ReadPendingCountSync()
    {
        FileStream? stream = null;
        var spins = 0;
        while (stream is null)
        {
            // A bounded spin: the lock is held only for the duration of a journal read/write (a few ms),
            // so a few hundred 1-ms spins is more than enough. If it ever exceeded that, something is wedged;
            // return the last known value (0) rather than blocking the caller indefinitely.
            if (++spins > 1000)
            {
                return 0;
            }

            try
            {
                stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(_lockRetryInterval);
            }
        }

        try
        {
            if (!File.Exists(_journalPath))
            {
                return 0;
            }

            var count = 0;
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var record = TryParseRecord(line);
                if (record is { Status: DeliveryRecordStatus.Pending })
                {
                    count++;
                }
            }

            return count;
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> while holding the cross-process file lock. The lock is a
    /// <see cref="FileStream"/> on <c>_lockPath</c> opened with <see cref="FileShare.None"/> (exclusive);
    /// when another instance holds it, the open is retried on a short interval until it is acquired or
    /// <paramref name="ct"/> is cancelled. The lock is released (and the file left in place) when
    /// <paramref name="action"/> returns.
    /// </summary>
    private async Task WithLockAsync(Func<Task> action, CancellationToken ct)
    {
        FileStream? stream = null;
        while (stream is null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Another instance holds the lock. Back off and retry.
                await Task.Delay(_lockRetryInterval, ct).ConfigureAwait(false);
            }
        }

        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            // Releasing the handle (not deleting the file) is what releases the lock; the file persists so
            // the next instance can acquire it.
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads and parses the journal file into its records. Returns an empty list when the file is absent.
    /// Malformed lines (a crash mid-write) are skipped so a torn line does not break the journal.
    /// </summary>
    private async Task<List<DeliveryQueueRecord>> ReadJournalAsync(CancellationToken ct)
    {
        var records = new List<DeliveryQueueRecord>();
        if (!File.Exists(_journalPath))
        {
            return records;
        }

        using var reader = new StreamReader(_journalPath);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = TryParseRecord(line);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    /// <summary>
    /// Rewrites the journal atomically (write to a temp file, flush, then move over the original) so a
    /// crash mid-write does not corrupt the journal.
    /// </summary>
    private async Task WriteJournalAsync(IReadOnlyList<DeliveryQueueRecord> records, CancellationToken ct)
    {
        var tempPath = _journalPath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var record in records)
            {
                var json = JsonSerializer.Serialize(record, _jsonOptions);
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tempPath, _journalPath, overwrite: true);
    }

    /// <summary>
    /// Parses a journaled record from a single JSON line. Returns null when the line is malformed (e.g. a
    /// crash mid-write), so a torn line does not break the journal.
    /// </summary>
    internal static DeliveryQueueRecord? TryParseRecord(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<DeliveryQueueRecord>(line, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        // The Job property (a DeliveryJob) is carried as a JSON string (flat record shape); the job's own
        // Iri/Activity converters are applied inside the string by DeliveryJobJsonConverter.
        options.Converters.Add(new DeliveryJobJsonConverter());
        return options;
    }

    /// <summary>
    /// The status of a journaled record in the shared delivery queue.
    /// </summary>
    public enum DeliveryRecordStatus
    {
        /// <summary>The job is waiting to be claimed by an instance.</summary>
        Pending = 0,

        /// <summary>The job has been claimed by an instance and is in flight (reclaimable past the visibility timeout).</summary>
        Claimed = 1,
    }

    /// <summary>
    /// A single journaled record in the shared delivery queue: a <see cref="DeliveryJob"/> plus its queue
    /// position (<see cref="Seq"/>), delivery <see cref="Status"/>, and (when claimed) the claim stamp
    /// (<see cref="ClaimedBy"/> — the claiming instance's id, and <see cref="ClaimedAt"/> — when it was
    /// claimed). The <see cref="Job"/> is serialized as a JSON <em>string</em> (not an embedded object) so
    /// the record's own JSON shape stays flat and the job's ActivityStreams JSON round-trips through the
    /// same converters the <see cref="FileBackedDeliveryQueue"/> uses.
    /// </summary>
    public sealed record DeliveryQueueRecord
    {
        /// <summary>The monotonically increasing queue position (assigned at enqueue, under the lock).</summary>
        public int Seq { get; init; }

        /// <summary>
        /// The job to deliver. Serialized as a JSON <em>string</em> (not an embedded object) by
        /// <see cref="DeliveryJobJsonConverter"/>, which keeps this record's shape flat and lets the
        /// job's Activity round-trip through <see cref="ActivityJson"/> (polymorphic by <c>type</c>).
        /// </summary>
        public DeliveryJob Job { get; init; } = default!;

        /// <summary>The delivery status (pending or claimed).</summary>
        public DeliveryRecordStatus Status { get; set; }

        /// <summary>The id of the instance that claimed this job (null while pending).</summary>
        public string? ClaimedBy { get; set; }

        /// <summary>When this job was claimed (null while pending).</summary>
        public DateTimeOffset? ClaimedAt { get; set; }
    }

    /// <summary>
    /// Serializes a <see cref="DeliveryJob"/> as a JSON <em>string</em> (the job's own JSON, produced by
    /// the same converters the <see cref="FileBackedDeliveryQueue"/> uses) and reads it back. Carrying the
    /// job as a string (rather than an embedded object) keeps <see cref="DeliveryQueueRecord"/> flat and
    /// lets the job's Activity round-trip through <see cref="ActivityJson"/> (polymorphic by <c>type</c>).
    /// </summary>
    private sealed class DeliveryJobJsonConverter : JsonConverter<DeliveryJob>
    {
        private static readonly JsonSerializerOptions JobOptions = CreateJobOptions();

        public override DeliveryJob? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var json = reader.GetString();
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<DeliveryJob>(json, JobOptions);
        }

        public override void Write(Utf8JsonWriter writer, DeliveryJob value, JsonSerializerOptions options)
            => writer.WriteStringValue(JsonSerializer.Serialize(value, JobOptions));

        private static JsonSerializerOptions CreateJobOptions()
        {
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            options.Converters.Add(new IriValueConverter());
            options.Converters.Add(new IriNullableConverter());
            options.Converters.Add(new ActivityJsonConverter());
            return options;
        }
    }

    /// <summary>
    /// Serializes a non-nullable <see cref="Iri"/> as its string value and reads it back.
    /// </summary>
    private sealed class IriValueConverter : JsonConverter<Iri>
    {
        public override Iri Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new Iri(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Iri value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    /// <summary>
    /// Serializes a nullable <see cref="Iri"/> as null or its string value and reads it back.
    /// </summary>
    private sealed class IriNullableConverter : JsonConverter<Iri?>
    {
        public override Iri? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? null : new Iri(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Iri? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value.ToString());
            }
        }
    }

    /// <summary>
    /// Serializes a polymorphic <see cref="Activity"/> to its ActivityStreams JSON (via
    /// <see cref="ActivityJson"/>) and reads it back through <c>IObjectOrLink</c>, so the journal preserves
    /// the concrete activity type.
    /// </summary>
    private sealed class ActivityJsonConverter : JsonConverter<Activity>
    {
        public override Activity? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var json = reader.GetString();
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            return ActivityJson.Deserialize<IObjectOrLink>(json) as Activity;
        }

        public override void Write(Utf8JsonWriter writer, Activity value, JsonSerializerOptions options)
            => writer.WriteStringValue(ActivityJson.Serialize(value));
    }
}
