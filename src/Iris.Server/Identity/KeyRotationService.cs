using System.Text.Json;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Stores;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Identity;

/// <summary>
/// Rotates the local instance's signing key for a local actor (Phase 84.2, local key-rotation
/// lifecycle — the follow-up 82.3 documented).
/// </summary>
/// <remarks>
/// A rotation mints a <em>new</em> RSA key at the next free fragment (<c>#key-2</c>, <c>#key-3</c>, … —
/// the first <c>#key-N</c> not already in the <see cref="IKeyStore"/> for that actor), stores it,
/// re-registers the actor→new-key binding in the <see cref="IKeyProvider"/> (so the signer — which is
/// already key-source-agnostic, signing with whatever <see cref="IIdentity.KeyId"/> it is given — signs
/// with the new key going forward), and re-stamps the actor document's <c>publicKey</c> extension (the
/// new <c>id</c> + new <c>publicKeyPem</c> + a <c>replaces</c> pointer to the old key IRI, which the
/// read-side <c>RemoteInboundKeyResolver</c> already honors to evict the stale key).
/// </remarks>
/// <remarks>
/// <strong>Overlap window.</strong> The <em>old</em> key is left in the <see cref="IKeyStore"/> after a
/// rotation (the key stores are fragment-aware — 82.3 — so <c>#key-1</c> and <c>#key-2</c> co-exist as
/// distinct entries). This means a signature made with the old key <em>before</em> the rotation still
/// verifies during the overlap window. An operator calls <see cref="RetireKey"/> to remove the old key
/// once the rotation is confirmed (peers that already fetched/cached the old public key can still verify
/// signatures made before retirement; removing the <em>private</em> key from the store only stops
/// <em>new</em> signatures with it).
/// </remarks>
public sealed class KeyRotationService
{
    private const string DefaultKeyFragment = "key";

    private readonly IPersistenceProvider _persistence;
    private readonly IKeyStore _keyStore;
    private readonly IKeyProvider _keyProvider;
    private readonly ILogger<KeyRotationService> _logger;

    /// <summary>
    /// Initializes the rotation service.
    /// </summary>
    /// <param name="persistence">The persistence provider (for the actor store + key store).</param>
    /// <param name="keyStore">The key store the new signing key is written to.</param>
    /// <param name="keyProvider">The server's key provider (so the proxy / delivery worker sign with the
    /// new key after rotation).</param>
    /// <param name="logger">A logger.</param>
    public KeyRotationService(
        IPersistenceProvider persistence,
        IKeyStore keyStore,
        IKeyProvider keyProvider,
        ILogger<KeyRotationService> logger)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(keyStore);
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _persistence = persistence;
        _keyStore = keyStore;
        _keyProvider = keyProvider;
        _logger = logger;
    }

    /// <summary>
    /// Rotates the local actor's signing key: mints a new RSA key at the next free fragment, stores it,
    /// re-registers the actor→new-key binding, and re-stamps the actor document's <c>publicKey</c>
    /// extension (with a <c>replaces</c> pointer to the old key IRI). The old key is left in the store
    /// (the overlap window).
    /// </summary>
    /// <param name="actorIri">The IRI of the local actor whose key is rotated. Must be a stored actor.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The new key IRI (e.g. <c>actorIri#key-2</c>).</returns>
    /// <exception cref="KeyNotFoundException">When no actor is stored for <paramref name="actorIri"/>.</exception>
    public async Task<Iri> RotateAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!await _persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct).ConfigureAwait(false)
            || actor is null)
        {
            throw new KeyNotFoundException($"No actor is stored for '{actorIri}'.");
        }

        // The old key IRI is the actor's current publicKey.id (the single boundary point for reading it,
        // GetPublicKeyIri — F-25), falling back to the #key-1 convention when the extension is absent.
        var oldKeyIri = actor.GetPublicKeyIri() ?? new Iri($"{actorIri}#key-1");

        // Mint the new key at the next free fragment (#key-2, #key-3, …): the first #key-N not already in
        // the store. Rotation is rare, so a bounded linear probe is sufficient (a key IRI is only ever a
        // #key-N fragment for a local actor).
        var newKeyIri = NextFreeKeyIri(actorIri);
        var newKey = KeyPairGenerator.GenerateRsa(newKeyIri);
        _keyStore.PutKey(newKey);

        // Re-register the actor→new-key binding so the signer (key-source-agnostic) signs with the new key.
        _keyProvider.RegisterKey(actorIri, newKeyIri);

        // Re-stamp the actor document's publicKey extension (new id + new publicKeyPem + replaces).
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData[ActivityPubExtensionNames.PublicKey] = JsonSerializer.SerializeToElement(new
        {
            id = newKeyIri.Value,
            owner = actorIri.Value,
            publicKeyPem = newKey.ExportPublicKeyPem(),
            replaces = oldKeyIri.Value,
        });
        await _persistence.Actors.PutActorAsync(actor, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Rotated the signing key for {Actor} from {OldKey} to {NewKey}.",
            actorIri.Value, oldKeyIri.Value, newKeyIri.Value);

        return newKeyIri;
    }

    /// <summary>
    /// Retires a key: removes it from the <see cref="IKeyStore"/> once its rotation is confirmed. The
    /// key's public part was already advertised via the actor document, so a peer that fetched/cached it
    /// can still verify signatures made before retirement; removing the <em>private</em> key stops new
    /// signatures with it.
    /// </summary>
    /// <param name="keyIri">The IRI of the key to retire.</param>
    /// <returns><see langword="true"/> when a key was removed; <see langword="false"/> when none was
    /// present.</returns>
    public bool RetireKey(Iri keyIri)
    {
        var removed = _keyStore.RemoveKey(keyIri);
        if (removed)
        {
            _logger.LogInformation("Retired the signing key {Key}.", keyIri.Value);
        }

        return removed;
    }

    /// <summary>
    /// The next free key IRI for the actor: the first <c>actorIri#key-N</c> (N ≥ 2) not already in the
    /// <see cref="IKeyStore"/>. Probes upward from 2 (a local actor's key IRIs are only ever
    /// <c>#key-N</c> fragments; <c>#key-1</c> is the initial key).
    /// </summary>
    private Iri NextFreeKeyIri(Iri actorIri)
    {
        for (var n = 2; ; n++)
        {
            var candidate = new Iri($"{actorIri}#{DefaultKeyFragment}-{n}");
            if (!_keyStore.TryGetKey(candidate, out _))
            {
                return candidate;
            }
        }
    }
}
