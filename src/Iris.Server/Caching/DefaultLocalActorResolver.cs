using Iris.Core;

namespace Iris.Server.Caching;

/// <summary>
/// The default <see cref="ILocalActorResolver"/>: an actor is local when it is present in the
/// instance's actor store AND its IRI is hosted on the instance base (when configured).
/// </summary>
/// <remarks>
/// The actor store holds both local actors (provisioned by the registration flow) and remote actors
/// (persisted by <see cref="Security.RemoteActorPersister"/> for the directory's "All known" scope).
/// When an instance base is configured, the resolver additionally checks that the actor IRI starts
/// with the instance base prefix, so remote actors cached in the store are correctly identified as
/// non-local. When no instance base is configured (e.g. in tests), the resolver falls back to the
/// store-membership-only check (the legacy behaviour).
/// </remarks>
public sealed class DefaultLocalActorResolver : ILocalActorResolver
{
    private readonly IPersistenceProvider _persistence;
    private readonly Iri? _instanceBase;

    /// <summary>
    /// Initializes a new <see cref="DefaultLocalActorResolver"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (its actor store is consulted).</param>
    /// <param name="instanceBase">
    /// The instance's base IRI (e.g. <c>https://iris.example</c>). When set, an actor is only
    /// considered local if its IRI starts with this prefix. When null, any actor in the store is
    /// considered local (legacy behaviour, used in tests that do not configure a base).
    /// </param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> is null.</exception>
    public DefaultLocalActorResolver(IPersistenceProvider persistence, Iri? instanceBase = null)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _persistence = persistence;
        _instanceBase = instanceBase;
    }

    /// <inheritdoc/>
    public async Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        // When an instance base is configured, the IRI must be hosted on this instance. This is the
        // authoritative local/remote discriminator: the actor store contains both local and remote
        // actors (RemoteActorPersister caches remote documents for the directory), so store membership
        // alone is insufficient.
        if (_instanceBase is { } instanceBase)
        {
            var prefix = instanceBase.Value.TrimEnd('/');
            if (!actorIri.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return await _persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false);
    }
}
