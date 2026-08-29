using System.Security.Cryptography;
using System.Text.Json;
using Iris.Core;

namespace Iris.Core.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="Ed25519Key"/> (the BouncyCastle-backed Ed25519 key, F-05):
/// generation, sign/verify round-trips, PEM (PKIX + PKCS#8) export/import, JWK (RFC 8037)
/// export/import, thumbprint (RFC 7638), and the public-only / private-only distinctions.
/// </summary>
public class Ed25519KeyTests
{
    private static readonly Iri KeyId = new("https://a.domain.local/u/alice#main-key");
    private static readonly byte[] Payload = System.Text.Encoding.UTF8.GetBytes("ed25519 round-trip payload");

    [Fact]
    public void Generate_ReturnsSignableKey()
    {
        var key = Ed25519Key.Generate(KeyId);

        Assert.Equal(KeyAlgorithm.Ed25519, key.Algorithm);
        Assert.Equal(KeyId, key.KeyId);
        Assert.True(key.CanSign);
        Assert.Equal(Ed25519Key.KeySizeBytes, key.GetPublicKeyBytes().Length);
        Assert.NotNull(key.GetPrivateSeedBytes());
        Assert.Equal(Ed25519Key.KeySizeBytes, key.GetPrivateSeedBytes()!.Length);
    }

    [Fact]
    public void SignVerify_RoundTrip()
    {
        var key = Ed25519Key.Generate(KeyId);
        var signature = key.Sign(Payload);

        Assert.Equal(Ed25519Key.SignatureSizeBytes, signature.Length);
        Assert.True(key.Verify(Payload, signature));
    }

    [Fact]
    public void Verify_WrongData_ReturnsFalse()
    {
        var key = Ed25519Key.Generate(KeyId);
        var signature = key.Sign(Payload);

        Assert.False(key.Verify([1, 2, 3], signature));
    }

    [Fact]
    public void Verify_TamperedSignature_ReturnsFalse()
    {
        var key = Ed25519Key.Generate(KeyId);
        var signature = key.Sign(Payload);
        signature[0] ^= 0xFF; // flip one bit

        Assert.False(key.Verify(Payload, signature));
    }

    [Fact]
    public void Verify_MalformedSignature_ReturnsFalse()
    {
        var key = Ed25519Key.Generate(KeyId);

        // A signature of the wrong length is invalid, not an error.
        Assert.False(key.Verify(Payload, [1, 2, 3]));
    }

    [Fact]
    public void FromPublicKey_PublishesPublicOnlyKey()
    {
        var key = Ed25519Key.Generate(KeyId);
        var pubBytes = key.GetPublicKeyBytes();
        var publicOnly = Ed25519Key.FromPublicKey(pubBytes, KeyId);

        Assert.False(publicOnly.CanSign);
        var signature = key.Sign(Payload);
        Assert.True(publicOnly.Verify(Payload, signature));
        Assert.Throws<InvalidOperationException>(() => publicOnly.Sign(Payload));
        Assert.Throws<InvalidOperationException>(() => publicOnly.ExportPrivateKeyPem());
    }

