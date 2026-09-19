using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Core.Identity;

namespace Iris.Core.Tests.Signing;

/// <summary>
/// Tests for the W3C Linked Data <c>RsaSignature2017</c> embedded proof verifier
/// (<see cref="EmbeddedSignatureVerifier"/>). This is the body-embedded signature format that
/// Mastodon uses for Delete/tombstone deliveries — the actor's own RSA key signs the activity
/// body directly, and the proof is carried in the <c>signature</c> field of the JSON.
/// </summary>
public class EmbeddedSignatureTests
{
    /// <summary>
    /// Builds a Delete activity with an embedded RsaSignature2017 proof, signed with the given RSA key.
    /// Returns the body bytes and the creator (key IRI).
    /// </summary>
    private static (byte[] Body, string Creator) BuildSignedDelete(
        KeyPair key,
        string actorIri,
        string keyIri)
    {
        // The activity body (without the signature field).
        var activity = new Dictionary<string, object>
        {
            ["@context"] = new[]
            {
                "https://www.w3.org/ns/activitystreams",
                "https://w3id.org/security/v1",
            },
            ["id"] = actorIri + "#delete",
            ["type"] = "Delete",
            ["actor"] = actorIri,
            ["object"] = actorIri,
            ["to"] = new[] { "https://www.w3.org/ns/activitystreams#Public" },
        };

        // The signature options (without signatureValue). The expires date is far in the future
        // so the test does not break when the current time passes a hardcoded expiration.
        var options = new Dictionary<string, object>
        {
            ["@context"] = "https://w3id.org/identity/v1",
            ["type"] = "RsaSignature2017",
            ["creator"] = keyIri,
            ["created"] = "2026-09-17T12:00:00Z",
            ["expires"] = "2099-01-01T00:00:00Z",
        };

        // Serialize the document (activity without signature) and the options.
        var documentJson = JsonSerializer.Serialize(activity);
        var optionsJson = JsonSerializer.Serialize(options);

        // Compute the signature base: SHA256(options) || SHA256(document).
        var optionsHash = SHA256.HashData(Encoding.UTF8.GetBytes(optionsJson));
        var documentHash = SHA256.HashData(Encoding.UTF8.GetBytes(documentJson));
        var baseToSign = new byte[optionsHash.Length + documentHash.Length];
        Buffer.BlockCopy(optionsHash, 0, baseToSign, 0, optionsHash.Length);
        Buffer.BlockCopy(documentHash, 0, baseToSign, optionsHash.Length, documentHash.Length);

        // Sign with RSA-SHA256.
        var signature = key.Sign(baseToSign);
        var signatureB64 = Convert.ToBase64String(signature);

        // Add the signature to the activity.
        activity["signature"] = new Dictionary<string, object>
        {
            ["@context"] = "https://w3id.org/identity/v1",
            ["type"] = "RsaSignature2017",
            ["creator"] = keyIri,
            ["created"] = "2026-09-17T12:00:00Z",
            ["expires"] = "2099-01-01T00:00:00Z",
            ["signatureValue"] = signatureB64,
        };

        var bodyJson = JsonSerializer.Serialize(activity);
        return (Encoding.UTF8.GetBytes(bodyJson), keyIri);
    }

    [Fact]
    public void Verify_ValidEmbeddedProof_ReturnsTrue()
    {
        var keyId = new Iri("https://remote.example.org/actors/alice#main-key");
        var key = KeyPairGenerator.GenerateRsa(keyId);
        var (body, creator) = BuildSignedDelete(key, "https://remote.example.org/actors/alice", keyId.Value);

        Assert.True(EmbeddedSignatureVerifier.HasEmbeddedProof(body));
        Assert.Equal(keyId.Value, EmbeddedSignatureVerifier.ExtractCreator(body));
        Assert.True(EmbeddedSignatureVerifier.Verify(body, key));
    }

    [Fact]
    public void Verify_TamperedBody_ReturnsFalse()
    {
        var keyId = new Iri("https://remote.example.org/actors/alice#main-key");
        var key = KeyPairGenerator.GenerateRsa(keyId);
        var (body, _) = BuildSignedDelete(key, "https://remote.example.org/actors/alice", keyId.Value);

        // Tamper with the body (change the actor IRI in the id field).
        var bodyText = Encoding.UTF8.GetString(body);
        var tamperedText = bodyText.Replace("alice#delete", "evil#delete");
        var tampered = Encoding.UTF8.GetBytes(tamperedText);

        Assert.False(EmbeddedSignatureVerifier.Verify(tampered, key));
    }

