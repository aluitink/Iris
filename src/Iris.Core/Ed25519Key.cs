using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Iris.Core;

/// <summary>
/// An Ed25519 (RFC 8032) key used to sign and verify ActivityPub HTTP signatures.
/// Wraps a BouncyCastle <see cref="Ed25519PublicKeyParameters"/> (and, when a private key was
/// supplied, an <see cref="Ed25519PrivateKeyParameters"/>) and exposes the operations the
/// ActivityPub key model needs: signing, verification, PKIX PEM export, PKCS#8 PEM import, and
/// JWK public-key serialization (the <c>publicKey</c> field of an actor document).
/// </summary>
/// <remarks>
/// The BCL has no <c>Ed25519</c> type on this runtime (the .NET 9+ <c>MLDsa</c> family that includes
/// Ed25519 is Windows/CNG-only and reports <c>IsSupported == false</c> on Linux), so Ed25519 — the
/// algorithm Pleroma and several modern servers sign with by default — is provided by BouncyCastle
/// (Resolved Decision #49). Ed25519 is not an <see cref="AsymmetricAlgorithm"/>, so this is a
/// self-contained type rather than a <see cref="KeyPair"/>; it shares the same JWK/PEM/label
/// conventions as <see cref="KeyPair"/> so the two are interchangeable at the wire boundary.
/// A key is public-only when built from a public key/PEM/JWK (it can verify but not sign).
/// </remarks>
public sealed class Ed25519Key : ISigningKey
{
    private readonly Ed25519PublicKeyParameters _public;
    private readonly Ed25519PrivateKeyParameters? _private;

    internal Ed25519Key(Ed25519PublicKeyParameters publicKey, Ed25519PrivateKeyParameters? privateKey)
    {
        _public = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
        _private = privateKey;
    }

    /// <inheritdoc/>
    public KeyAlgorithm Algorithm => KeyAlgorithm.Ed25519;

    /// <inheritdoc/>
    public Iri KeyId { get; internal set; }

    /// <summary>
    /// Gets <see langword="true"/> when this key has a private component and can sign.
    /// </summary>
    public bool CanSign => _private is not null;

    /// <summary>
    /// The fixed length (in bytes) of an Ed25519 public or private key.
    /// </summary>
    public const int KeySizeBytes = 32;

    /// <summary>
    /// The fixed length (in bytes) of an Ed25519 signature.
    /// </summary>
    public const int SignatureSizeBytes = 64;

    /// <summary>
    /// Creates a new random Ed25519 key pair (can sign and verify).
    /// </summary>
    /// <param name="keyId">The IRI that will identify the key.</param>
    /// <returns>A new <see cref="Ed25519Key"/> with a private key (can sign).</returns>
    public static Ed25519Key Generate(Iri keyId)
    {
        var (pub, priv) = GenerateKeyPair();
        return new Ed25519Key(pub, priv) { KeyId = keyId };
    }

    /// <summary>
    /// Creates a <b>public-only</b> <see cref="Ed25519Key"/> from a 32-byte raw public key (can verify, not sign).
    /// </summary>
    /// <param name="publicKeyBytes">The 32-byte Ed25519 public key.</param>
    /// <param name="keyId">The IRI that identifies the key.</param>
    /// <returns>A public-only <see cref="Ed25519Key"/>.</returns>
    /// <exception cref="ArgumentException">When <paramref name="publicKeyBytes"/> is not 32 bytes.</exception>
    public static Ed25519Key FromPublicKey(byte[] publicKeyBytes, Iri keyId)
    {
        ArgumentNullException.ThrowIfNull(publicKeyBytes);
        if (publicKeyBytes.Length != KeySizeBytes)
        {
            throw new ArgumentException($"An Ed25519 public key must be {KeySizeBytes} bytes.", nameof(publicKeyBytes));
        }

        var key = new Ed25519Key(new Ed25519PublicKeyParameters(publicKeyBytes, 0), null) { KeyId = keyId };
        return key;
    }

