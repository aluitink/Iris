using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Delivery;

/// <summary>
/// A persistent, file-backed <see cref="IDeliveryDeadLetterStore"/> (Phase 16.2, production
/// persistence): dead-lettered deliveries are journaled to disk (one JSON object per line) so they
/// survive a restart and an operator can still inspect — and re-drive — permanently-failed deliveries
/// after the process restarts.
/// </summary>
/// <remarks>
/// The default <see cref="InMemoryDeliveryDeadLetterStore"/> is ephemeral — a host restart loses the
/// dead-letter history (the operator can no longer see which deliveries permanently failed). A production
/// instance wants that history to survive a restart. This store keeps the same in-memory bounded,
/// newest-first view as the in-memory store (so the <see cref="DeliveryWorker"/> and any operator tooling
/// see identical behavior) and additionally journals each recorded entry to a file. On construction it
/// re-reads the file to restore the entries that were held when the previous process stopped.
/// </remarks>
/// <remarks>
/// <strong>Bounding and eviction.</strong> The in-memory view holds at most <c>capacity</c> entries
/// (the most recent; the oldest are evicted). The journal file, however, is append-only and is not
/// trimmed on eviction (eviction is an in-memory view concern, not a durability concern — the file is the
/// operator's full dead-letter log). A production host that wants a bounded file would pair this with a
/// periodic rotation (out of scope here); for a bounded run the file grows with the number of
/// dead-lettered deliveries.
/// </remarks>
public sealed class FileBackedDeliveryDeadLetterStore : IDeliveryDeadLetterStore
{
    /// <summary>
    /// The default maximum number of dead-lettered entries held in memory before the oldest is evicted —
    /// the same as <see cref="InMemoryDeliveryDeadLetterStore.DefaultCapacity"/>.
    /// </summary>
    public const int DefaultCapacity = 1000;

    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    private readonly string _journalPath;
    private readonly int _capacity;
    private readonly ConcurrentQueue<DeadLetterEntry> _entries = new();
    private readonly SemaphoreSlim _journalLock = new(1, 1);

    /// <summary>
    /// Initializes a new file-backed dead-letter store that journals to <paramref name="journalPath"/>
    /// and restores any entries already in that file.
    /// </summary>
    /// <param name="journalPath">The path of the journal file. Created if it does not exist; the
    /// directory must already exist.</param>
    /// <param name="capacity">The in-memory bound (the most recent <paramref name="capacity"/> entries
    /// are held; the oldest are evicted). Must be greater than 0.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="journalPath"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="capacity"/> is less than or equal to 0.</exception>
    public FileBackedDeliveryDeadLetterStore(string journalPath, int capacity = DefaultCapacity)
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
        _capacity = capacity;
        RestoreFromJournal();
    }

    /// <inheritdoc/>
    public int Count => _entries.Count;

    /// <summary>
    /// The path of the journal file (for inspection).
    /// </summary>
    public string JournalPath => _journalPath;

    /// <inheritdoc/>
    public async Task AddAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();

        // Journal to disk first (so a crash after the flush still leaves the entry for restore), then add
        // to the in-memory bounded view (evicting the oldest beyond the capacity).
        await JournalAsync(entry, ct).ConfigureAwait(false);

        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
            // evict the oldest (in-memory view only; the file is the full log)
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Newest first: the ConcurrentQueue is FIFO, so reverse (the same order as the in-memory store).
        return Task.FromResult<IReadOnlyList<DeadLetterEntry>>(
            _entries.Reverse().ToList());
    }

    /// <summary>
    /// Re-reads the journal file and restores the entries that were held (in file order, which is
    /// oldest-first; the in-memory view then presents them newest-first via <see cref="ListAsync"/>).
    /// Malformed lines (a crash mid-write) are skipped. Runs synchronously at construction.
    /// </summary>
    private void RestoreFromJournal()
    {
        if (!File.Exists(_journalPath))
        {
            return;
        }

        foreach (var line in File.ReadLines(_journalPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = TryParseEntry(line);
            if (entry is not null)
            {
                _entries.Enqueue(entry);
            }
        }

        // Apply the capacity bound to the restored set (drop the oldest beyond the capacity).
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
            // evict the oldest
        }
    }

    private async Task JournalAsync(DeadLetterEntry entry, CancellationToken ct)
    {
        await _journalLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(entry, _jsonOptions);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");
            await using var stream = new FileStream(
                _journalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _journalLock.Release();
        }
    }

    private static DeadLetterEntry? TryParseEntry(string line)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<DeadLetterEntry>(line, _jsonOptions);
            return entry is { Activity: not null, InboxIri: { } inbox } && inbox.Value.Length > 0 ? entry : null;
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
    /// journal round-trips the value type. Registered for <c>Iri</c> so the record's <c>InboxIri</c>
    /// property round-trips.
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
    /// <c>Follow</c>, etc.).
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
