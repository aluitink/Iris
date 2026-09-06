using Iris.Client.Auth;
using Iris.Core;
using Microsoft.Extensions.Options;

namespace Iris.Server.Observability;

/// <summary>
/// The default <see cref="IReadinessGate"/>: the instance is ready once the instance actor's signing
/// key has been registered and is resolvable.
/// </summary>
/// <remarks>
/// A freshly-started instance is not ready until its key material is loaded (a file-backed key store
/// reads on startup; the in-memory default is registered by the host before traffic is routed). Until
/// the instance actor's key is registered with the <see cref="IKeyProvider"/> and present in the
/// <see cref="IKeyStore"/>, the <c>GET /ap/v1/ready</c> probe reports not-ready (<c>503</c>), so a load
/// balancer does not route traffic to an instance that cannot sign outbound delivery or authenticate
/// its own actor. The probe is a fast, dependency-free read (it resolves the identity and key, it does
/// not make a network call), so it is safe for a high-frequency orchestrator probe.
///
/// A host that loads keys asynchronously (or from a secret manager) may bind its own
/// <see cref="IReadinessGate"/> (via <c>ExtraServices</c> or an override) that additionally awaits the
/// key-load; the default covers the common register-then-serve model.
/// </remarks>
public sealed class DefaultReadinessGate : IReadinessGate
{
    private readonly IKeyProvider _keyProvider;
    private readonly IKeyStore _keyStore;
    private readonly IOptions<ActivityPubServerOptions> _options;

    /// <summary>
    /// Initializes a new <see cref="DefaultReadinessGate"/>.
    /// </summary>
    /// <param name="keyProvider">The provider that resolves the instance actor's signing identity.</param>
    /// <param name="keyStore">The store that holds the key material.</param>
    /// <param name="options">The instance's options (provides <see cref="ActivityPubServerOptions.InstanceActorId"/>).</param>
    public DefaultReadinessGate(
        IKeyProvider keyProvider,
        IKeyStore keyStore,
        IOptions<ActivityPubServerOptions> options)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        var actorIri = _options.Value.InstanceActorId;

        // No instance actor configured: the instance cannot sign or serve, so it is never ready.
        if (actorIri is not { } instanceActor)
        {
            return Task.FromResult(false);
        }

        // Ready only when the actor's signing identity is registered AND its key is present in the store.
        var ready = _keyProvider.TryGetIdentity(instanceActor, out var identity)
            && identity is not null
            && _keyStore.TryGetKey(identity.KeyId, out _);

        return Task.FromResult(ready);
    }
}