    /// <summary>
    /// Creates a key pair from a 32-byte raw private (seed) key (can sign and verify). The public key
    /// is derived from the seed.
    /// </summary>
    /// <param name="privateSeedBytes">The 32-byte Ed25519 private seed.</param>
    /// <param name="keyId">The IRI that identifies the key.</param>
    /// <returns>An <see cref="Ed25519Key"/> with a private key (can sign).</returns>
    /// <exception cref="ArgumentException">When <paramref name="privateSeedBytes"/> is not 32 bytes.</exception>
    public static Ed25519Key FromPrivateSeed(byte[] privateSeedBytes, Iri keyId)
    {
        ArgumentNullException.ThrowIfNull(privateSeedBytes);
        if (privateSeedBytes.Length != KeySizeBytes)
        {
            throw new ArgumentException($"An Ed25519 private seed must be {KeySizeBytes} bytes.", nameof(privateSeedBytes));
        }

        var priv = new Ed25519PrivateKeyParameters(privateSeedBytes, 0);
        var pub = priv.GeneratePublicKey();
        return new Ed25519Key(pub, priv) { KeyId = keyId };
    }

    /// <summary>
    /// Loads a key from a PEM string. Public keys must be PKIX
    /// (<c>-----BEGIN PUBLIC KEY-----</c>); private keys must be PKCS#8
    /// (<c>-----BEGIN PRIVATE KEY-----</c>). The algorithm is self-identifying (the PKIX envelope
    /// carries the id-Ed25519 AlgorithmIdentifier), so no algorithm argument is needed.
    /// </summary>
    /// <param name="pem">The PEM-encoded public or private key.</param>
    /// <param name="keyId">The IRI that identifies the key.</param>
    /// <returns>The loaded <see cref="Ed25519Key"/> (public-only when a public key was given).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="pem"/> is null.</exception>
    /// <exception cref="FormatException">When <paramref name="pem"/> is not a valid Ed25519 key.</exception>
    public static Ed25519Key FromPem(string pem, Iri keyId)
    {
        ArgumentNullException.ThrowIfNull(pem);
        var body = PemBody(pem);
        if (body.Length == 0)
        {
            throw new FormatException("The PEM has no base64 body.");
        }

        byte[] der;
        try
        {
            der = Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            throw new FormatException("The PEM body is not valid base64.");
        }

        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            // PKCS#8: the Ed25519 private key is carried as an OCTET STRING wrapping the 32-byte seed.
            var seed = UnwrapPkcs8Ed25519Seed(der);
            return FromPrivateSeed(seed, keyId);
        }

