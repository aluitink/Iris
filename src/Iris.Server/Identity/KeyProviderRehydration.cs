using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Stores;

namespace Iris.Server.Identity;

/// <summary>
/// Rebuilds the in-memory actor→key-IRI bindings in an <see cref="IKeyProvider"/> from the durable
/// actor documents (Phase 84.4, key-rotation durability).
/// </summary>
/// <remarks>
/// The <see cref="IKeyProvider"/> (e.g. <c>InMemoryKeyProvider</c>) holds the actor→key-IRI map
/// <em>in-process only</em>: a host restart wipes it. The keys themselves are durable (in the
/// <see cref="IKeyStore"/>), and — since 84.2/84.3 — the actor document's <c>publicKey</c> extension
/// carries the <em>current</em> key IRI (<c>publicKey.id</c>, re-stamped on every rotation). This helper
/// is the single restart-restore path: it re-derives each local actor's key IRI from the persisted
/// actor document's <c>publicKey.id</c> (via <see cref="IriExtensions.GetPublicKeyIri"/>, the boundary
/// point 84.2 established) and re-registers it in the <see cref="IKeyProvider"/>.
/// </remarks>
/// <remarks>
/// <strong>Why this matters:</strong> a host that re-registers keys from a hard-coded <c>#key-1</c>
/// convention (the pre-84.4 behavior) re-binds a rotated actor to its <em>old</em> key after a restart
/// (or, once the old key is retired, fails to resolve any key at all). Re-deriving from
/// <c>publicKey.id</c> means a rotation to <c>#key-2</c> survives a restart — the actor keeps signing
/// with <c>#key-2</c>.
/// </remarks>
public static class KeyProviderRehydration
{
    /// <summary>
    /// The key-IRI fragment a legacy actor (no <c>publicKey</c> extension) is assumed to use.
    /// </summary>
    private const string LegacyKeyFragment = "key-1";

    /// <summary>
    /// Re-derives each local actor's signing-key binding from the durable actor documents and re-registers
    /// it in <paramref name="keyProvider"/>.
    /// </summary>
    /// <remarks>
    /// For every actor in <paramref name="actorStore"/>, the key IRI is resolved as
    /// <c>actor.GetPublicKeyIri() ?? {actorId}#key-1</c> (the <c>#key-1</c> fallback is only for a legacy
    /// actor that predates the <c>publicKey</c> extension). The binding is registered only when the key is
    /// actually present in <paramref name="keyStore"/> (a retired key is skipped, not registered).
    /// </remarks>
    /// <param name="keyProvider">The in-process key provider whose actor→key bindings are rebuilt.</param>
    /// <param name="actorStore">The durable actor store (the source of truth for <c>publicKey.id</c>).</param>
    /// <param name="keyStore">The durable key store (guards that the resolved key is present).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the number of actor→key bindings registered.</returns>
    public static async Task<int> RehydrateFromActorsAsync(
        IKeyProvider keyProvider,
        IActorStore actorStore,
        IKeyStore keyStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(actorStore);
        ArgumentNullException.ThrowIfNull(keyStore);

        var actors = await actorStore.ListActorsAsync(ct).ConfigureAwait(false);
        var registered = 0;
        foreach (var actor in actors)
        {
            if (string.IsNullOrEmpty(actor.Id))
            {
                continue;
            }

            var actorIri = Iri.TryParse(actor.Id, out var parsed) ? parsed : (Iri?)null;
            if (actorIri is null)
            {
                continue;
            }

            // The current key IRI is the actor document's publicKey.id (re-stamped on every rotation);
            // fall back to the #key-1 convention only for a legacy actor with no publicKey extension.
            var keyIri = actor.GetPublicKeyIri() ?? new Iri($"{actorIri.Value}#{LegacyKeyFragment}");
            if (keyStore.TryGetKey(keyIri, out _))
            {
                keyProvider.RegisterKey(actorIri.Value, keyIri);
                registered++;
            }
        }

        return registered;
    }
}
