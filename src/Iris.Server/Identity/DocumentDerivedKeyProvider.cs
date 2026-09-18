using System.Collections.Concurrent;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Stores;

namespace Iris.Server.Identity;

/// <summary>
/// An <see cref="IKeyProvider"/> whose actor→key bindings are derived from the <em>durable</em> actor
/// documents (Phase 84.6, shared-state scale-out — the convergence half).
/// </summary>
/// <remarks>
/// <para>
/// The default server <see cref="IKeyProvider"/> (<see cref="InMemoryKeyProvider"/>, in
/// <c>Iris.Client</c>) holds the actor→key-IRI map <em>in-process only</em>. That is fine for a single
/// instance (84.4's <see cref="KeyProviderRehydration"/> re-derives the map from the persisted documents
/// on a restart), but with <strong>two instances over the same persistence</strong> each holds a disjoint
/// copy: a rotation on instance A updates A's map, so A's signer signs with the new key, while B's map
/// still points at the old key — B keeps signing with a key that A has moved past (and, once the old key
/// is retired, with a key that no longer exists in the store). The key <em>material</em> is already shared
/// (both instances read the same <see cref="IKeyStore"/>), and the actor document's <c>publicKey</c>
/// extension is the single durable source of truth for <em>which</em> key is current (84.2 re-stamps it on
/// every rotation). The only divergent surface is the in-memory actor→key map.
/// </para>
/// <para>
/// <see cref="DocumentDerivedKeyProvider"/> closes that gap. Its <see cref="TryGetIdentity"/> (synchronous
/// — the <c>SigningHandler</c> resolves identities in a sync call) reads a thread-safe in-memory map, and
/// <see cref="RefreshFromActorsAsync"/> is the <strong>convergence</strong> primitive: it re-derives every
/// local actor's key IRI from the durable actor document's <c>publicKey.id</c> (via
/// <see cref="IriExtensions.GetPublicKeyIri"/>, the 84.2/84.4 boundary point, with a <c>#key-1</c> fallback
/// for a legacy actor) and re-registers it — guarded by <see cref="IKeyStore.TryGetKey"/> so a retired key
/// is not (re)bound. Two instances over the same store converge when each (re)runs
/// <see cref="RefreshFromActorsAsync"/>: after a rotation on A, B's next refresh re-points B's map at the
/// new key, and B's signer signs with it — no restart, no divergent copy.
/// </para>
/// <para>
/// This provider is a drop-in replacement for the <see cref="InMemoryKeyProvider"/>: it implements the same
/// <see cref="IKeyProvider"/> seam (so the <c>SigningHandler</c>, <see cref="KeyRotationService"/>, and the
/// startup restore path all work unchanged) and additionally owns the refresh. The <em>when</em> of the
/// refresh (a periodic hosted service, or an on-miss trigger) is a separate wiring concern; the provider
/// itself is the convergence primitive.
/// </para>
/// </remarks>
public sealed class DocumentDerivedKeyProvider : IKeyProvider
{
    /// <summary>The key-IRI fragment a legacy actor (no <c>publicKey</c> extension) is assumed to use.</summary>
    private const string LegacyKeyFragment = "key-1";

    private readonly IKeyStore _keyStore;
    private readonly ConcurrentDictionary<Iri, Iri> _actorToKey = new();

    /// <summary>
    /// Initializes a new <see cref="DocumentDerivedKeyProvider"/>.
    /// </summary>
    /// <param name="keyStore">
    /// The durable key store (the source of the key material + the guard that a resolved key is present).
    /// Both instances over the same persistence share this store.
    /// </param>
    public DocumentDerivedKeyProvider(IKeyStore keyStore)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    /// <summary>
    /// Attempts to resolve the signing identity for the given actor from the (refreshed) in-memory map.
    /// </summary>
    /// <remarks>
    /// Synchronous, as the <c>SigningHandler</c> requires. The map is current with the durable documents as
    /// of the last <see cref="RefreshFromActorsAsync"/>; a rotation performed on <em>another</em> instance
    /// is not visible until this instance refreshes (the convergence primitive). The resolved key is
    /// re-verified against the store (a key retired since the last refresh is not returned).
    /// </remarks>
    /// <param name="actorId">The actor IRI to sign as.</param>
    /// <param name="identity">When successful, the identity (actor + key id); otherwise null.</param>
    /// <returns><see langword="true"/> if a current identity was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetIdentity(Iri actorId, out IIdentity? identity)
    {
        if (_actorToKey.TryGetValue(actorId, out var keyIri)
            && _keyStore.TryGetKey(keyIri, out var key)
            && key is not null)
        {
            identity = new SystemIdentity(actorId, key.KeyId);
            return true;
        }

        identity = null;
        return false;
    }

    /// <summary>
    /// Registers (or replaces) the key IRI used to sign for the given actor (the same seam the
    /// <see cref="KeyRotationService"/> and the startup restore path use).
    /// </summary>
    /// <param name="actorId">The actor IRI to sign as.</param>
    /// <param name="keyId">The key IRI (must already be present in the key store).</param>
    public void RegisterKey(Iri actorId, Iri keyId)
    {
        _actorToKey[actorId] = keyId;
    }

    /// <summary>
    /// Re-derives every local actor's key IRI from the durable actor documents and updates this provider's
    /// in-memory map (the convergence primitive).
    /// </summary>
    /// <remarks>
    /// For every actor in <paramref name="actorStore"/>, the key IRI is resolved as
    /// <c>actor.GetPublicKeyIri() ?? {actorId}#key-1</c> (the <c>#key-1</c> fallback is only for a legacy
    /// actor that predates the <c>publicKey</c> extension). A binding is registered only when the key is
    /// actually present in <paramref name="keyStore"/> (a retired key is skipped, not registered). This is
    /// the same logic as <see cref="KeyProviderRehydration.RehydrateFromActorsAsync"/> (the restart-restore
    /// path, 84.4), but owned by the provider so it can be re-run to converge a running instance. An actor
    /// whose key no longer resolves (e.g. retired and not yet re-rotated) is dropped from the map.
    /// </remarks>
    /// <param name="actorStore">The durable actor store (the source of truth for <c>publicKey.id</c>).</param>
    /// <param name="keyStore">
    /// The durable key store (guards that the resolved key is present). May differ from the store this
    /// provider was constructed with only in test setups; production uses the same shared store.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the number of actor→key bindings (re)registered.</returns>
    public async Task<int> RefreshFromActorsAsync(IActorStore actorStore, IKeyStore keyStore, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actorStore);
        ArgumentNullException.ThrowIfNull(keyStore);

        var actors = await actorStore.ListActorsAsync(ct).ConfigureAwait(false);
        var next = new Dictionary<Iri, Iri>();
        foreach (var actor in actors)
        {
            if (string.IsNullOrEmpty(actor.Id))
            {
                continue;
            }

            if (Iri.TryParse(actor.Id, out var parsed) is not true)
            {
                continue;
            }

            var actorIri = parsed!;

            // The current key IRI is the actor document's publicKey.id (re-stamped on every rotation);
            // fall back to the #key-1 convention only for a legacy actor with no publicKey extension.
            var keyIri = actor.GetPublicKeyIri() ?? new Iri($"{actorIri}#{LegacyKeyFragment}");
            if (keyStore.TryGetKey(keyIri, out _))
            {
                next[actorIri] = keyIri;
            }
        }

        // Replace the map atomically (drop actors whose key no longer resolves, add newly-rotated ones).
        _actorToKey.Clear();
        foreach (var (actorIri, keyIri) in next)
        {
            _actorToKey[actorIri] = keyIri;
        }

        return next.Count;
    }
}