    [Fact]
    public void FromPublicKey_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => Ed25519Key.FromPublicKey([1, 2, 3], KeyId));
    }

    [Fact]
    public void FromPrivateSeed_DerivesMatchingPublicKey()
    {
        var original = Ed25519Key.Generate(KeyId);
        var seed = original.GetPrivateSeedBytes()!;
        var rebuilt = Ed25519Key.FromPrivateSeed(seed, KeyId);

        Assert.Equal(original.GetPublicKeyBytes(), rebuilt.GetPublicKeyBytes());
        var signature = original.Sign(Payload);
        Assert.True(rebuilt.Verify(Payload, signature));
        var rebuiltSig = rebuilt.Sign(Payload);
        Assert.True(original.Verify(Payload, rebuiltSig));
    }

    [Fact]
    public void FromPrivateSeed_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => Ed25519Key.FromPrivateSeed([1, 2, 3], KeyId));
    }

    [Fact]
    public void PublicPem_RoundTrip()
    {
        var key = Ed25519Key.Generate(KeyId);
        var pem = key.ExportPublicKeyPem();

        Assert.StartsWith("-----BEGIN PUBLIC KEY-----\n", pem);
        Assert.Contains("-----END PUBLIC KEY-----", pem);

        var loaded = Ed25519Key.FromPem(pem, KeyId);
        Assert.False(loaded.CanSign);
        Assert.Equal(key.GetPublicKeyBytes(), loaded.GetPublicKeyBytes());
    }

    [Fact]
    public void PrivatePem_RoundTrip()
    {
        var key = Ed25519Key.Generate(KeyId);
        var pem = key.ExportPrivateKeyPem();

        Assert.StartsWith("-----BEGIN PRIVATE KEY-----\n", pem);
        Assert.Contains("-----END PRIVATE KEY-----", pem);

        var loaded = Ed25519Key.FromPem(pem, KeyId);
        Assert.True(loaded.CanSign);
        Assert.Equal(key.GetPrivateSeedBytes(), loaded.GetPrivateSeedBytes());

        // The reloaded key signs and the original verifies (and vice versa).
        var signature = loaded.Sign(Payload);
        Assert.True(key.Verify(Payload, signature));
    }

    [Fact]
    public void FromPem_Invalid_Throws()
    {
        Assert.Throws<FormatException>(() => Ed25519Key.FromPem("not a pem", KeyId));
    }

    [Fact]
    public void Jwk_RoundTrip()
    {
        var key = Ed25519Key.Generate(KeyId);
        var jwk = key.GetPublicJwk();

        // RFC 8037 shape: kty=OKP, crv=Ed25519, x=base64url(32 bytes).
        var element = JsonDocument.Parse(jwk).RootElement;
        Assert.Equal("OKP", element.GetProperty("kty").GetString());
        Assert.Equal("Ed25519", element.GetProperty("crv").GetString());

        var loaded = Ed25519Key.FromJwk(jwk, KeyId);
        Assert.False(loaded.CanSign);
        Assert.Equal(key.GetPublicKeyBytes(), loaded.GetPublicKeyBytes());

        var signature = key.Sign(Payload);
        Assert.True(loaded.Verify(Payload, signature));
    }

    [Fact]
    public void Jwk_MissingX_Throws()
    {
        Assert.Throws<FormatException>(() => Ed25519Key.FromJwk("{\"kty\":\"OKP\",\"crv\":\"Ed25519\"}", KeyId));
    }

    [Fact]
    public void GetThumbprint_IsStableAndBase64Url()
    {
        var key = Ed25519Key.Generate(KeyId);
        var first = key.GetThumbprint();
        var second = key.GetThumbprint();

        Assert.Equal(first, second);

        // RFC 7638: base64url-encoded SHA-256 → 43 base64url chars, no padding.
        Assert.Equal(43, first.Length);
        Assert.DoesNotContain("=", first);
        Assert.DoesNotContain("+", first);
        Assert.DoesNotContain("/", first);
    }

    [Fact]
    public void GetThumbprint_DiffersAcrossKeys()
    {
        var a = Ed25519Key.Generate(KeyId);
        var b = Ed25519Key.Generate(KeyId);

        Assert.NotEqual(a.GetThumbprint(), b.GetThumbprint());
    }

    [Fact]
    public void ImplementsISigningKey()
    {
        ISigningKey key = Ed25519Key.Generate(KeyId);

        Assert.Equal(KeyAlgorithm.Ed25519, key.Algorithm);
        Assert.Equal(KeyId, key.KeyId);
        var signature = key.Sign(Payload);
        Assert.True(key.Verify(Payload, signature));
        Assert.Equal(key.GetPublicJwk(), ((Ed25519Key)key).GetPublicJwk());
    }

    [Fact]
    public void KeyPem_LoadSave_RoundTrip()
    {
        var key = Ed25519Key.Generate(KeyId);
        var pem = KeyPem.Save(key);
        var loaded = KeyPem.Load(pem, KeyAlgorithm.Ed25519, KeyId);

        Assert.IsType<Ed25519Key>(loaded);
        Assert.Equal(key.GetPrivateSeedBytes(), ((Ed25519Key)loaded).GetPrivateSeedBytes());
    }

    [Fact]
    public void Signatures_AlgorithmLabel_IsEd25519()
    {
        Assert.Equal("ed25519", Signatures.AlgorithmLabel(KeyAlgorithm.Ed25519));
    }

    [Fact]
    public void SignVerify_ViaSignaturesHelpers()
    {
        var key = Ed25519Key.Generate(KeyId);
        var signature = Signatures.SignBase(key, Payload);
        Assert.True(Signatures.VerifyBase(key, Payload, signature));
    }
}
