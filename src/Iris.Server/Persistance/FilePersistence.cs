using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Persistance;

/// <summary>
/// A small, self-contained, file-backed persistence primitive (Phase 16.4, production persistence): a
/// single JSON document on disk that is read into memory at construction and rewritten atomically (temp
/// file + <c>File.Move</c> overwrite) on every mutation.
/// </summary>
/// <remarks>
/// <strong>Why file-backed (and not a database).</strong> The codebase's rule is no new NuGet packages
/// without a ROADMAP note + justification, and a relational driver (EF Core, Npgsql, …) would be a new
/// dependency. Phase 16.2 already established the file-backed pattern for the delivery queue (BCL +
/// <see cref="System.Text.Json"/>). This primitive follows the same pattern for the
/// <see cref="IPersistenceProvider"/> stores: a production host that wants a real database still swaps
/// in a different <see cref="IPersistenceProvider"/> implementation behind the same seam.
/// </remarks>
/// <remarks>
/// <strong>Durability model.</strong> A mutation is durable when its atomic rewrite has completed.
/// In-memory state is the source of truth between rewrites; a crash before a rewrite loses that one
/// mutation (the same window as the in-memory store). Because the rewrite is atomic (write to a temp
/// file in the same directory, then move over the original), a crash mid-write never leaves a torn or
/// empty file — the file always holds either the previous snapshot or the new one.
/// </remarks>
/// <remarks>
/// <strong>Concurrency.</strong> All reads and writes are serialized by a single <see cref="SemaphoreSlim"/>
/// per store file, so a store is safe under concurrent access (the server is multi-threaded). The file
/// I/O is async; the critical section is held only for the snapshot read/write.
/// </remarks>
/// <remarks>
/// <strong>Public surface.</strong> This class and its JSON converters / document models are public so a
/// host can construct the file-backed stores directly (e.g. in tests or a custom host) without going
/// through the aggregate provider. They are deliberately small and stable.
/// </remarks>
public sealed class FilePersistence : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, object> _state = new(StringComparer.Ordinal);
    private readonly Func<ConcurrentDictionary<string, object>, JsonDocument> _toDocument;
    private readonly Action<JsonElement, ConcurrentDictionary<string, object>> _fromDocument;

    /// <summary>
    /// The pre-configured <see cref="JsonSerializerOptions"/> shared by every store file: the
    /// <see cref="Iri"/> value-type converters (so value-type keys round-trip) and the
    /// ActivityStreams converters (so polymorphic objects/activities round-trip their concrete type via
    /// <see cref="IObjectOrLink"/>).
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>
    /// Initializes a new file-backed store over <paramref name="path"/>, reading any existing document.
    /// </summary>
    /// <param name="path">The path of the store file. Created on the first write; the directory must
    /// already exist.</param>
    /// <param name="toDocument">Serializes the in-memory state to a <see cref="JsonDocument"/> on write.</param>
    /// <param name="fromDocument">Populates the in-memory state from the file's root element on read.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> or a mapping is null.</exception>
    public FilePersistence(
        string path,
        Func<ConcurrentDictionary<string, object>, JsonDocument> toDocument,
        Action<JsonElement, ConcurrentDictionary<string, object>> fromDocument)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        ArgumentNullException.ThrowIfNull(toDocument);
        ArgumentNullException.ThrowIfNull(fromDocument);

        _path = path;
        _toDocument = toDocument;
        _fromDocument = fromDocument;

        LoadFromDisk();
    }

    /// <summary>
    /// The path of the store file (for inspection / tests).
    /// </summary>
    public string Path => _path;

    /// <summary>
    /// Runs <paramref name="action"/> against the in-memory state under the store lock. The action may
    /// mutate the state; when <paramref name="persist"/> is <see langword="true"/> the state is
    /// atomically rewritten to disk.
    /// </summary>
    /// <param name="action">Reads/mutates the state.</param>
    /// <param name="persist">When true, rewrite the file after the action.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the operation (and any rewrite) is done.</returns>
    /// <typeparam name="T">The result type of <paramref name="action"/>.</typeparam>
    public async Task<T> WithStateAsync<T>(
        Func<ConcurrentDictionary<string, object>, T> action,
        bool persist,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = action(_state);
            if (persist)
            {
                await WriteAsync(ct).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> against the in-memory state under the store lock, synchronously.
    /// When <paramref name="persist"/> is <see langword="true"/> the state is rewritten to disk
    /// synchronously. The file write is a single small JSON document, so a synchronous write is acceptable
    /// for the synchronous <see cref="IKeyStore"/> seam (the signing pipeline calls it on the request
    /// path); the async <see cref="WithStateAsync{T}"/> is preferred on the async store seams.
    /// </summary>
    /// <param name="action">Reads/mutates the state.</param>
    /// <param name="persist">When true, rewrite the file after the action.</param>
    /// <returns>The result of <paramref name="action"/>.</returns>
    /// <typeparam name="T">The result type of <paramref name="action"/>.</typeparam>
    public T WithState<T>(Func<ConcurrentDictionary<string, object>, T> action, bool persist)
    {
        _lock.Wait();
        try
        {
            var result = action(_state);
            if (persist)
            {
                WriteSync();
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Atomically rewrites the store file from the in-memory state, synchronously (temp file + move). The
    /// caller must hold the store lock.
    /// </summary>
    private void WriteSync()
    {
        using var document = _toDocument(_state);
        var json = document.RootElement.GetRawText();

        var tempPath = _path + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, _path, overwrite: true);
    }

    /// <summary>
    /// Releases the store's lock. The in-memory state and the file on disk are left in place; this only
    /// frees the <see cref="SemaphoreSlim"/> that serializes reads/writes.
    /// </summary>
    public void Dispose() => _lock.Dispose();

    /// <summary>
    /// Reads the in-memory state under the store lock (no write).
    /// </summary>
    /// <param name="action">Reads the state.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the read is done.</returns>
    /// <typeparam name="T">The result type of <paramref name="action"/>.</typeparam>
    public async Task<T> SnapshotAsync<T>(Func<ConcurrentDictionary<string, object>, T> action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return action(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Reads the in-memory state synchronously under the store lock (no write). The state is an
    /// in-memory structure (no disk I/O), so a synchronous read is safe and lets callers use
    /// <c>out</c> parameters (which async methods cannot).
    /// </summary>
    /// <param name="action">Reads the state.</param>
    /// <returns>The result of <paramref name="action"/>.</returns>
    /// <typeparam name="T">The result type of <paramref name="action"/>.</typeparam>
    public T Snapshot<T>(Func<ConcurrentDictionary<string, object>, T> action)
    {
        _lock.Wait();
        try
        {
            return action(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Reads the store file (if present) into the in-memory state. A missing or empty file leaves the
    /// state empty; a malformed file (a hand-edited or truncated file) is treated as empty rather than
    /// thrown on, so the host still starts.
    /// </summary>
    private void LoadFromDisk()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        var json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            _fromDocument(document.RootElement, _state);
        }
        catch (JsonException)
        {
            // A corrupt file is treated as empty (the host starts with no data rather than failing to
            // start). The file is left in place so an operator can inspect it.
            _state.Clear();
        }
    }

    /// <summary>
    /// Atomically rewrites the store file from the in-memory state (temp file + move). The caller must
    /// hold the store lock.
    /// </summary>
    private async Task WriteAsync(CancellationToken ct)
    {
        using var document = _toDocument(_state);
        var json = document.RootElement.GetRawText();

        var tempPath = _path + ".tmp";
        await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await temp.WriteAsync(bytes, ct).ConfigureAwait(false);
            await temp.FlushAsync(ct).ConfigureAwait(false);
        }

        // Atomic replace: a crash mid-rename leaves either the old or the new file, never a torn one.
        File.Move(tempPath, _path, overwrite: true);
    }

    /// <summary>
    /// Creates the shared JSON options: the <see cref="Iri"/> value-type converters (for the
    /// <see cref="IriEdge"/> and <see cref="DocumentEntry"/> record properties). ActivityStreams
    /// document payloads are stored as JSON strings (the <see cref="DocumentEntry.Json"/> field) and
    /// round-tripped through <see cref="ActivityJson"/> on read, so no custom converters are needed for
    /// the polymorphic ActivityStreams types.
    /// </summary>
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(new IriValueConverter());
        options.Converters.Add(new IriNullableConverter());
        return options;
    }

    /// <summary>
    /// Serializes a non-nullable <see cref="Iri"/> as its string value and reads it back.
    /// </summary>
    public sealed class IriValueConverter : JsonConverter<Iri>
    {
        public override Iri Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new Iri(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Iri value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    /// <summary>
    /// Serializes a nullable <see cref="Iri"/> as null or its string value and reads it back.
    /// </summary>
    public sealed class IriNullableConverter : JsonConverter<Iri?>
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
    /// A directed IRI edge (e.g. a follow, like, reply, block, flag, mute, or relay subscription): a
    /// source IRI and a target IRI. Used by the edge-store file format.
    /// </summary>
    /// <param name="Source">The IRI of the source (actor, object, or community).</param>
    /// <param name="Target">The IRI of the target (actor, object, or community).</param>
    public sealed record IriEdge(Iri Source, Iri Target);

    /// <summary>
    /// A stored document (actor / activity / object / community): the document's IRI and its JSON
    /// payload (the ActivityStreams JSON, round-tripped through <see cref="IObjectOrLink"/> on read).
    /// </summary>
    /// <param name="Iri">The IRI that identifies the document.</param>
    /// <param name="Json">The ActivityStreams JSON payload.</param>
    public sealed record DocumentEntry(Iri Iri, string Json);
}
