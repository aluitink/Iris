namespace Iris.Core;

/// <summary>
/// An in-memory <see cref="IKeyStore"/>. Keys are ephemeral: they live only for the
/// lifetime of this instance and are lost on process exit.
/// </summary>
/// <remarks>
/// Suitable for tests and for servers that regenerate keys on first run. A persistent
/// deployment should supply a file- or database-backed <see cref="IKeyStore"/> instead.
/// </remarks>
public sealed class InMemoryKeyStore : IKeyStore, IDisposable
{
    private readonly Dictionary<Iri, ISigningKey> _keys = new();

    /// <inheritdoc/>
    public bool TryGetKey(Iri keyId, out ISigningKey? key)
        => _keys.TryGetValue(keyId, out key);

    /// <inheritdoc/>
    public void PutKey(ISigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_keys.TryGetValue(key.KeyId, out var existing))
        {
            DisposeIfDisposable(existing);
        }
        _keys[key.KeyId] = key;
    }

    /// <inheritdoc/>
    public bool RemoveKey(Iri keyId)
    {
        if (_keys.TryGetValue(keyId, out var existing))
        {
            DisposeIfDisposable(existing);
            _keys.Remove(keyId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Disposes all stored keys (when they are <see cref="IDisposable"/>) and clears the store.
    /// </summary>
    public void Dispose()
    {
        foreach (var key in _keys.Values)
        {
            DisposeIfDisposable(key);
        }

        _keys.Clear();
    }

    private static void DisposeIfDisposable(ISigningKey key)
    {
        if (key is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
