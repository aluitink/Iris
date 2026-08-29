namespace Iris.Core.Identity;

/// <summary>
/// Stores and resolves <see cref="ISigningKey"/>s (RSA / EC via <see cref="KeyPair"/>, Ed25519 via
/// <see cref="Ed25519Key"/>) by their key IRI.
/// </summary>
/// <remarks>
/// Implementations decide the persistence strategy: an in-memory store is ephemeral (keys
/// vanish on restart), a file/DB-backed store persists them. Keys are generated on first
/// run when none is configured (see <see cref="KeyPairGenerator"/> or <see cref="Ed25519Key.Generate(Iri)"/>).
/// </remarks>
public interface IKeyStore
{
    /// <summary>
    /// Attempts to retrieve the key for the given key IRI.
    /// </summary>
    /// <param name="keyId">The IRI identifying the key.</param>
    /// <param name="key">When successful, the key; otherwise null.</param>
    /// <returns><see langword="true"/> if the key was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetKey(Iri keyId, out ISigningKey? key);

    /// <summary>
    /// Stores (or replaces) a key under its key IRI.
    /// </summary>
    /// <param name="key">The key to store. The store takes ownership and disposes the previous one if present (when it is disposable).</param>
    /// <exception cref="ArgumentNullException">When <paramref name="key"/> is null.</exception>
    public void PutKey(ISigningKey key);

    /// <summary>
    /// Removes the key for the given key IRI, disposing it when it is disposable.
    /// </summary>
    /// <param name="keyId">The IRI identifying the key.</param>
    /// <returns><see langword="true"/> if a key was removed; otherwise <see langword="false"/>.</returns>
    public bool RemoveKey(Iri keyId);
}
