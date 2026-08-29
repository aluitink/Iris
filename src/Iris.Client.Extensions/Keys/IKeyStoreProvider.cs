using Iris.Core;

namespace Iris.Client.Extensions.Keys;

/// <summary>
/// Provides the <see cref="IKeyStore"/> the <see cref="IrisClientFactory"/> uses to resolve
/// signing identities. The <see cref="IrisSession"/> owns the underlying in-memory store (it
/// puts the authenticated actor's key there for the session lifetime); this seam lets the
/// factory and the session share one store without a hard reference between them.
/// </summary>
/// <remarks>
/// In a Blazor (WASM) host the same <see cref="IKeyStoreProvider"/> (backed by the
/// session's store) is registered as a singleton, so every client built for the session signs
/// with the session's key.
/// </remarks>
public interface IKeyStoreProvider
{
    /// <summary>
    /// Gets the <see cref="IKeyStore"/> containing the session's keys.
    /// </summary>
    public IKeyStore KeyStore { get; }
}
