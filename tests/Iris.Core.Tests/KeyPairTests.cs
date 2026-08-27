using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Core;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for <see cref="KeyPair"/> sign/verify, JWK serialization, and thumbprints.
/// </summary>
public class KeyPairTests
{
    private static readonly Iri KeyId = new("https://a.domain.local/u/alice#main-key");
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("iris test payload 1234567890");

    [Theory]
    [InlineData(KeyAlgorithm.Rsa)]
    [InlineData(KeyAlgorithm.EcP256)]
    public void SignVerify_RoundTrip_Verifies(KeyAlgorithm algorithm)
    {
        using var key = KeyPairGenerator.Generate(algorithm, KeyId);

        var signature = key.Sign(Payload);

        Assert.True(key.Verify(Payload, signature));
    }

    [Theory]
    [InlineData(KeyAlgorithm.Rsa)]
    [InlineData(KeyAlgorithm.EcP256)]
    public void Verify_TamperedData_Fails(KeyAlgorithm algorithm)
    {
        using var key = KeyPairGenerator.Generate(algorithm, KeyId);
        var signature = key.Sign(Payload);

        var tampered = (byte[])Payload.Clone();
        tampered[^1] ^= 0xFF;

        Assert.False(key.Verify(tampered, signature));
    }

    [Theory]
    [InlineData(KeyAlgorithm.Rsa)]
    [InlineData(KeyAlgorithm.EcP256)]
    public void Verify_TamperedSignature_Fails(KeyAlgorithm algorithm)
    {
        using var key = KeyPairGenerator.Generate(algorithm, KeyId);
        var signature = key.Sign(Payload);
        signature[0] ^= 0xFF;

        Assert.False(key.Verify(Payload, signature));
    }

    [Fact]
    public void Verify_MalformedSignature_ReturnsFalse()
    {
        using var key = KeyPairGenerator.GenerateEcP256(KeyId);

        // A signature of the wrong length for ECDSA (a single byte) is malformed, not a throw.
        Assert.False(key.Verify(Payload, [0x01]));
    }

    [Theory]
    [InlineData(KeyAlgorithm.Rsa)]
    [InlineData(KeyAlgorithm.EcP256)]
    public void DifferentKeys_DoNotCrossVerify(KeyAlgorithm algorithm)
    {
        using var key = KeyPairGenerator.Generate(algorithm, KeyId);
        using var other = KeyPairGenerator.Generate(algorithm, KeyId);
        var signature = key.Sign(Payload);

        Assert.False(other.Verify(Payload, signature));
    }

    [Fact]
    public void GetPublicJwk_Rsa_ContainsRsaFields()
    {
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        using var doc = JsonDocument.Parse(key.GetPublicJwk());
        var root = doc.RootElement;

        Assert.Equal("RSA", root.GetProperty("kty").GetString());
        Assert.NotNull(root.GetProperty("n").GetString());
        Assert.Equal("AQAB", root.GetProperty("e").GetString()); // 0x010001 == 65537
    }

    [Fact]
    public void GetPublicJwk_Ec_ContainsEcFields()
    {
        using var key = KeyPairGenerator.GenerateEcP256(KeyId);
        using var doc = JsonDocument.Parse(key.GetPublicJwk());
        var root = doc.RootElement;

        Assert.Equal("EC", root.GetProperty("kty").GetString());
        Assert.Equal("P-256", root.GetProperty("crv").GetString());
        Assert.NotNull(root.GetProperty("x").GetString());
        Assert.NotNull(root.GetProperty("y").GetString());
    }

    [Fact]
    public void GetThumbprint_IsStableAndBase64Url()
    {
        using var key = KeyPairGenerator.GenerateEcP256(KeyId);
        var first = key.GetThumbprint();
        var second = key.GetThumbprint();

        Assert.Equal(first, second);
        // RFC 7638 SHA-256 -> 32 bytes -> 43 base64url chars.
        Assert.Equal(43, first.Length);
        Assert.DoesNotContain("=", first);
        Assert.DoesNotContain("+", first);
        Assert.DoesNotContain("/", first);
    }

    [Fact]
    public void Sign_NullData_Throws()
    {
        using var key = KeyPairGenerator.GenerateRsa(KeyId);

        Assert.Throws<ArgumentNullException>(() => key.Sign(null!));
    }

    [Fact]
    public void Verify_NullArgs_Throw()
    {
        using var key = KeyPairGenerator.GenerateRsa(KeyId);

        Assert.Throws<ArgumentNullException>(() => key.Verify(null!, [0x01]));
        Assert.Throws<ArgumentNullException>(() => key.Verify(Payload, null!));
    }

    [Theory]
    [InlineData(KeyAlgorithm.Rsa)]
    [InlineData(KeyAlgorithm.EcP256)]
    public void FromJwk_PublicKey_VerifiesSignatureMadeWithPrivateKey(KeyAlgorithm algorithm)
    {
        using var secret = KeyPairGenerator.Generate(algorithm, KeyId);

        // The server-side path: the actor document carries the JWK; the verifier reconstructs a
        // public-only key from it and must verify a signature made with the private key.
        var jwk = secret.GetPublicJwk();
        using var publicKey = KeyPair.FromJwk(jwk, algorithm, KeyId);

        var signature = secret.Sign(Payload);
        Assert.True(publicKey.Verify(Payload, signature));
    }

    [Theory]
    [InlineData(KeyAlgorithm.Rsa)]
    [InlineData(KeyAlgorithm.EcP256)]
    public void FromJwk_PublicKey_CannotSign(KeyAlgorithm algorithm)
    {
        using var secret = KeyPairGenerator.Generate(algorithm, KeyId);
        using var publicKey = KeyPair.FromJwk(secret.GetPublicJwk(), algorithm, KeyId);

        Assert.ThrowsAny<CryptographicException>(() => publicKey.Sign(Payload));
    }

    [Fact]
    public void FromJwk_MissingMember_Throws()
    {
        Assert.Throws<FormatException>(() =>
            KeyPair.FromJwk("{\"kty\":\"RSA\"}", KeyAlgorithm.Rsa, KeyId));
    }
}