    [Fact]
    public void Verify_WrongKey_ReturnsFalse()
    {
        var keyId1 = new Iri("https://remote.example.org/actors/alice#main-key");
        var keyId2 = new Iri("https://remote.example.org/actors/bob#main-key");
        var key1 = KeyPairGenerator.GenerateRsa(keyId1);
        var key2 = KeyPairGenerator.GenerateRsa(keyId2);
        var (body, _) = BuildSignedDelete(key1, "https://remote.example.org/actors/alice", keyId1.Value);

        // Verify with the wrong key.
        Assert.False(EmbeddedSignatureVerifier.Verify(body, key2));
    }

    [Fact]
    public void Verify_NoSignatureField_ReturnsFalse()
    {
        var keyId = new Iri("https://remote.example.org/actors/alice#main-key");
        var key = KeyPairGenerator.GenerateRsa(keyId);

        var body = Encoding.UTF8.GetBytes(
            """
            {"@context":"https://www.w3.org/ns/activitystreams","type":"Delete","id":"https://remote.example.org/actors/alice#delete","actor":"https://remote.example.org/actors/alice","object":"https://remote.example.org/actors/alice"}
            """);

        Assert.False(EmbeddedSignatureVerifier.HasEmbeddedProof(body));
        Assert.False(EmbeddedSignatureVerifier.Verify(body, key));
    }

    [Fact]
    public void Verify_WrongType_ReturnsFalse()
    {
        var keyId = new Iri("https://remote.example.org/actors/alice#main-key");
        var key = KeyPairGenerator.GenerateRsa(keyId);

        var body = Encoding.UTF8.GetBytes(
            """
            {"@context":"https://www.w3.org/ns/activitystreams","type":"Delete","signature":{"type":"Ed25519Signature2020","creator":"https://remote.example.org/actors/alice#main-key","signatureValue":"dGVzdA=="}}
            """);

        Assert.False(EmbeddedSignatureVerifier.HasEmbeddedProof(body));
        Assert.False(EmbeddedSignatureVerifier.Verify(body, key));
    }

    [Fact]
    public void Verify_ExpiredProof_ReturnsFalse()
    {
        var keyId = new Iri("https://remote.example.org/actors/alice#main-key");
        var key = KeyPairGenerator.GenerateRsa(keyId);

        // Build a proof with an expired "expires" field.
        var activity = new Dictionary<string, object>
        {
            ["@context"] = new[] { "https://www.w3.org/ns/activitystreams", "https://w3id.org/security/v1" },
            ["id"] = "https://remote.example.org/actors/alice#delete",
            ["type"] = "Delete",
            ["actor"] = "https://remote.example.org/actors/alice",
            ["object"] = "https://remote.example.org/actors/alice",
        };

        var options = new Dictionary<string, object>
        {
            ["@context"] = "https://w3id.org/identity/v1",
            ["type"] = "RsaSignature2017",
            ["creator"] = keyId.Value,
            ["created"] = "2020-01-01T00:00:00Z",
            ["expires"] = "2020-01-02T00:00:00Z", // Expired.
        };

        var documentJson = JsonSerializer.Serialize(activity);
        var optionsJson = JsonSerializer.Serialize(options);
        var optionsHash = SHA256.HashData(Encoding.UTF8.GetBytes(optionsJson));
        var documentHash = SHA256.HashData(Encoding.UTF8.GetBytes(documentJson));
        var baseToSign = new byte[optionsHash.Length + documentHash.Length];
        Buffer.BlockCopy(optionsHash, 0, baseToSign, 0, optionsHash.Length);
        Buffer.BlockCopy(documentHash, 0, baseToSign, optionsHash.Length, documentHash.Length);

        var signature = key.Sign(baseToSign);

        activity["signature"] = new Dictionary<string, object>
        {
            ["@context"] = "https://w3id.org/identity/v1",
            ["type"] = "RsaSignature2017",
            ["creator"] = keyId.Value,
            ["created"] = "2020-01-01T00:00:00Z",
            ["expires"] = "2020-01-02T00:00:00Z",
            ["signatureValue"] = Convert.ToBase64String(signature),
        };

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(activity));

        Assert.True(EmbeddedSignatureVerifier.HasEmbeddedProof(body));
        Assert.False(EmbeddedSignatureVerifier.Verify(body, key));
    }

    [Fact]
    public void Verify_MalformedJson_ReturnsFalse()
    {
        var keyId = new Iri("https://remote.example.org/actors/alice#main-key");
        var key = KeyPairGenerator.GenerateRsa(keyId);

        var body = Encoding.UTF8.GetBytes("not valid json");
        Assert.False(EmbeddedSignatureVerifier.Verify(body, key));
    }

    [Fact]
    public void ExtractCreator_NoProof_ReturnsNull()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {"@context":"https://www.w3.org/ns/activitystreams","type":"Delete"}
            """);

        Assert.Null(EmbeddedSignatureVerifier.ExtractCreator(body));
    }
}