        // PKIX: the subjectPublicKey is an OCTET STRING wrapping the 32-byte public key.
        var publicKey = UnwrapPkixEd25519PublicKey(der);
        return FromPublicKey(publicKey, keyId);
    }

    /// <summary>
    /// Creates a <b>public-only</b> <see cref="Ed25519Key"/> from a JSON Web Key (JWK). For Ed25519 the
    /// JWK is <c>kty</c>=<c>"OKP"</c>, <c>crv</c>=<c>"Ed25519"</c>, <c>x</c>=base64url(32-byte public key)
    /// (RFC 8037).
    /// </summary>
    /// <param name="jwkJson">A JWK JSON object string.</param>
    /// <param name="keyId">The IRI that identifies the key.</param>
    /// <returns>A public-only <see cref="Ed25519Key"/> (verify-only).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="jwkJson"/> is null.</exception>
    /// <exception cref="FormatException">When the JWK is malformed or missing required members.</exception>
    public static Ed25519Key FromJwk(string jwkJson, Iri keyId)
    {
        ArgumentNullException.ThrowIfNull(jwkJson);
        var jwk = JsonSerializer.Deserialize<JsonElement>(jwkJson);
        if (jwk.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("A JWK must be a JSON object.");
        }

        var x = DecodeBase64Url(RequireString(jwk, "x"));
        return FromPublicKey(x, keyId);
    }

    /// <summary>
    /// Signs the given data with this key's private key (Ed25519, no separate hash — the algorithm
    /// hashes internally).
    /// </summary>
    /// <param name="data">The bytes to sign. Must not be null.</param>
    /// <returns>The 64-byte signature.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="data"/> is null.</exception>
    /// <exception cref="InvalidOperationException">When this key is public-only (cannot sign).</exception>
    public byte[] Sign(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_private is null)
        {
            throw new InvalidOperationException("This Ed25519 key is public-only and cannot sign.");
        }

        var signer = new Ed25519Signer();
        signer.Init(true, _private);
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    /// <summary>
    /// Verifies a signature over the given data using this key's public key.
    /// </summary>
    /// <param name="data">The bytes that were signed. Must not be null.</param>
    /// <param name="signature">The 64-byte signature to verify. Must not be null.</param>
    /// <returns><see langword="true"/> when the signature is valid; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="data"/> or <paramref name="signature"/> is null.</exception>
    public bool Verify(byte[] data, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, _public);
            verifier.BlockUpdate(data, 0, data.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Org.BouncyCastle.Crypto.CryptoException)
        {
            // A malformed signature (wrong length) is an invalid signature, not an error.
            return false;
        }
    }

    /// <summary>
    /// Exports this key's public key as a SubjectPublicKeyInfo (PKIX) PEM string
    /// (<c>-----BEGIN PUBLIC KEY-----</c>).
    /// </summary>
    /// <returns>A PKIX PEM string.</returns>
    public string ExportPublicKeyPem() => ToPem(WrapInSubjectPublicKeyInfo(_public.GetEncoded()));

    /// <summary>
    /// Exports this key's private key as a PKCS#8 PEM string (<c>-----BEGIN PRIVATE KEY-----</c>).
    /// </summary>
    /// <returns>A PKCS#8 PEM string.</returns>
    /// <exception cref="InvalidOperationException">When this key is public-only (has no private key).</exception>
    public string ExportPrivateKeyPem()
    {
        if (_private is null)
        {
            throw new InvalidOperationException("This Ed25519 key is public-only and has no private key to export.");
        }

        return ToPem(WrapInPkcs8(_private.GetEncoded()), "PRIVATE KEY");
    }

    /// <summary>
    /// Gets the JWK (JSON Web Key) representation of the public key, as a JSON object string
    /// (RFC 8037: <c>kty</c>=<c>"OKP"</c>, <c>crv</c>=<c>"Ed25519"</c>, <c>x</c>=base64url(32-byte key)).
    /// This is the form placed in the <c>publicKey</c> property of an actor document.
    /// </summary>
    /// <returns>A JSON object string describing the public key.</returns>
    public string GetPublicJwk() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["kty"] = "OKP",
        ["crv"] = "Ed25519",
        ["x"] = Base64Url.Encode(_public.GetEncoded()),
    });

    /// <summary>
    /// Gets a JWK Thumbprint (RFC 7638) of the public key: the base64url-encoded SHA-256 hash of the
    /// canonical (sorted, unspaced) JWK JSON. Useful for stable key identity.
    /// </summary>
    /// <returns>The base64url-encoded SHA-256 thumbprint.</returns>
    public string GetThumbprint()
    {
        // RFC 7638 canonical form: members sorted by name, no insignificant whitespace (crv, kty, x).
        var canonical = $"\"crv\":\"Ed25519\",\"kty\":\"OKP\",\"x\":\"{Base64Url.Encode(_public.GetEncoded())}\"";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Base64Url.Encode(hash);
    }

    /// <summary>
    /// Gets the 32-byte raw Ed25519 public key.
    /// </summary>
    public byte[] GetPublicKeyBytes() => _public.GetEncoded();

    /// <summary>
    /// Gets the 32-byte raw Ed25519 private seed, or null when this key is public-only.
    /// </summary>
    public byte[]? GetPrivateSeedBytes() => _private?.GetEncoded();

    private static (Ed25519PublicKeyParameters Pub, Ed25519PrivateKeyParameters Priv) GenerateKeyPair()
    {
        var generator = new Org.BouncyCastle.Crypto.Generators.Ed25519KeyPairGenerator();
        generator.Init(new Org.BouncyCastle.Crypto.Parameters.Ed25519KeyGenerationParameters(new Org.BouncyCastle.Security.SecureRandom()));
        var pair = generator.GenerateKeyPair();
        return ((Ed25519PublicKeyParameters)pair.Public, (Ed25519PrivateKeyParameters)pair.Private);
    }

    // ---- DER / PEM helpers (Ed25519-specific; the BCL has no Ed25519 SubjectPublicKeyInfo/PKCS#8) ----

    private const string PublicPemBegin = "-----BEGIN PUBLIC KEY-----";
    private const string PublicPemEnd = "-----END PUBLIC KEY-----";

    private static string ToPem(byte[] der, string label = "PUBLIC KEY")
    {
        var base64 = Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN {label}-----\n{base64}\n-----END {label}-----\n";
    }

    /// <summary>
    /// Wraps a 32-byte Ed25519 public key in a SubjectPublicKeyInfo (PKIX) envelope:
    /// <c>SEQUENCE { AlgorithmIdentifier(id-Ed25519), OCTET STRING(32-byte key) }</c>.
    /// </summary>
    private static byte[] WrapInSubjectPublicKeyInfo(byte[] publicKey)
    {
        var algorithm = Ed25519AlgorithmIdentifier();
        var inner = Concat(algorithm, DerOctetString(publicKey));
        return Concat([0x30, (byte)inner.Length], inner);
    }

    /// <summary>
    /// Wraps a 32-byte Ed25519 private seed in a PKCS#8 envelope:
    /// <c>SEQUENCE { 1, SEQUENCE(id-Ed25519), OCTET STRING( OCTET STRING(seed) ) }</c>.
    /// </summary>
    private static byte[] WrapInPkcs8(byte[] seed)
    {
        var algorithm = Ed25519AlgorithmIdentifier();
        // privateKey OCTET STRING wrapping an inner OCTET STRING that holds the seed.
        var privateKeyOctets = DerOctetString(DerOctetString(seed));
        var version = new byte[] { 0x02, 0x01, 0x00 }; // INTEGER 1
        var inner = Concat(version, Concat(algorithm, privateKeyOctets));
        return Concat([0x30, (byte)inner.Length], inner);
    }

    /// <summary>
    /// The id-Ed25519 AlgorithmIdentifier: <c>SEQUENCE { OID 1.3.101.112 }</c> (parameters absent).
    /// </summary>
    private static byte[] Ed25519AlgorithmIdentifier()
    {
        var oid = new byte[] { 0x06, 0x03, 0x2B, 0x65, 0x70 }; // 1.3.101.112
        var identifier = new byte[oid.Length + 2];
        identifier[0] = 0x30;
        identifier[1] = (byte)oid.Length;
        Array.Copy(oid, 0, identifier, 2, oid.Length);
        return identifier;
    }

    /// <summary>
    /// Unwraps the 32-byte public key from a PKIX SubjectPublicKeyInfo DER for Ed25519.
    /// </summary>
    private static byte[] UnwrapPkixEd25519PublicKey(byte[] der)
    {
        var subjectPublicKey = ReadSequence(der).Content;
        // Skip the AlgorithmIdentifier SEQUENCE, then read the subjectPublicKey OCTET STRING.
        var afterAlgorithm = ReadTlv(subjectPublicKey, 0).TotalLength;
        var publicKeyTlv = ReadTlv(subjectPublicKey, afterAlgorithm);
        if (publicKeyTlv.Tag != 0x04 || publicKeyTlv.Content.Length != KeySizeBytes)
        {
            throw new FormatException("The PKIX envelope does not carry a 32-byte Ed25519 public key.");
        }

        return publicKeyTlv.Content;
    }

    /// <summary>
    /// Unwraps the 32-byte private seed from a PKCS#8 DER for Ed25519.
    /// </summary>
    private static byte[] UnwrapPkcs8Ed25519Seed(byte[] der)
    {
        var outer = ReadSequence(der).Content;
        // outer: INTEGER(1), SEQUENCE(id-Ed25519), OCTET STRING( OCTET STRING(seed) ).
        // Read past the INTEGER and the AlgorithmIdentifier SEQUENCE to the privateKey OCTET STRING.
        var pos = ReadTlv(outer, 0).TotalLength;                       // INTEGER 1
        pos += ReadTlv(outer, pos).TotalLength;                        // AlgorithmIdentifier SEQUENCE
        var privateKeyTlv = ReadTlv(outer, pos);
        if (privateKeyTlv.Tag != 0x04)
        {
            throw new FormatException("The PKCS#8 envelope is missing the privateKey OCTET STRING.");
        }

        // privateKeyOctets is an OCTET STRING wrapping another OCTET STRING holding the seed.
        var seedTlv = ReadTlv(privateKeyTlv.Content, 0);
        if (seedTlv.Tag != 0x04 || seedTlv.Content.Length != KeySizeBytes)
        {
            throw new FormatException("The PKCS#8 envelope does not carry a 32-byte Ed25519 seed.");
        }

        return seedTlv.Content;
    }

    private readonly record struct Tlv(byte Tag, byte[] Content, int TotalLength);

    private static Tlv ReadTlv(byte[] data, int offset)
    {
        var tag = data[offset];
        var first = data[offset + 1];
        int headerLength;
        int contentLength;
        if (first < 0x80)
        {
            headerLength = 2;
            contentLength = first;
        }
        else
        {
            var numBytes = first & 0x7F;
            headerLength = 2 + numBytes;
            contentLength = 0;
            for (var i = 0; i < numBytes; i++)
            {
                contentLength = (contentLength << 8) | data[offset + 2 + i];
            }
        }

        var contentOffset = offset + headerLength;
        var content = new byte[contentLength];
        Array.Copy(data, contentOffset, content, 0, contentLength);
        var totalLength = headerLength + contentLength;
        return new Tlv(tag, content, totalLength);
    }

    private static Tlv ReadSequence(byte[] data)
    {
        var tlv = ReadTlv(data, 0);
        if (tlv.Tag != 0x30)
        {
            throw new FormatException("Expected a DER SEQUENCE.");
        }

        return tlv;
    }

    private static byte[] DerOctetString(byte[] content)
    {
        var length = DerLength(content.Length);
        var octetString = new byte[1 + length.Length + content.Length];
        octetString[0] = 0x04;
        Array.Copy(length, 0, octetString, 1, length.Length);
        Array.Copy(content, 0, octetString, 1 + length.Length, content.Length);
        return octetString;
    }

    private static byte[] DerLength(int value)
    {
        if (value < 0x80)
        {
            return [(byte)value];
        }

        var bytes = new List<byte>();
        for (var v = value; v > 0; v >>= 8)
        {
            bytes.Insert(0, (byte)(v & 0xFF));
        }

        var length = new byte[1 + bytes.Count];
        length[0] = (byte)(0x80 | bytes.Count);
        bytes.CopyTo(1, length, 0, bytes.Count);
        return length;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Array.Copy(a, 0, result, 0, a.Length);
        Array.Copy(b, 0, result, a.Length, b.Length);
        return result;
    }

    private static string PemBody(string pem)
    {
        var lines = pem.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var body = new StringBuilder();
        var inBody = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("-----BEGIN ", StringComparison.Ordinal))
            {
                inBody = true;
                continue;
            }

            if (line.StartsWith("-----END ", StringComparison.Ordinal))
            {
                inBody = false;
                continue;
            }

            if (inBody)
            {
                body.Append(line);
            }
        }

        return body.ToString();
    }

    private static string RequireString(JsonElement jwk, string name)
    {
        if (jwk.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        throw new FormatException($"JWK is missing the required '{name}' member.");
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padding = (4 - (value.Length % 4)) % 4;
        var normalized = value.PadRight(value.Length + padding, '=').Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized);
    }

    private static class Base64Url
    {
        public static string Encode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
