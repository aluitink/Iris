using System.Text.Json;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;

namespace Iris.Web.Accounts;

/// <summary>
/// Provisions a local ActivityPub actor (a <see cref="Person"/>) for a new account. This is the same
/// "create a local actor" mechanism <c>SampleServer</c>'s seed logic uses (generate a
/// <see cref="KeyPair"/>, write it to the <see cref="IKeyStore"/>, build the <see cref="Person"/> with
/// its <c>publicKeyPem</c>, store it in the actor store, and register its key with the
/// <see cref="IKeyProvider"/> so the server can sign as it) — but generalized into a DI service so
/// registration and the admin bootstrapper call one shared code path.
/// </summary>
/// <remarks>
/// The actor's IRI is derived the exact way the server derives it (<c>BaseUri.Value.TrimEnd('/')</c>
/// + <c>/ap/v1/u/{handle}</c>) so the provisioned actor's IRI is identical to the one the WebFinger /
/// actor-document / inbox handlers resolve. Idempotent by handle (re-provisioning replaces the actor
/// and re-mints the key).
/// </remarks>
public sealed class ActorProvisioner
{
    private readonly IPersistenceProvider _persistence;
    private readonly IKeyStore _keyStore;
    private readonly IKeyProvider _keyProvider;
    private readonly Iri _baseUri;

    /// <summary>
    /// Initializes the provisioner.
    /// </summary>
    /// <param name="persistence">The persistence provider (for the actor store + key store).</param>
    /// <param name="keyStore">The key store the signing key is written to.</param>
    /// <param name="keyProvider">The server's key provider (so the proxy / delivery worker can sign as the actor).</param>
    /// <param name="baseUri">The advertised public base URI (used to derive the actor IRI).</param>
    public ActorProvisioner(
        IPersistenceProvider persistence,
        IKeyStore keyStore,
        IKeyProvider keyProvider,
        Iri baseUri)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(keyStore);
        ArgumentNullException.ThrowIfNull(keyProvider);
        _persistence = persistence;
        _keyStore = keyStore;
        _keyProvider = keyProvider;
        _baseUri = baseUri;
    }

    /// <summary>
    /// The IRI a local actor with the given handle would have (<c>{base}/ap/v1/u/{handle}</c>).
    /// </summary>
    /// <param name="handle">The actor's handle.</param>
    public Iri ActorIriFor(string handle)
        => new($"{_baseUri.Value.TrimEnd('/')}/ap/v1/u/{handle}");

    /// <summary>
    /// Provisions (or re-provisions) a local actor for the given handle and returns its IRI. Generates
    /// a fresh RSA key pair, writes it to the key store, stores the <see cref="Person"/>, and registers
    /// the key with the <see cref="IKeyProvider"/>.
    /// </summary>
    /// <param name="handle">The actor's handle (the account's username).</param>
    /// <param name="displayName">The actor's display name (defaults to the handle when null/blank).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The provisioned actor's IRI.</returns>
    public async Task<Iri> ProvisionAsync(string handle, string? displayName = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        var actorIri = ActorIriFor(handle);
        var keyIri = new Iri($"{actorIri}#key-1");

        // A fresh key pair per account. PutKey keys by KeyId and disposes the previous key if any.
        var key = KeyPairGenerator.GenerateRsa(keyIri);
        _keyStore.PutKey(key);

        var name = string.IsNullOrWhiteSpace(displayName) ? handle : displayName.Trim();
        var actor = new Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [name],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData[ActivityPubExtensionNames.PublicKey] = JsonSerializer.SerializeToElement(new
        {
            id = keyIri.Value,
            owner = actorIri.Value,
            publicKeyPem = key.ExportPublicKeyPem(),
        });
        await _persistence.Actors.PutActorAsync(actor, ct).ConfigureAwait(false);

        // Register the key so the proxy endpoint and the outbound DeliveryWorker can sign as it.
        _keyProvider.RegisterKey(actorIri, keyIri);

        return actorIri;
    }
}
