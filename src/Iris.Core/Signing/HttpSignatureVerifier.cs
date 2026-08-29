namespace Iris.Core.Signing;

/// <summary>
/// Default <see cref="ISignatureVerifier"/>. Parses the <c>Signature</c> header, resolves the
/// key from the <see cref="IKeyStore"/> by <c>keyId</c>, reconstructs the signature base from
/// the declared <c>headers</c> list, and checks the signature.
/// </summary>
/// <param name="keyStore">The store used to resolve keys.</param>
public sealed class HttpSignatureVerifier(IKeyStore keyStore) : ISignatureVerifier
{
    /// <inheritdoc/>
    public bool Verify(HttpRequestMetadata metadata, string signatureHeader)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        if (!SignatureHeader.TryParse(signatureHeader, out var header) || header is null)
        {
            return false;
        }

        if (!Iri.TryParse(header.KeyId, out var keyId))
        {
            return false;
        }

        if (!keyStore.TryGetKey(keyId, out var key) || key is null)
        {
            return false;
        }

        // The key is borrowed from the store (the store owns its lifetime); do not dispose it here.
        return VerifyWithKey(metadata, key, header);
    }

    /// <inheritdoc/>
    public bool Verify(HttpRequestMetadata metadata, ISigningKey key, string signatureHeader)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        if (!SignatureHeader.TryParse(signatureHeader, out var header) || header is null)
        {
            return false;
        }

        return VerifyWithKey(metadata, key, header);
    }

    private static bool VerifyWithKey(HttpRequestMetadata metadata, ISigningKey key, SignatureHeader header)
    {
        var components = header.Headers.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
        {
            return false;
        }

        var baseBytes = Signatures.BuildSignatureBase(metadata, components);

        if (!TryDecodeBase64(header.Signature, out var signature))
        {
            return false;
        }

        return Signatures.VerifyBase(key, baseBytes, signature);
    }

    private static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
