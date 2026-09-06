using Iris.Core.Identity;

namespace Iris.Client.Auth;

/// <summary>
/// An <see cref="IKeyProvider"/> that delegates to a primary provider and, when that cannot resolve the
/// actor, falls back to resolving the actor's key directly from a durable <see cref="IKeyStore"/> by the
/// well-known local key-IRI convention (<c>{actor}#key-1</c>).
/// </summary>
/// <remarks>
/// The in-process <see cref="InMemoryKeyProvider"/> (the default server <c>IKeyProvider</c>) only knows
/// the actors that were registered with it — at startup (the seeded instance actor) or at request time
/// (a freshly provisioned local actor). A local actor whose key was created by a *different* code path
/// (e.g. admin-assisted provisioning that writes straight to the durable key store but not to the
/// in-memory provider) would otherwise be un-signable until a restart — the <c>SigningHandler</c> throws
/// <c>No signing identity registered for actor '…'</c>. This provider closes that gap: if the primary
/// lookup misses, it looks the key up in the (durable, e.g. EF-backed) store by the same
/// <c>{actor}#key-1</c> convention the seed and <c>ActorProvisioner</c> use. The store is the source of
/// truth for the key material; the in-memory map is only a fast path.
/// </remarks>
public sealed class DelegatingKeyProvider : IKeyProvider
{
    private const string DefaultKeyFragment = "key-1";

    private readonly IKeyProvider _primary;
    private readonly IKeyStore _keyStore;
    private readonly string _keyFragment;

    /// <summary>
    /// Initializes a new <see cref="DelegatingKeyProvider"/>.
    /// </summary>
    /// <param name="primary">The primary provider to consult first (the in-memory actor→key map).</param>
    /// <param name="keyStore">The (durable) key store used for the fallback key lookup.</param>
    /// <param name="keyFragment">
    /// The fragment appended to the actor IRI to form the local key IRI (default <c>key-1</c>). It is
    /// part of the key IRI (<c>{actor}#key-1</c>), not a URL path, so it is not subject to URL-fragment
    /// parsing concerns.
    /// </param>
    public DelegatingKeyProvider(IKeyProvider primary, IKeyStore keyStore, string keyFragment = DefaultKeyFragment)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _keyFragment = string.IsNullOrEmpty(keyFragment) ? DefaultKeyFragment : keyFragment;
    }

    /// <inheritdoc/>
    public bool TryGetIdentity(Iri actorId, out IIdentity? identity)
    {
        if (_primary.TryGetIdentity(actorId, out identity))
        {
            return identity is not null;
        }

        // Fallback: the actor's key lives in the durable store under the well-known local key IRI
        // ({actor}#{fragment}). This makes a locally-provisioned actor signable without an explicit
        // RegisterKey call (and without waiting for a restart to re-run the startup restore pass).
        var keyIri = new Iri($"{actorId}#{_keyFragment}");
        if (_keyStore.TryGetKey(keyIri, out var key) && key is not null)
        {
            identity = new SystemIdentity(actorId, keyIri);
            return true;
        }

        identity = null;
        return false;
    }

    /// <inheritdoc/>
    public void RegisterKey(Iri actorId, Iri keyId)
        => _primary.RegisterKey(actorId, keyId);
}
