using Iris.Core;
using Iris.Core.Identity;
using Microsoft.JSInterop;

namespace Iris.WebCrypto;

/// <summary>
/// Loads an <see cref="ISigningKey"/> from PEM key material. The Iris client authenticators
/// (<c>BasicAuthClientAuthenticator</c> / <c>OAuth2ClientAuthenticator</c> in
/// <c>Iris.Client</c>) accept an implementation of this delegate shape so a host can supply a
/// custom key loader — e.g. <see cref="WebCryptoSigningKeyFactory"/> for a Blazor WebAssembly host,
/// where the .NET BCL cannot load an RSA private key.
/// </summary>
/// <remarks>
/// This is the public, named form of the key-factory delegate
/// <c>Func&lt;string, KeyAlgorithm, Iri, CancellationToken, Task&lt;ISigningKey&gt;&gt;</c> the
/// authenticators take. A <see cref="WebCryptoSigningKeyFactory"/> instance is directly assignable to
/// that delegate (method-group conversion), so it can be passed straight to an authenticator's
/// key-factory constructor.
/// </remarks>
public interface ISigningKeyFactory
{
    /// <summary>
    /// Loads a signing key from PEM.
    /// </summary>
    /// <param name="pem">The PEM key material (PKCS#8 for private-key signing).</param>
    /// <param name="algorithm">The key algorithm the key must support.</param>
    /// <param name="keyIri">The IRI identifying the key (the actor's <c>publicKey.id</c>).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The loaded signing key.</returns>
    Task<ISigningKey> CreateAsync(
        string pem, KeyAlgorithm algorithm, Iri keyIri, CancellationToken ct = default);
}

/// <summary>
/// An <see cref="ISigningKeyFactory"/> that produces <see cref="WebCryptoSigningKey"/> instances from
/// the browser's WebCrypto. Pass this to an Iris client authenticator (e.g.
/// <c>BasicAuthClientAuthenticator</c> / <c>OAuth2ClientAuthenticator</c> via their
/// key-factory constructor) in a Blazor WebAssembly or other JS-interop host, where the .NET BCL
/// cannot load an RSA private key.
/// </summary>
/// <remarks>
/// The factory captures the host's <see cref="IJSRuntime"/> at construction. Each call loads a fresh
/// key from the PEM (the authenticator supplies the actor document's owner-only
/// <c>privateKey</c>), so a re-login with a rotated key loads the new material. The first
/// <see cref="CreateAsync(string, KeyAlgorithm, Iri, CancellationToken)"/> call also auto-injects the
/// <c>WebCrypto.js</c> bridge into the page (idempotent).
/// </remarks>
public sealed class WebCryptoSigningKeyFactory : ISigningKeyFactory
{
    private readonly IJSRuntime _js;

    /// <summary>
    /// Initializes a new <see cref="WebCryptoSigningKeyFactory"/>.
    /// </summary>
    /// <param name="js">The JS runtime (Blazor WebAssembly). Not disposed by the factory.</param>
    public WebCryptoSigningKeyFactory(IJSRuntime js)
    {
        _js = js ?? throw new ArgumentNullException(nameof(js));
    }

    /// <inheritdoc/>
    public async Task<ISigningKey> CreateAsync(
        string pem, KeyAlgorithm algorithm, Iri keyIri, CancellationToken ct = default)
        => await WebCryptoSigningKey.CreateAsync(_js, pem, keyIri, ct).ConfigureAwait(false);
}
