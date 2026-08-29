using System.Security.Cryptography;
using Iris.Core;

namespace Iris.Core.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="KeyPairGenerator"/> key generation (RSA-2048 and EC P-256).
/// </summary>
public class KeyPairGeneratorTests
{
    private static readonly Iri KeyId = new("https://a.domain.local/u/alice#main-key");

    [Fact]
    public void GenerateRsa_CreatesRsaKeyPair()
    {
        using var key = KeyPairGenerator.GenerateRsa(KeyId);

        Assert.Equal(KeyAlgorithm.Rsa, key.Algorithm);
        Assert.Equal(KeyId, key.KeyId);
        Assert.IsType<RSA>(key.Key, exactMatch: false);
        Assert.Equal(KeyPairGenerator.RsaKeySizeBits, ((RSA)key.Key).KeySize);
    }

    [Fact]
    public void GenerateEcP256_CreatesEcKeyPair()
    {
        using var key = KeyPairGenerator.GenerateEcP256(KeyId);

        Assert.Equal(KeyAlgorithm.EcP256, key.Algorithm);
        Assert.Equal(KeyId, key.KeyId);
        Assert.IsType<ECDsa>(key.Key, exactMatch: false);
        Assert.Equal(256, ((ECDsa)key.Key).KeySize);
    }

    [Fact]
    public void Generate_TwoKeysAreDifferent()
    {
        using var a = KeyPairGenerator.GenerateRsa(KeyId);
        using var b = KeyPairGenerator.GenerateRsa(KeyId);

        // Distinct key material: the public-key encodings must differ.
        Assert.NotEqual(a.Key.ExportSubjectPublicKeyInfo(), b.Key.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void Generate_UnsupportedAlgorithm_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyPairGenerator.Generate((KeyAlgorithm)99, KeyId));
    }
}
