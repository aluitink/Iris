using Iris.Core;

namespace Iris.Client.Extensions;

/// <summary>
/// An <see cref="IKeyStoreProvider"/> that exposes an explicitly supplied <see cref="IKeyStore"/>.
/// </summary>
/// <remarks>
/// Used by a host that manages its own <see cref="IKeyStore"/> (e.g. the session's in-memory
/// store) and wants the <see cref="IrisClientFactory"/> to read keys from it.
/// </remarks>
public sealed class SessionKeyStoreProvider : IKeyStoreProvider
{
    /// <summary>
    /// Creates a new <see cref="SessionKeyStoreProvider"/> over the given key store.
    /// </summary>
    /// <param name="keyStore">The key store to expose. Must not be null.</param>
    public SessionKeyStoreProvider(IKeyStore keyStore)
    {
        KeyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    /// <inheritdoc/>
    public IKeyStore KeyStore { get; }
}
