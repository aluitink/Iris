using System.Text;
using Iris.Core;

namespace Iris.Core.Tests.Signing;

/// <summary>
/// Unit tests for <see cref="HttpSignatureSigner"/> and <see cref="HttpSignatureVerifier"/>:
/// sign/verify round-trip for both profiles, digest correctness, and tamper detection.
/// </summary>
public class HttpSignatureTests
{
    private static readonly Iri Actor = new("https://a.domain.local/u/alice");
    private static readonly Iri KeyId = new("https://a.domain.local/u/alice#main-key");

    private static HttpRequestMetadata PostWithBody(byte[] body, string digest)
        => new(
            method: "POST",
            pathAndQuery: "/u/alice/inbox",
            host: "a.domain.local",
            date: "Tue, 26 Aug 2026 12:00:00 GMT",
            contentType: "application/activity+json",
            body: body,
            headers: new Dictionary<string, string> { ["digest"] = digest });

    private static HttpRequestMetadata GetNoBody()
        => new(
            method: "GET",
            pathAndQuery: "/u/alice",
            host: "a.domain.local",
            date: "Tue, 26 Aug 2026 12:00:00 GMT",
            contentType: null,
            body: [],
            headers: new Dictionary<string, string>());

    [Theory]
    [InlineData(KeyAlgorithm.Rsa, SigningProfile.ClientToServer)]
    [InlineData(KeyAlgorithm.Rsa, SigningProfile.ServerToServer)]
    [InlineData(KeyAlgorithm.EcP256, SigningProfile.ClientToServer)]
    [InlineData(KeyAlgorithm.EcP256, SigningProfile.ServerToServer)]
    public static void SignVerify_RoundTrip_BothAlgorithmsAndProfiles_Succeeds(KeyAlgorithm algorithm, SigningProfile profile)
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.Generate(algorithm, KeyId);
        store.PutKey(key);
        var identity = new SystemIdentity(Actor, KeyId);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);

        var body = Encoding.UTF8.GetBytes("{\"id\":\"https://a.domain.local/a/1\"}");
        var digest = Signatures.ComputeDigest(body);
        var metadata = PostWithBody(body, digest);
        var signatureHeader = signer.Sign(metadata, identity, profile);

        Assert.True(verifier.Verify(metadata, signatureHeader));
    }

    [Fact]
    public void Sign_ClientToServer_Verifies()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);
        var identity = new SystemIdentity(Actor, KeyId);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);

        var metadata = GetNoBody();
        var signatureHeader = signer.Sign(metadata, identity, SigningProfile.ClientToServer);

        Assert.True(verifier.Verify(metadata, signatureHeader));
    }

    [Fact]
    public void Sign_ServerToServer_IncludesDigestAndContentType()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateEcP256(KeyId);
        store.PutKey(key);
        var identity = new SystemIdentity(Actor, KeyId);
        var signer = new HttpSignatureSigner(store);

        var body = Encoding.UTF8.GetBytes("{\"id\":\"1\"}");
        var digest = Signatures.ComputeDigest(body);
        var metadata = PostWithBody(body, digest);

        var signatureHeader = signer.Sign(metadata, identity, SigningProfile.ServerToServer);

        Assert.True(SignatureHeader.TryParse(signatureHeader, out var header));
        Assert.Equal(Signatures.ServerToServerHeaders, header!.Headers);
    }

    [Theory]
    [InlineData(KeyAlgorithm.Rsa)]
    [InlineData(KeyAlgorithm.EcP256)]
    public void Verify_TamperedBody_Fails(KeyAlgorithm algorithm)
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.Generate(algorithm, KeyId);
        store.PutKey(key);
        var identity = new SystemIdentity(Actor, KeyId);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);

        var body = Encoding.UTF8.GetBytes("{\"id\":\"original\"}");
        var digest = Signatures.ComputeDigest(body);
        var metadata = PostWithBody(body, digest);
        var signatureHeader = signer.Sign(metadata, identity, SigningProfile.ServerToServer);

        // The receiver recomputes the digest from the (tampered) body it actually got.
        var tamperedBody = Encoding.UTF8.GetBytes("{\"id\":\"tampered\"}");
        var received = PostWithBody(tamperedBody, Signatures.ComputeDigest(tamperedBody));

        Assert.False(verifier.Verify(received, signatureHeader));
    }

    [Fact]
    public void Verify_TamperedDate_Fails()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);
        var identity = new SystemIdentity(Actor, KeyId);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);

        var metadata = GetNoBody();
        var signatureHeader = signer.Sign(metadata, identity, SigningProfile.ClientToServer);

        var tampered = metadata.With(date: "Tue, 26 Aug 2026 12:00:01 GMT");
        Assert.False(verifier.Verify(tampered, signatureHeader));
    }

    [Fact]
    public void Verify_TamperedHost_Fails()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);
        var identity = new SystemIdentity(Actor, KeyId);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);

        var metadata = GetNoBody();
        var signatureHeader = signer.Sign(metadata, identity, SigningProfile.ClientToServer);

        var tampered = metadata.With(host: "evil.domain.local");
        Assert.False(verifier.Verify(tampered, signatureHeader));
    }

    [Fact]
    public void Verify_UnknownKeyId_Fails()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);
        var identity = new SystemIdentity(Actor, KeyId);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);

        var metadata = GetNoBody();
        var signatureHeader = signer.Sign(metadata, identity, SigningProfile.ClientToServer);

        // The verifier's store does not contain the key, so it cannot resolve it.
        using var emptyStore = new InMemoryKeyStore();
        var emptyVerifier = new HttpSignatureVerifier(emptyStore);

        Assert.False(emptyVerifier.Verify(metadata, signatureHeader));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    public void Verify_MalformedHeader_ReturnsFalse(string header)
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);
        var verifier = new HttpSignatureVerifier(store);

        Assert.False(verifier.Verify(GetNoBody(), header));
    }

    [Fact]
    public void Verify_TamperedSignatureValue_Fails()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);
        var identity = new SystemIdentity(Actor, KeyId);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);

        var metadata = GetNoBody();
        Assert.True(SignatureHeader.TryParse(signer.Sign(metadata, identity, SigningProfile.ClientToServer), out var header));

        // Flip one character of the base64 signature.
        var sig = header!.Signature.ToCharArray();
        sig[0] = sig[0] == 'a' ? 'b' : 'a';
        var tampered = new SignatureHeader(header.KeyId, header.Algorithm, header.Headers, new string(sig)).Format();

        Assert.False(verifier.Verify(metadata, tampered));
    }

    [Fact]
    public void Sign_HeaderUsesCorrectAlgorithmLabel()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateEcP256(KeyId);
        store.PutKey(key);
        var signer = new HttpSignatureSigner(store);
        var identity = new SystemIdentity(Actor, KeyId);

        var header = signer.Sign(GetNoBody(), identity, SigningProfile.ClientToServer);

        Assert.True(SignatureHeader.TryParse(header, out var parsed));
        Assert.Equal("ecdsa-p256-sha256", parsed!.Algorithm);
    }

    [Fact]
    public void Sign_UnknownIdentityKey_ThrowsKeyNotFound()
    {
        using var store = new InMemoryKeyStore();
        var signer = new HttpSignatureSigner(store);
        var identity = new SystemIdentity(Actor, KeyId);

        Assert.Throws<KeyNotFoundException>(() => signer.Sign(GetNoBody(), identity, SigningProfile.ClientToServer));
    }

    [Fact]
    public void CrossProfile_ClientSignature_VerifiesOnServer()
    {
        // The server accepts both profiles: a ClientToServer signature verifies on a server verifier.
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);
        var identity = new SystemIdentity(Actor, KeyId);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);

        var metadata = GetNoBody();
        var clientSignature = signer.Sign(metadata, identity, SigningProfile.ClientToServer);

        Assert.True(verifier.Verify(metadata, clientSignature));
    }
}
