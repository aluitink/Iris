using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.Core.Identity;

/// <summary>
/// An asymmetric key pair used to sign and verify ActivityPub HTTP signatures.
/// Wraps an <see cref="RSA"/> or <see cref="ECDsa"/> and exposes the operations the
/// ActivityPub key model needs: signing, verification, PEM private-key load/save, and
/// JWK public-key serialization (the <c>publicKey</c> field of an actor document).
/// </summary>
/// <remarks>
/// A <see cref="KeyPair"/> is immutable with respect to its algorithm and key id. It owns
/// the underlying key material and disposes it via <see cref="Dispose"/>.
/// </remarks>
public sealed class KeyPair : ISigningKey, IDisposable
{
    private readonly AsymmetricAlgorithm _key;

    internal KeyPair(AsymmetricAlgorithm key, KeyAlgorithm algorithm, Iri keyId)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        Algorithm = algorithm;
        KeyId = keyId;
    }

    /// <summary>
    /// Gets the algorithm of the key pair.
    /// </summary>
    public KeyAlgorithm Algorithm { get; }

    /// <summary>
    /// Gets the IRI that identifies this key (the <c>keyId</c> / <c>publicKey.id</c> in a signature).
    /// </summary>
    public Iri KeyId { get; }

    /// <summary>
    /// Gets the underlying <see cref="AsymmetricAlgorithm"/>. Exposed for advanced use;
    /// callers must not dispose it — lifetime is managed by this <see cref="KeyPair"/>.
    /// </summary>
    public AsymmetricAlgorithm Key => _key;

    /// <summary>
    /// Signs the given data with this key's private key (SHA-256 digest).
    /// </summary>
    /// <param name="data">The bytes to sign. Must not be null.</param>
    /// <returns>The signature bytes.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="data"/> is null.</exception>
    public byte[] Sign(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Algorithm switch
        {
            KeyAlgorithm.Rsa when _key is RSA rsa => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            KeyAlgorithm.EcP256 when _key is ECDsa ec => ec.SignData(data, HashAlgorithmName.SHA256),
            _ => throw new NotSupportedException($"Algorithm {Algorithm} is not supported."),
        };
    }

    /// <summary>
    /// Verifies a signature over the given data using this key.
    /// </summary>
    /// <param name="data">The bytes that were signed. Must not be null.</param>
    /// <param name="signature">The signature to verify. Must not be null.</param>
    /// <returns><see langword="true"/> when the signature is valid; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="data"/> or <paramref name="signature"/> is null.</exception>
    public bool Verify(byte[] data, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        try
        {
            return Algorithm switch
            {
                KeyAlgorithm.Rsa when _key is RSA rsa => rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                KeyAlgorithm.EcP256 when _key is ECDsa ec => ec.VerifyData(data, signature, HashAlgorithmName.SHA256),
                _ => false,
            };
        }
        catch (CryptographicException)
        {
            // A malformed signature (wrong length/encoding) is an invalid signature, not an error.
            return false;
        }
    }

    /// <summary>
    /// Exports this key's private key as a PKCS#8 PEM string.
    /// This is the form carried in the authenticated actor document's <c>privateKey</c> property.
    /// </summary>
    /// <returns>A PEM string (e.g. <c>-----BEGIN PRIVATE KEY-----</c>).</returns>
    public string ExportPrivateKeyPem() => _key.ExportPkcs8PrivateKeyPem();

    /// <summary>
    /// Exports this key's public key as a SubjectPublicKeyInfo (PKIX) PEM string.
    /// This is the alternate form carried in an actor document's <c>publicKey</c> property
    /// (<c>publicKeyPem</c>), as opposed to the JWK form produced by <see cref="GetPublicJwk"/>.
    /// </summary>
    /// <returns>A PEM string (e.g. <c>-----BEGIN PUBLIC KEY-----</c>).</returns>
    public string ExportPublicKeyPem()
    {
        var info = Algorithm switch
        {
            KeyAlgorithm.Rsa when _key is RSA rsa => rsa.ExportSubjectPublicKeyInfo(),
            KeyAlgorithm.EcP256 when _key is ECDsa ec => ec.ExportSubjectPublicKeyInfo(),
            _ => throw new NotSupportedException($"Algorithm {Algorithm} is not supported."),
        };
        return ToPem(info);
    }

    private static string ToPem(byte[] der)
    {
        var base64 = Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN PUBLIC KEY-----\n{base64}\n-----END PUBLIC KEY-----\n";
    }

    /// <summary>
    /// Gets the JWK (JSON Web Key) representation of the public key, as a JSON object string.
    /// This is the form placed in the <c>publicKey</c> property of an actor document.
    /// </summary>
    /// <returns>A JSON object string describing the public key (kty, plus n/e or crv/x/y).</returns>
    public string GetPublicJwk()
    {
        if (Algorithm == KeyAlgorithm.Rsa)
        {
            if (_key is RSA rsa)
            {
                var p = rsa.ExportParameters(false);
                return JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["kty"] = "RSA",
                    ["n"] = Base64Url.Encode(p.Modulus!),
                    ["e"] = Base64Url.Encode(p.Exponent!),
                });
            }
        }
        else if (Algorithm == KeyAlgorithm.EcP256)
        {
            if (_key is ECDsa ec)
            {
                var p = ec.ExportParameters(false);
                return JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["kty"] = "EC",
                    ["crv"] = "P-256",
                    ["x"] = Base64Url.Encode(p.Q.X!),
                    ["y"] = Base64Url.Encode(p.Q.Y!),
                });
            }
        }

        throw new NotSupportedException($"Algorithm {Algorithm} is not supported.");
    }

    /// <summary>
    /// Gets a JWK Thumbprint (RFC 7638) of the public key: the base64url-encoded SHA-256
    /// hash of the canonical (sorted, unspaced) JWK JSON. Useful for stable key identity.
    /// </summary>
    /// <returns>The base64url-encoded SHA-256 thumbprint.</returns>
    public string GetThumbprint()
    {
        // RFC 7638 canonical form: members sorted by name, no insignificant whitespace.
        var canonical = Algorithm switch
        {
            KeyAlgorithm.Rsa when _key is RSA rsa =>
                BuildRsaCanonical(rsa),
            KeyAlgorithm.EcP256 when _key is ECDsa ec =>
                BuildEcCanonical(ec),
            _ => throw new NotSupportedException($"Algorithm {Algorithm} is not supported."),
        };

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Base64Url.Encode(hash);
    }

    /// <summary>
    /// Creates a <see cref="KeyPair"/> from a PEM key. Private keys must be PKCS#8
    /// (<c>-----BEGIN PRIVATE KEY-----</c>). RSA public keys may be PKIX
    /// (<c>-----BEGIN PUBLIC KEY-----</c>) or PKCS#1 (<c>-----BEGIN RSA PUBLIC KEY-----</c>, the form
    /// several real-world servers serve in <c>publicKeyPem</c>); EC public keys must be PKIX.
    /// </summary>
    /// <param name="pem">The PEM-encoded private or public key.</param>
    /// <param name="algorithm">The algorithm the key was generated with.</param>
    /// <param name="keyId">The IRI that identifies the key.</param>
    /// <returns>The loaded <see cref="KeyPair"/> (owns the key material; public-only when a public key was given).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="pem"/> is null.</exception>
    /// <exception cref="CryptographicException">When <paramref name="pem"/> is not a valid key for the algorithm.</exception>
    public static KeyPair FromPem(string pem, KeyAlgorithm algorithm, Iri keyId)
    {
        ArgumentNullException.ThrowIfNull(pem);

        AsymmetricAlgorithm key = algorithm switch
        {
            KeyAlgorithm.Rsa => RSA.Create(),
            KeyAlgorithm.EcP256 => ECDsa.Create(ECCurve.NamedCurves.nistP256),
            _ => throw new NotSupportedException($"Algorithm {algorithm} is not supported."),
        };

        try
        {
            ImportFromPem(key, pem);
            return new KeyPair(key, algorithm, keyId);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Imports a PEM key into <paramref name="key"/>, accepting a PKCS#1 RSA public key
    /// (<c>-----BEGIN RSA PUBLIC KEY-----</c>) for <see cref="RSA"/> in addition to the forms
    /// <see cref="AsymmetricAlgorithm.ImportFromPem"/> already supports.
    /// </summary>
    private static void ImportFromPem(AsymmetricAlgorithm key, string pem)
    {
        try
        {
            key.ImportFromPem(pem);
            return;
        }
        catch (CryptographicException)
        {
            if (key is not RSA rsa || !pem.Contains("RSA PUBLIC KEY", StringComparison.Ordinal))
            {
                throw;
            }

            // PKCS#1 (raw) RSA public key (the form real-world servers serve in publicKeyPem):
            // import the RsaPublicKey DER directly.
            ImportPkcs1(rsa, pem);
        }
    }

    /// <summary>
    /// Imports the PKCS#1 (raw) RSA public key carried by <paramref name="pem"/> into
    /// <paramref name="rsa"/>: the PEM's base64 body is the <c>RsaPublicKey</c> DER, which is wrapped
    /// in a SubjectPublicKeyInfo (PKIX) envelope so <see cref="RSA.ImportSubjectPublicKeyInfo"/> accepts
    /// it (the BCL has no PKCS#1 public-key import).
    /// </summary>
    private static void ImportPkcs1(RSA rsa, string pem)
    {
        var pkcs1 = Convert.FromBase64String(PemBody(pem));
        rsa.ImportSubjectPublicKeyInfo(WrapInSubjectPublicKeyInfo(pkcs1), out _);
    }

    /// <summary>
    /// Wraps a PKCS#1 <c>RsaPublicKey</c> DER in a SubjectPublicKeyInfo (PKIX) envelope: a
    /// <c>SubjectPublicKeyInfo ::= SEQUENCE { algorithm, subjectPublicKey }</c> whose algorithm is the
    /// rsaEncryption AlgorithmIdentifier (1.2.840.113549.1.1.1, NULL parameters) and whose
    /// <c>subjectPublicKey</c> is an OCTET STRING carrying the <c>RsaPublicKey</c> bytes.
    /// </summary>
    private static byte[] WrapInSubjectPublicKeyInfo(byte[] pkcs1)
    {
        // AlgorithmIdentifier: SEQUENCE { OID 1.2.840.113549.1.1.1, NULL }.
        var rsaEncryptionOid = new byte[]
        {
            0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01,
            0x05, 0x00,
        };
        var algorithm = new byte[rsaEncryptionOid.Length + 2];
        algorithm[0] = 0x30;
        algorithm[1] = (byte)rsaEncryptionOid.Length;
        Array.Copy(rsaEncryptionOid, 0, algorithm, 2, rsaEncryptionOid.Length);

        var subjectPublicKey = DerOctetString(pkcs1);
        var inner = Concat(algorithm, subjectPublicKey);
        return Concat([0x30, (byte)inner.Length], inner);
    }

    /// <summary>
    /// Encodes <paramref name="content"/> as a DER OCTET STRING (0x04 + length + content), handling
    /// long-form lengths.
    /// </summary>
    private static byte[] DerOctetString(byte[] content)
    {
        var length = DerLength(content.Length);
        var octetString = new byte[1 + length.Length + content.Length];
        octetString[0] = 0x04;
        Array.Copy(length, 0, octetString, 1, length.Length);
        Array.Copy(content, 0, octetString, 1 + length.Length, content.Length);
        return octetString;
    }

    /// <summary>
    /// Encodes a DER length (short form when it fits in one byte, long form otherwise).
    /// </summary>
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

    /// <summary>
    /// Extracts the concatenated base64 body of a PEM block (the lines between the BEGIN and END
    /// markers, with the BEGIN/END type stripped).
    /// </summary>
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

    /// <summary>
    /// Creates a <b>public-only</b> <see cref="KeyPair"/> from a JSON Web Key (JWK). The result can
    /// <see cref="Verify">verify</see> signatures but cannot sign (it has no private key).
    /// </summary>
    /// <param name="jwkJson">A JWK JSON object string (the <c>publicKey</c> field of an actor document).
    /// For RSA: <c>kty</c>=<c>"RSA"</c> with <c>n</c>/<c>e</c>; for EC: <c>kty</c>=<c>"EC"</c> with
    /// <c>crv</c>=<c>"P-256"</c>, <c>x</c>/<c>y</c>. Values are base64url-encoded.</param>
    /// <param name="algorithm">The algorithm the key was generated with.</param>
    /// <param name="keyId">The IRI that identifies the key.</param>
    /// <returns>A public-only <see cref="KeyPair"/> (owns the key material; verify-only).</returns>
    /// <remarks>
    /// This is the inbound (server) counterpart to <see cref="GetPublicJwk"/>: a server that receives
    /// a signed request from a remote actor fetches that actor's document, reads the <c>publicKey</c>
    /// JWK, and reconstructs a public-only key to verify the signature. See Resolved Decision #27.
    /// </remarks>
    /// <exception cref="ArgumentNullException">When <paramref name="jwkJson"/> is null.</exception>
    /// <exception cref="FormatException">When the JWK is malformed or missing required members.</exception>
    /// <exception cref="CryptographicException">When the JWK parameters do not form a valid key.</exception>
    public static KeyPair FromJwk(string jwkJson, KeyAlgorithm algorithm, Iri keyId)
    {
        ArgumentNullException.ThrowIfNull(jwkJson);

        var jwk = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jwkJson);
        if (jwk.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new FormatException("A JWK must be a JSON object.");
        }

        AsymmetricAlgorithm key = algorithm switch
        {
            KeyAlgorithm.Rsa => BuildRsaFromJwk(jwk),
            KeyAlgorithm.EcP256 => BuildEcFromJwk(jwk),
            _ => throw new NotSupportedException($"Algorithm {algorithm} is not supported."),
        };

        try
        {
            return new KeyPair(key, algorithm, keyId);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _key.Dispose();

    private static string BuildRsaCanonical(RSA rsa)
    {
        var p = rsa.ExportParameters(false);
        // RFC 7638: members sorted lexicographically (e, kty, n).
        return $"\"e\":\"{Base64Url.Encode(p.Exponent!)}\",\"kty\":\"RSA\",\"n\":\"{Base64Url.Encode(p.Modulus!)}\"";
    }

    private static string BuildEcCanonical(ECDsa ec)
    {
        var p = ec.ExportParameters(false);
        // RFC 7638: members sorted lexicographically (crv, kty, x, y).
        return $"\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url.Encode(p.Q.X!)}\",\"y\":\"{Base64Url.Encode(p.Q.Y!)}\"";
    }

    private static RSA BuildRsaFromJwk(System.Text.Json.JsonElement jwk)
    {
        var modulus = DecodeBase64Url(RequireString(jwk, "n"));
        var exponent = DecodeBase64Url(RequireString(jwk, "e"));
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
        return rsa;
    }

    private static ECDsa BuildEcFromJwk(System.Text.Json.JsonElement jwk)
    {
        var x = DecodeBase64Url(RequireString(jwk, "x"));
        var y = DecodeBase64Url(RequireString(jwk, "y"));
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ec.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        });
        return ec;
    }

    private static string RequireString(System.Text.Json.JsonElement jwk, string name)
    {
        if (jwk.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return value.GetString()!;
        }

        throw new FormatException($"JWK is missing the required '{name}' member.");
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padding = (4 - (value.Length % 4)) % 4;
        var normalized = value.PadRight(value.Length + padding, '=')
            .Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized);
    }

    private static class Base64Url
    {
        public static string Encode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
