namespace Iris.Core.Signing;

/// <summary>
/// Default <see cref="ISignatureSigner"/>. Resolves the key from an <see cref="IKeyStore"/> by
/// the identity's <see cref="IIdentity.KeyId"/>, builds the signature base for the profile,
/// signs it, and returns the <c>Signature</c> header value.
/// </summary>
/// <param name="keyStore">The store used to resolve keys.</param>
public sealed class HttpSignatureSigner(IKeyStore keyStore) : ISignatureSigner
{
    /// <inheritdoc/>
    public string Sign(HttpRequestMetadata metadata, IIdentity identity, SigningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(identity);

        if (!keyStore.TryGetKey(identity.KeyId, out var key) || key is null)
        {
            throw new KeyNotFoundException($"No key found for id '{identity.KeyId}'.");
        }

        // The key is borrowed from the store (the store owns its lifetime); do not dispose it here.
        var components = Signatures.HeadersForProfile(profile).Split(' ');
        var baseBytes = Signatures.BuildSignatureBase(metadata, components);
        var signature = Signatures.SignBase(key, baseBytes);

        return new SignatureHeader(
            identity.KeyId.Value,
            Signatures.AlgorithmLabel(key.Algorithm),
            Signatures.HeadersForProfile(profile),
            Convert.ToBase64String(signature))
            .Format();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Unlike the default interface implementation (which defers to the synchronous <c>Sign</c>
    /// method), this awaits the key's <c>SignAsync</c>, so a WebCrypto-backed key in a Blazor Web
    /// Assembly host signs through the browser's asynchronous <c>crypto.subtle</c>. For a
    /// BCL/BouncyCastle key the key's <c>SignAsync</c> default completes synchronously, so behavior
    /// is identical to <c>Sign</c>.
    /// </remarks>
    public async Task<string> SignAsync(
        HttpRequestMetadata metadata,
        IIdentity identity,
        SigningProfile profile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(identity);

        if (!keyStore.TryGetKey(identity.KeyId, out var key) || key is null)
        {
            throw new KeyNotFoundException($"No key found for id '{identity.KeyId}'.");
        }

        var components = Signatures.HeadersForProfile(profile).Split(' ');
        var baseBytes = Signatures.BuildSignatureBase(metadata, components);
        var signature = await key.SignAsync(baseBytes, ct).ConfigureAwait(false);

        return new SignatureHeader(
            identity.KeyId.Value,
            Signatures.AlgorithmLabel(key.Algorithm),
            Signatures.HeadersForProfile(profile),
            Convert.ToBase64String(signature))
            .Format();
    }
}
