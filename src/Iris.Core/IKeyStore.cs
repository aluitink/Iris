namespace Iris.Core;

/// <summary>
/// Stores and resolves <see cref="KeyPair"/>s by their key IRI.
/// </summary>
/// <remarks>
/// Implementations decide the persistence strategy: an in-memory store is ephemeral (keys
/// vanish on restart), a file/DB-backed store persists them. Keys are generated on first
/// run when none is configured (see <see cref="KeyPairGenerator"/>).
/// </remarks>
public interface IKeyStore
{
    /// <summary>
    /// Attempts to retrieve the key pair for the given key IRI.
    /// </summary>
    /// <param name="keyId">The IRI identifying the key.</param>
    /// <param name="keyPair">When successful, the key pair; otherwise null.</param>
    /// <returns><see langword="true"/> if the key was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetKey(Iri keyId, out KeyPair? keyPair);

    /// <summary>
    /// Stores (or replaces) a key pair under its key IRI.
    /// </summary>
    /// <param name="key">The key pair to store. The store takes ownership and disposes the previous one if present.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="key"/> is null.</exception>
    public void PutKey(KeyPair key);

    /// <summary>
    /// Removes the key pair for the given key IRI, disposing it.
    /// </summary>
    /// <param name="keyId">The IRI identifying the key.</param>
    /// <returns><see langword="true"/> if a key was removed; otherwise <see langword="false"/>.</returns>
    public bool RemoveKey(Iri keyId);
}
