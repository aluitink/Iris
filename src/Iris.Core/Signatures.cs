using System.Security.Cryptography;
using System.Text;

namespace Iris.Core;

/// <summary>
/// Shared, HTTP-agnostic cryptography and string building for ActivityPub HTTP signatures
/// (draft-cavage-http-signatures-03 as used by ActivityPub). Used by both
/// <see cref="ISignatureSigner"/> and <see cref="ISignatureVerifier"/> so the signing and
/// verification paths never drift.
/// </summary>
/// <remarks>
/// The signature base is built by joining, for each component in the declared
/// <c>headers</c> list, a <c>name: value</c> line (the <c>(request-target)</c> pseudo-header
/// is special-cased), terminated by a newline. The result is SHA-256 hashed and signed with
/// the key. The verifier reconstructs the same base from the raw request, so it accepts both
/// signing profiles.
/// </remarks>
public static class Signatures
{
    /// <summary>
    /// The <c>Signature</c> header name.
    /// </summary>
    public const string SignatureHeaderName = "Signature";

    /// <summary>
    /// The <c>Signature-Input</c> header name (not used by Iris, but defined here for completeness).
    /// </summary>
    public const string SignatureInputHeaderName = "Signature-Input";

    /// <summary>
    /// The <c>host</c> header name.
    /// </summary>
    public const string HostHeaderName = "host";

    /// <summary>
    /// The <c>date</c> header name.
    /// </summary>
    public const string DateHeaderName = "date";

    /// <summary>
    /// The <c>digest</c> header name.
    /// </summary>
    public const string DigestHeaderName = "digest";

    /// <summary>
    /// The <c>content-type</c> header name.
    /// </summary>
    public const string ContentTypeHeaderName = "content-type";

    /// <summary>
    /// The pseudo-header for the request target.
    /// </summary>
    public const string RequestTargetComponent = "(request-target)";

    /// <summary>
    /// The digest algorithm used for the <c>digest</c> header (per the ServerToServer profile).
    /// </summary>
    public const string DigestAlgorithm = "SHA-512";

    /// <summary>
    /// The signature header value component for <c>(request-target) host date</c>
    /// (<see cref="SigningProfile.ClientToServer"/>).
    /// </summary>
    public const string ClientToServerHeaders = "(request-target) host date";

    /// <summary>
    /// The signature header value component for
    /// <c>(request-target) host date digest content-type</c>
    /// (<see cref="SigningProfile.ServerToServer"/>).
    /// </summary>
    public const string ServerToServerHeaders = "(request-target) host date digest content-type";

    /// <summary>
    /// Returns the <c>algorithm</c> value for the <see cref="SignatureHeader"/> for the given key algorithm.
    /// </summary>
    /// <param name="algorithm">The key algorithm.</param>
    /// <returns><c>rsa-sha256</c> for RSA, <c>ecdsa-p256-sha256</c> for EC P-256, <c>ed25519</c> for Ed25519.</returns>
    public static string AlgorithmLabel(KeyAlgorithm algorithm)
        => algorithm switch
        {
            KeyAlgorithm.Rsa => "rsa-sha256",
            KeyAlgorithm.EcP256 => "ecdsa-p256-sha256",
            KeyAlgorithm.Ed25519 => "ed25519",
            _ => throw new NotSupportedException($"Algorithm {algorithm} is not supported."),
        };

    /// <summary>
    /// Returns the <c>headers</c> value for a <see cref="SignatureHeader"/> for the given profile.
    /// </summary>
    /// <param name="profile">The signing profile.</param>
    /// <returns>The space-separated component list.</returns>
    public static string HeadersForProfile(SigningProfile profile)
        => profile switch
        {
            SigningProfile.ClientToServer => ClientToServerHeaders,
            SigningProfile.ServerToServer => ServerToServerHeaders,
            _ => throw new NotSupportedException($"Profile {profile} is not supported."),
        };

    /// <summary>
    /// Computes the <c>digest</c> header value for a request body (SHA-512, base64).
    /// </summary>
    /// <param name="body">The raw request body bytes. May be empty (an empty body has a defined digest).</param>
    /// <returns>A string of the form <c>sha-512=base64</c>.</returns>
    public static string ComputeDigest(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var hash = SHA512.HashData(body);
        return $"sha-512={Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Builds the signature base (the bytes that are SHA-256 hashed and signed) from the raw
    /// request, given the declared component list.
    /// </summary>
    /// <param name="metadata">The request fields.</param>
    /// <param name="components">The component list from the <c>Signature</c> header
    /// (e.g. <c>(request-target) host date</c>).</param>
    /// <returns>The signature base as bytes (UTF-8).</returns>
    /// <remarks>
    /// The <c>digest</c> component value must already be the header value
    /// (<c>sha-512=base64</c>); this method does not recompute it — it trusts the declared
    /// value, exactly as a verifier would when reconstructing the base from the wire.
    /// </remarks>
    public static byte[] BuildSignatureBase(HttpRequestMetadata metadata, IReadOnlyList<string> components)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(components);

        var builder = new StringBuilder();
        for (var i = 0; i < components.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            var component = components[i];
            var value = component switch
            {
                RequestTargetComponent => $"{metadata.Method.ToLowerInvariant()} {metadata.PathAndQuery}",
                HostHeaderName => metadata.Host,
                DateHeaderName => metadata.Date,
                DigestHeaderName => metadata.GetHeader(DigestHeaderName) ?? "",
                ContentTypeHeaderName => metadata.ContentType ?? "",
                _ => metadata.GetHeader(component) ?? "",
            };

            builder.Append(component).Append(": ").Append(value);
        }

        builder.Append('\n');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Signs a signature base with the given key.
    /// </summary>
    /// <param name="key">The signing key (RSA / EC <see cref="KeyPair"/> or Ed25519 <see cref="Ed25519Key"/>).</param>
    /// <param name="baseBytes">The signature base bytes (see <see cref="BuildSignatureBase"/>).</param>
    /// <returns>The signature bytes (base64 is applied by the caller when building the header).</returns>
    public static byte[] SignBase(ISigningKey key, byte[] baseBytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(baseBytes);
        return key.Sign(baseBytes);
    }

    /// <summary>
    /// Verifies a signature over a signature base with the given key.
    /// </summary>
    /// <param name="key">The signing key (RSA / EC <see cref="KeyPair"/> or Ed25519 <see cref="Ed25519Key"/>).</param>
    /// <param name="baseBytes">The signature base bytes (see <see cref="BuildSignatureBase"/>).</param>
    /// <param name="signature">The signature bytes.</param>
    /// <returns><see langword="true"/> when valid; otherwise <see langword="false"/>.</returns>
    public static bool VerifyBase(ISigningKey key, byte[] baseBytes, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(baseBytes);
        ArgumentNullException.ThrowIfNull(signature);
        return key.Verify(baseBytes, signature);
    }
}
