using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Extensions;

/// <summary>
/// Manages the client's identity for the lifetime of the session: it authenticates an actor
/// against the Iris home server (fetching the owner-only actor document + private key), holds
/// the key in memory for the session lifetime (Resolved Decision #5), and registers it for
/// signing.
/// </summary>
/// <remarks>
/// The key is **in-memory only** — it is lost on page refresh (the user re-authenticates),
/// which is acceptable for Basic auth and is the v1 model. A future phase can swap in OAuth2
/// bearer tokens without changing this surface (the <see cref="IClientAuthenticator"/> is the
/// seam).
/// </remarks>
public sealed class IrisSession : IDisposable
{
    private readonly IClientAuthenticator _authenticator;
    private readonly IKeyStore _keyStore;
    private readonly IKeyProvider _keyProvider;

    private Iri? _currentActor;
    private Iri? _currentKey;

    /// <summary>
    /// Creates a new <see cref="IrisSession"/>.
    /// </summary>
    /// <param name="authenticator">
    /// The authenticator used to fetch the actor document + private key (Basic auth). Must not be null.
    /// </param>
    /// <param name="keyStore">
    /// The session's in-memory key store, which holds the authenticated key for the session lifetime.
    /// Must not be null.
    /// </param>
    /// <param name="keyProvider">
    /// The key provider that maps an actor IRI to its signing identity. Must not be null.
    /// </param>
    public IrisSession(IClientAuthenticator authenticator, IKeyStore keyStore, IKeyProvider keyProvider)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
    }

    /// <summary>
    /// Gets the IRI of the currently authenticated actor, or <see langword="null"/> if the
    /// session is not authenticated.
    /// </summary>
    public Iri? CurrentActorIri => _currentActor;

    /// <summary>
    /// Gets the authenticated actor's document (including the owner-only extensions), or
    /// <see langword="null"/> if the session is not authenticated.
    /// </summary>
    public Actor? CurrentActor { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the session is authenticated.
    /// </summary>
    public bool IsAuthenticated => _currentActor is not null;

    /// <summary>
    /// Gets the <see cref="IKeyStore"/> that holds the session's key. Exposed so the
    /// <see cref="IrisClientFactory"/> (or a host) can resolve the signing identity.
    /// </summary>
    public IKeyStore KeyStore => _keyStore;

    /// <summary>
    /// Authenticates the given actor against the home server, loads its private key, stores it in
    /// the session key store, and registers it for signing. Subsequent calls switch the active
    /// identity (the previous key is removed from the store).
    /// </summary>
    /// <param name="actorId">The IRI of the actor to authenticate.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// The authenticated actor, or <see langword="null"/> if authentication failed (no
    /// credentials, the server rejected the request, the document carried no private key, or the
    /// key could not be parsed).
    /// </returns>
    public async Task<Actor?> LoginAsync(Iri actorId, CancellationToken ct = default)
    {
        var result = await _authenticator.AuthenticateAsync(actorId, ct).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        // The authenticator returns an Actor (the owner-only document) + a loaded KeyPair. The
        // key is owned by the store once put, so it must not be disposed here.
        var actor = result.Actor;
        var key = result.Key;

        if (_currentKey is { } previous && previous != key.KeyId)
        {
            _keyStore.RemoveKey(previous);
        }

        _keyStore.PutKey(key);
        _keyProvider.RegisterKey(actorId, key.KeyId);

        _currentActor = actorId;
        _currentKey = key.KeyId;
        CurrentActor = actor;

        return actor;
    }

    /// <summary>
    /// Switches the session to a different already-authenticated identity by re-authenticating
    /// the given actor. Equivalent to <see cref="LoginAsync"/>; provided as a discoverable name
    /// for the "identity selection" use case.
    /// </summary>
    /// <param name="actorId">The IRI of the actor to switch to.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The authenticated actor, or <see langword="null"/> if authentication failed.</returns>
    public Task<Actor?> SwitchIdentityAsync(Iri actorId, CancellationToken ct = default)
        => LoginAsync(actorId, ct);

    /// <summary>
    /// Clears the session: removes the current key from the store and forgets the identity. The
    /// next <see cref="LoginAsync"/> re-authenticates from scratch.
    /// </summary>
    public void Logout()
    {
        if (_currentKey is { } keyId)
        {
            _keyStore.RemoveKey(keyId);
        }

        _currentActor = null;
        _currentKey = null;
        CurrentActor = null;
    }

    /// <summary>
    /// Disposes the session, removing the current key from the store.
    /// </summary>
    public void Dispose()
    {
        Logout();
    }
}
