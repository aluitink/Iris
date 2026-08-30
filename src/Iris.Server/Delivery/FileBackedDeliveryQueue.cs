using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Delivery;

/// <summary>
/// A persistent, file-backed <see cref="IDeliveryQueue"/> (Phase 16.2, production persistence): pending
/// outbound federation <see cref="DeliveryJob"/>s are journaled to disk (one JSON object per line) as
/// they are enqueued, and replayed into an in-memory channel on construction.
/// </summary>
/// <remarks>
/// The default <see cref="InMemoryDeliveryQueue"/> is ephemeral — a host restart loses every pending
/// delivery (a follow that was scheduled to be delivered, a boost, a relay fan-out). A production
/// instance wants those to survive a restart so the federation edge is not silently dropped. This queue
/// journals each enqueued job to a file (append, line-delimited) before it is handed to the in-memory
/// channel, and on construction re-reads the file to replay any jobs that were pending when the previous
/// process stopped.
/// </remarks>
/// <remarks>
/// <strong>Durability model.</strong> Enqueueing is <em>at-least-once</em>: a job is journaled to disk
/// (and flushed) before it is written to the channel, so a crash after the flush leaves the job on disk
/// for replay; a crash before the flush loses the job (the same window as the in-memory queue). The
/// receiving instance dedupes a re-delivered activity by its <c>Id</c> (C-07), so replaying a job that
/// was in fact already delivered is a harmless no-op. The in-memory channel provides the same back
/// pressure and bounded capacity as <see cref="InMemoryDeliveryQueue"/>.
/// </remarks>
/// <remarks>
/// <strong>Replay and truncation.</strong> On construction, existing journal lines are parsed and
/// enqueued into the channel. A job that was already dequeued by the previous process (and is now being
/// redelivered) is harmless (deduped by the receiver). To prevent the journal from growing without bound
/// across many restarts (jobs that were replayed and delivered but never removed from the file),
/// <see cref="TruncateAsync"/> rewrites the journal to contain only the jobs still pending — call it on
/// a clean shutdown (after the queue has drained) to keep the file small. When <see cref="TruncateAsync"/>
/// is not called, the journal is append-only and grows with every delivery (acceptable for a bounded
/// run; a production host should truncate on clean shutdown).
/// </remarks>
public sealed class FileBackedDeliveryQueue : IDeliveryQueue, IAsyncDisposable
{
    /// <summary>
    /// The default channel capacity (jobs) — the same as <see cref="InMemoryDeliveryQueue.DefaultCapacity"/>.
    /// </summary>
    public const int DefaultCapacity = 1000;

    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    private readonly string _journalPath;
    private readonly Channel<DeliveryJob> _channel;
    private readonly SemaphoreSlim _journalLock = new(1, 1);
    private bool _completed;

