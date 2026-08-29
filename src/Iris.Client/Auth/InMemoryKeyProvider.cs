using Iris.Core;

namespace Iris.Client.Auth;

/// <summary>
/// An <see cref="IKeyProvider"/> backed by an in-memory <see cref="IKeyStore"/>.
/// </summary>
/// <remarks>
/// v1 model: the session holds a map of actor IRI → key IRI. When a request is made for an
/// actor the session has a key for, the identity is resolved from the store. Keys are
/// **borrowed** (the store owns their lifetime); this provider never disposes them.
/// </remarks>
public sealed class InMemoryKeyProvider : IKeyProvider
{
    private readonly IKeyStore _keyStore;
    private readonly Dictionary<Iri, Iri> _actorToKey = new();

    /// <summary>
    /// Initializes a new <see cref="InMemoryKeyProvider"/>.
    /// </summary>
    /// <param name="keyStore">The store from which key pairs are resolved.</param>
    public InMemoryKeyProvider(IKeyStore keyStore)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    /// <summary>
    /// Registers (or replaces) the key IRI used to sign for the given actor.
    /// </summary>
    /// <param name="actorId">The actor IRI to sign as.</param>
    /// <param name="keyId">The key IRI (must already be present in the key store).</param>
    public void RegisterKey(Iri actorId, Iri keyId)
    {
        _actorToKey[actorId] = keyId;
    }

    /// <inheritdoc/>
    public bool TryGetIdentity(Iri actorId, out IIdentity? identity)
    {
        if (_actorToKey.TryGetValue(actorId, out var keyId)
            && _keyStore.TryGetKey(keyId, out var key)
            && key is not null)
        {
            identity = new SystemIdentity(actorId, key.KeyId);
            return true;
        }

        identity = null;
        return false;
    }
}
