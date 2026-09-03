using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="IKeyStore"/> (Phase 16.4, production persistence): the local instance's
/// signing keys persisted to a single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// Each key is stored as its <see cref="KeyAlgorithm"/>, its key IRI, and its PKCS#8 private-key PEM
/// (the form <see cref="KeyPair.ExportPrivateKeyPem"/> / <see cref="Ed25519Key.ExportPrivateKeyPem"/>
/// produce). On <see cref="IKeyStore.TryGetKey"/> the key is reconstructed from the PEM via
/// <see cref="KeyPair.FromPem"/> / <see cref="Ed25519Key.FromPem"/>. Because the private PEM is the
/// canonical lossless form for all three algorithms (RSA, EC P-256, and Ed25519), round-tripping
/// through it preserves the exact key material — a signature made before a restart verifies identically
/// after one.
/// </remarks>
/// <remarks>
/// <see cref="IKeyStore"/> is synchronous (the signing pipeline calls it on the request path), so this
/// store reads the file once at construction into an in-memory index and rewrites the file atomically on
/// each <see cref="IKeyStore.PutKey"/> / <see cref="IKeyStore.RemoveKey"/>. It is not
/// <see cref="IDisposable"/>: the in-memory index holds only the PEM strings (no live key material), so
/// there is nothing to dispose — the keys are reconstructed on demand.
/// </remarks>
public sealed class FileBackedKeyStore : IKeyStore, IDisposable
{
    private const string KeysSection = "keys";

    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed key store over <paramref name="path"/> (creating the file on the
    /// first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedKeyStore(string path)
        : this(new FilePersistence(path, KeysToDocument, KeysFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedKeyStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public bool TryGetKey(Iri keyId, out ISigningKey? key)
    {
        key = null;
        var snapshot = _file.Snapshot(KeyIndex);
        if (!snapshot.TryGetValue(keyId, out var entry) || entry is null)
        {
            return false;
        }

        key = Reconstruct(entry);
        return key is not null;
    }

    /// <inheritdoc/>
    public void PutKey(ISigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _file.WithState(s =>
        {
            var index = KeyIndex(s);
            index[key.KeyId] = new StoredKey(
                key.Algorithm,
                key.KeyId,
                key.ExportPrivateKeyPem());
            return 0;
        }, persist: true);
    }

    /// <inheritdoc/>
    public bool RemoveKey(Iri keyId)
    {
        var snapshot = _file.Snapshot(KeyIndex);
        if (!snapshot.ContainsKey(keyId))
        {
            return false;
        }

        _file.WithState(s =>
        {
            var index = KeyIndex(s);
            return index.TryRemove(keyId, out _);
        }, persist: true);
        return true;
    }

    /// <summary>
    /// The key index for the current state (key IRI → stored key), created on demand.
    /// </summary>
    private static ConcurrentDictionary<Iri, StoredKey> KeyIndex(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<Iri, StoredKey>)(state.TryGetValue(KeysSection, out var k) ? k! : state[KeysSection] = new ConcurrentDictionary<Iri, StoredKey>());

    /// <summary>
    /// Reconstructs a live <see cref="ISigningKey"/> from its stored PEM. Returns null when the stored
    /// key cannot be reconstructed (e.g. a corrupt PEM) rather than throwing, so a single bad entry does
    /// not take down signature verification for the whole instance.
    /// </summary>
    private static ISigningKey? Reconstruct(StoredKey entry)
    {
        try
        {
            return entry.Algorithm switch
            {
                KeyAlgorithm.Ed25519 => Ed25519Key.FromPem(entry.PrivateKeyPem, entry.KeyId),
                _ => KeyPair.FromPem(entry.PrivateKeyPem, entry.Algorithm, entry.KeyId),
            };
        }
        catch (Exception)
        {
            // A corrupt or unreadable PEM is treated as "key not present": the caller falls back to the
            // same behavior as a missing key rather than crashing the request.
            return null;
        }
    }

    /// <summary>
    /// Serializes the key index to a JSON document (an array of <see cref="StoredKey"/>).
    /// </summary>
    private static JsonDocument KeysToDocument(ConcurrentDictionary<string, object> state)
    {
        var index = state.TryGetValue(KeysSection, out var k)
            ? (ConcurrentDictionary<Iri, StoredKey>)k!
            : new ConcurrentDictionary<Iri, StoredKey>();
        return JsonSerializer.SerializeToDocument(index.Values.ToList(), FilePersistence.JsonOptions);
    }

    /// <summary>
    /// Populates the key index from the file's root element (an array of stored keys).
    /// </summary>
    private static void KeysFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var index = new ConcurrentDictionary<Iri, StoredKey>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var entry = item.Deserialize<StoredKey>(FilePersistence.JsonOptions);
                if (entry is not null)
                {
                    index[entry.KeyId] = entry;
                }
            }
        }

        state[KeysSection] = index;
    }

    /// <summary>
    /// A stored signing key: its algorithm, its key IRI, and its PKCS#8 private-key PEM. The PEM is the
    /// lossless form for all three supported algorithms, so it round-trips the exact key material.
    /// </summary>
    /// <param name="Algorithm">The algorithm the key was generated with.</param>
    /// <param name="KeyId">The IRI that identifies the key.</param>
    /// <param name="PrivateKeyPem">The PKCS#8 private-key PEM.</param>
    public sealed record StoredKey(KeyAlgorithm Algorithm, Iri KeyId, string PrivateKeyPem);

    /// <summary>
    /// Releases the store's file lock. The file on disk is left in place (the data is durable);
    /// this only frees the <see cref="FilePersistence"/> lock that serializes reads/writes.
    /// </summary>
    public void Dispose() => _file.Dispose();
}