    /// <summary>
    /// Initializes a new file-backed queue that journals to <paramref name="journalPath"/> and replays
    /// any pending jobs already in that file.
    /// </summary>
    /// <param name="journalPath">The absolute (or relative) path of the journal file. Created if it does
    /// not exist; the directory must already exist.</param>
    /// <param name="capacity">The in-memory channel capacity (back-pressure bound). Must be greater than 0.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="journalPath"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="capacity"/> is less than or equal to 0.</exception>
    public FileBackedDeliveryQueue(string journalPath, int capacity = DefaultCapacity)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            throw new ArgumentNullException(nameof(journalPath));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
        }

        _journalPath = journalPath;
        _channel = Channel.CreateBounded<DeliveryJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

        ReplayJournal();
    }

    /// <inheritdoc/>
    public int Count => _channel.Reader.Count;

    /// <summary>
    /// The path of the journal file (for inspection / <see cref="TruncateAsync"/>).
    /// </summary>
    public string JournalPath => _journalPath;

    /// <inheritdoc/>
    public async Task EnqueueAsync(DeliveryJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_completed)
        {
            throw new InvalidOperationException("The queue is complete; no further jobs can be enqueued.");
        }

        // Journal to disk (and flush) BEFORE the channel write so a crash after the flush still leaves
        // the job for replay (at-least-once). A crash before the flush loses the job (the same window as
        // the in-memory queue).
        await JournalAsync(job, ct).ConfigureAwait(false);
        await _channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DeliveryJob?> TryDequeueAsync(CancellationToken ct = default)
    {
        if (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            _channel.Reader.TryRead(out var job);
            return job;
        }

        return null;
    }

    /// <inheritdoc/>
    public Task CompleteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _completed = true;
        _channel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rewrites the journal file to contain only the jobs currently still pending in the channel, so the
    /// file does not grow without bound across restarts. Call this on a clean shutdown after the queue
    /// has drained. A no-op when the channel is empty (the journal is truncated to nothing).
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the journal has been rewritten.</returns>
    public async Task TruncateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var pending = SnapshotPending();
        await _journalLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Rewrite the journal atomically (write to a temp file, then move over the original) so a
            // crash mid-truncate does not corrupt the journal.
            var tempPath = _journalPath + ".tmp";
            var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            try
            {
                foreach (var job in pending)
                {
                    await WriteLineAsync(temp, job, ct).ConfigureAwait(false);
                }

                await temp.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                temp.Dispose();
            }

            File.Move(tempPath, _journalPath, overwrite: true);
        }
        finally
        {
            _journalLock.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _journalLock.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Re-reads the journal file and re-enqueues any pending jobs (without journaling them again — they
    /// are already on disk). Runs synchronously on the calling thread at construction (the file is small
    /// relative to a queue's lifetime; an async constructor is not possible in C#).
    /// </summary>
    private void ReplayJournal()
    {
        if (!File.Exists(_journalPath))
        {
            return;
        }

        // Read all lines; each is one journaled job. Malformed lines (a crash mid-write) are skipped.
        var lines = File.ReadLines(_journalPath).ToList();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var job = TryParseJob(line);
            if (job is not null)
            {
                // Enqueue into the channel directly (the job is already journaled; do not journal again).
                _channel.Writer.TryWrite(job);
            }
        }
    }

    /// <summary>
    /// Appends a job to the journal file as one JSON line and flushes it to disk (so the bytes are
    /// durable before <see cref="EnqueueAsync"/> returns).
    /// </summary>
    private async Task JournalAsync(DeliveryJob job, CancellationToken ct)
    {
        await _journalLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                _journalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await WriteLineAsync(stream, job, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _journalLock.Release();
        }
    }

    private static async Task WriteLineAsync(Stream stream, DeliveryJob job, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(job, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Snapshots the jobs currently pending in the channel (reading and re-enqueueing them, preserving
    /// order) — the same read-drain-requeue pattern as <see cref="InMemoryDeliveryQueue.Jobs"/>.
    /// </summary>
    private List<DeliveryJob> SnapshotPending()
    {
        var jobs = new List<DeliveryJob>();
        while (_channel.Reader.TryRead(out var job))
        {
            jobs.Add(job);
        }

        foreach (var job in jobs)
        {
            _channel.Writer.TryWrite(job);
        }

        return jobs;
    }

    /// <summary>
    /// Parses a journaled job from a single JSON line. Returns null when the line is malformed (e.g. a
    /// crash mid-write), so a torn line does not break the replay.
    /// </summary>
    private static DeliveryJob? TryParseJob(string line)
    {
        try
        {
            var job = JsonSerializer.Deserialize<DeliveryJob>(line, _jsonOptions);
            return job is { Activity: not null, InboxIri: { } inbox } && inbox.Value.Length > 0 ? job : null;
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
        options.Converters.Add(new IriValueConverter());
        options.Converters.Add(new IriNullableConverter());
        options.Converters.Add(new ActivityJsonConverter());
        return options;
    }

    /// <summary>
    /// Serializes a non-nullable <see cref="Iri"/> as its string value and reads it back, so the
    /// journal round-trips the value type (which is not directly JSON-serializable). Registered for
    /// <c>Iri</c> so the record's <c>InboxIri</c> property round-trips.
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
    /// Registered for <c>Iri?</c> because <see cref="Iri"/> is a readonly struct and the record's
    /// <c>ActorIri</c> property is nullable.
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
    /// <see cref="ActivityJson"/>) and reads it back through <c>IObjectOrLink</c>, so the journal
    /// preserves the concrete activity type (the <c>type</c> discriminator dispatches to <c>Create</c>,
    /// <c>Follow</c>, etc. — deserializing into the base <c>Activity</c> type would lose it).
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

            // IObjectOrLink dispatches on the "type" property to the concrete CLR type (Create, Follow,
            // ...). Deserializing into the base Activity type would return a plain Activity.
            return ActivityJson.Deserialize<IObjectOrLink>(json) as Activity;
        }

        public override void Write(Utf8JsonWriter writer, Activity value, JsonSerializerOptions options)
            => writer.WriteStringValue(ActivityJson.Serialize(value));
    }
}
