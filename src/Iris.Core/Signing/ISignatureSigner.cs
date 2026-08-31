namespace Iris.Core.Signing;

/// <summary>
/// Signs an outgoing ActivityPub request and produces the <c>Signature</c> header value.
/// </summary>
/// <remarks>
/// Implementations are pure (no I/O, no HTTP) and live in <c>Iris.Core</c> so the same code
/// signs client requests and server-to-server deliveries.
/// </remarks>
public interface ISignatureSigner
{
    /// <summary>
    /// Signs the given request for the given profile.
    /// </summary>
    /// <param name="metadata">The request fields to sign.</param>
    /// <param name="identity">The identity (actor + key id) to sign as. The key is resolved from the store by <see cref="IIdentity.KeyId"/>.</param>
    /// <param name="profile">Which headers to cover.</param>
    /// <returns>The <c>Signature</c> header value to set on the request.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="metadata"/> or <paramref name="identity"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">When the identity's key is not present in the store.</exception>
    public string Sign(HttpRequestMetadata metadata, IIdentity identity, SigningProfile profile);

    /// <summary>
    /// Signs the given request for the given profile, asynchronously.
    /// </summary>
    /// <remarks>
    /// The default implementation defers to the synchronous <c>Sign</c> method, which is correct for
    /// the BCL/BouncyCastle-backed signer on a server. The client's <c>SigningHandler</c> (an async
    /// pipeline stage) calls this so a WebCrypto-backed key in a Blazor WebAssembly host — whose
    /// <c>ISigningKey.SignAsync</c> awaits the browser's <c>crypto.subtle</c> — signs without blocking.
    /// </remarks>
    /// <param name="metadata">The request fields to sign.</param>
    /// <param name="identity">The identity (actor + key id) to sign as.</param>
    /// <param name="profile">Which headers to cover.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The <c>Signature</c> header value to set on the request.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="metadata"/> or <paramref name="identity"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">When the identity's key is not present in the store.</exception>
    Task<string> SignAsync(
        HttpRequestMetadata metadata,
        IIdentity identity,
        SigningProfile profile,
        CancellationToken ct = default)
        => Task.FromResult(Sign(metadata, identity, profile));
}
