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
        using var loaded = KeyPem.Load(pem, algorithm, KeyId);

        // Same algorithm, key id, and public key material.
        Assert.Equal(algorithm, loaded.Algorithm);
        Assert.Equal(KeyId, loaded.KeyId);
        Assert.Equal(original.Key.ExportSubjectPublicKeyInfo(), loaded.Key.ExportSubjectPublicKeyInfo());

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
