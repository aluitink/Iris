using System.Security.Cryptography;
using Iris.Core;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for <see cref="KeyPem"/> PKCS#8 PEM load/save round-trips.
/// </summary>
public class KeyPemTests
{
    private static readonly Iri KeyId = new("https://a.domain.local/u/alice#main-key");
    private static readonly byte[] Payload = System.Text.Encoding.UTF8.GetBytes("pem round-trip payload");

    [Theory]
    [InlineData(KeyAlgorithm.Rsa)]
    [InlineData(KeyAlgorithm.EcP256)]
    public void SaveLoad_RoundTrip_PreservesKeyAndSigning(KeyAlgorithm algorithm)
    {
        using var original = KeyPairGenerator.Generate(algorithm, KeyId);
        var signature = original.Sign(Payload);

        var pem = KeyPem.Save(original);
        var loaded = KeyPem.Load(pem, algorithm, KeyId);
        Assert.IsType<KeyPair>(loaded);
        using var loadedKeyPair = (KeyPair)loaded;

        // Same algorithm, key id, and public key material.
        Assert.Equal(algorithm, loaded.Algorithm);
        Assert.Equal(KeyId, loaded.KeyId);
        Assert.Equal(original.Key.ExportSubjectPublicKeyInfo(), loadedKeyPair.Key.ExportSubjectPublicKeyInfo());

        // The loaded key verifies a signature made by the original (and vice versa).
        Assert.True(loaded.Verify(Payload, signature));
        var loadedSig = loaded.Sign(Payload);
        Assert.True(original.Verify(Payload, loadedSig));
    }

    [Theory]
    [InlineData(KeyAlgorithm.Rsa)]
    [InlineData(KeyAlgorithm.EcP256)]
    public void Save_ProducesPkcs8Pem(KeyAlgorithm algorithm)
    {
        using var key = KeyPairGenerator.Generate(algorithm, KeyId);

        var pem = KeyPem.Save(key);

        Assert.StartsWith("-----BEGIN PRIVATE KEY-----\n", pem);
        Assert.Contains("-----END PRIVATE KEY-----", pem);
    }

    [Fact]
    public void Load_Pkcs1RsaPublicPem_LoadsVerifyingKey()
    {
        // A real-world wire form (e.g. Rayven): publicKeyPem carries a raw RSA public key
        // (-----BEGIN RSA PUBLIC KEY-----, PKCS#1), not a PKIX public key.
        using var original = KeyPairGenerator.GenerateRsa(KeyId);
        var pkcs1Pem = original.Key is RSA rsa
            ? $"-----BEGIN RSA PUBLIC KEY-----\n{Convert.ToBase64String(rsa.ExportRSAPublicKey(), Base64FormattingOptions.InsertLineBreaks)}\n-----END RSA PUBLIC KEY-----\n"
            : throw new InvalidOperationException("expected an RSA key");

        var signature = original.Sign(Payload);
        var loaded = KeyPem.Load(pkcs1Pem, KeyAlgorithm.Rsa, KeyId);
        Assert.IsType<KeyPair>(loaded);
        using var loadedKeyPair = (KeyPair)loaded;

        // Same public key material; the loaded (public-only) key verifies the original's signature.
        Assert.Equal(rsa.ExportSubjectPublicKeyInfo(), ((RSA)loadedKeyPair.Key).ExportSubjectPublicKeyInfo());
        Assert.True(loaded.Verify(Payload, signature));
    }

    [Fact]
    public void Load_PkixRsaPublicPem_LoadsVerifyingKey()
    {
        using var original = KeyPairGenerator.GenerateRsa(KeyId);
        var signature = original.Sign(Payload);

        var loaded = KeyPem.Load(original.ExportPublicKeyPem(), KeyAlgorithm.Rsa, KeyId);
        Assert.IsType<KeyPair>(loaded);
        using var loadedKeyPair = (KeyPair)loaded;

        Assert.True(loaded.Verify(Payload, signature));
    }

    [Fact]
    public void Load_InvalidPem_Throws()
    {
        // ImportFromPem rejects non-PEM input with an ArgumentException.
        Assert.Throws<ArgumentException>(() => KeyPem.Load("not a pem", KeyAlgorithm.Rsa, KeyId));
    }

    [Fact]
    public void Save_NullKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => KeyPem.Save(null!));
    }
}
