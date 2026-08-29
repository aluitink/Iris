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
}
