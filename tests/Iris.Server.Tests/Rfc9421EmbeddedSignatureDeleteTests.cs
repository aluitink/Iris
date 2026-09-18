using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Core;
using Iris.Core.Signing;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 140.1 tests: Deletes with embedded signatures. Mastodon 4.5+ signs Delete activities with an
/// RFC 9421 HTTP header signature AND a body-embedded W3C <c>RsaSignature2017</c> proof (the actor's
/// own key). When the actor is deleted, its HTTP endpoint returns 410 Gone, so the RFC 9421 header
/// key can no longer be resolved (or its header signature no longer verifies against a live key).
/// The body-embedded proof is what survives. The RFC 9421 validation path must fall back to verifying
/// the embedded proof — the same fallback the legacy draft-cavage-03 path already had — otherwise a
/// legitimate Delete from a deleted actor is rejected and the local tombstone is never applied.
/// </summary>
public sealed class Rfc9421EmbeddedSignatureDeleteTests
{
    private const string ActorIri = "https://gone.example.org/actors/deleted";
    private const string CreatorKeyIri = "https://gone.example.org/actors/deleted#key-1";
    // A different key IRI for the RFC 9421 header signature (simulating the actor's header-signing key
    // that is now 410 Gone / unresolvable).
    private const string HeaderKeyIri = "https://gone.example.org/actors/deleted#header-key-1";

    /// <summary>
    /// A fake <see cref="IInboundKeyResolver"/>: returns null for the RFC 9421 header key IRI (the
    /// actor is 410 Gone) but resolves the embedded proof's creator key IRI to the given key.
    /// </summary>
    private sealed class FallbackKeyResolver(ISigningKey creatorKey) : IInboundKeyResolver
    {
        public Task<ISigningKey?> ResolveAsync(Iri keyId, CancellationToken ct = default)
        {
            var result = keyId.Value == CreatorKeyIri ? creatorKey : null;
            return Task.FromResult(result);
        }
    }

    private static HttpContext BuildHttpContext(byte[] body, string signatureHeader, string signatureInput)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/ap/v1/u/bob/inbox";
        context.Request.Headers["Signature"] = signatureHeader;
        context.Request.Headers["Signature-Input"] = signatureInput;
        context.Request.Body = new MemoryStream(body);
        return context;
    }

    private static byte[] BuildDeleteBodyWithEmbeddedProof(KeyPair key, string actorIri, string creatorKeyIri)
    {
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

        var options = new Dictionary<string, object>
        {
            ["@context"] = "https://w3id.org/identity/v1",
            ["type"] = "RsaSignature2017",
            ["creator"] = creatorKeyIri,
            ["created"] = "2026-09-17T12:00:00Z",
            ["expires"] = "2099-01-01T00:00:00Z",
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
            ["creator"] = creatorKeyIri,
            ["created"] = "2026-09-17T12:00:00Z",
            ["expires"] = "2099-01-01T00:00:00Z",
            ["signatureValue"] = Convert.ToBase64String(signature),
        };

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(activity));
    }

    private static HttpSignatureValidator BuildValidator(
        IInboundKeyResolver resolver, IPersistenceProvider persistence)
    {
        return new HttpSignatureValidator(
            resolver,
            new HttpSignatureVerifier(new InMemoryKeyStore()),
            remoteKeyCache: null,
            remoteActorCache: null,
            logger: NullLogger<HttpSignatureValidator>.Instance,
            persistence: persistence);
    }

    private static IPersistenceProvider SeedActorPersistence()
    {
        var persistence = new InMemoryPersistenceProvider();
        persistence.ActorStore.PutActorAsync(new KristofferStrube.ActivityStreams.Person
        {
            Id = ActorIri,
            PreferredUsername = "deleted",
            Name = ["deleted"],
        }).GetAwaiter().GetResult();
        return persistence;
    }

    private const string Rfc9421SignatureInput =
        $"sig1=(\"date\" \"@method\" \"@authority\" \"@path\");created=1758182400;keyid=\"{HeaderKeyIri}\"";

    // --- 1. RFC 9421 header key unresolvable + valid embedded proof -> accepted ------------

    [Fact]
    public async Task Rfc9421_UnresolvableHeaderKey_ValidEmbeddedProof_IsAccepted()
    {
        var keyId = new Iri(CreatorKeyIri);
        var key = KeyPairGenerator.GenerateRsa(keyId);
        var body = BuildDeleteBodyWithEmbeddedProof(key, ActorIri, CreatorKeyIri);
        var context = BuildHttpContext(
            body,
            signatureHeader: "sig1=:dGVzdA==:", // header signature value is irrelevant (key is unresolvable).
            signatureInput: Rfc9421SignatureInput);

        var validator = BuildValidator(new FallbackKeyResolver(key), SeedActorPersistence());
        var result = await validator.ValidateAsync(context);

        Assert.NotNull(result);
        Assert.True(
            result!.IsValid,
            "a Delete whose RFC 9421 header key is unresolvable (actor 410 Gone) but carries a valid " +
            "embedded RsaSignature2017 proof must be accepted via the embedded-proof fallback.");
    }

    // --- 2. RFC 9421 header key unresolvable + NO embedded proof -> rejected ---------------

    [Fact]
    public async Task Rfc9421_UnresolvableHeaderKey_NoEmbeddedProof_IsRejected()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {"@context":"https://www.w3.org/ns/activitystreams","type":"Delete","id":"https://gone.example.org/actors/deleted#delete","actor":"https://gone.example.org/actors/deleted","object":"https://gone.example.org/actors/deleted"}
            """);
        var context = BuildHttpContext(
            body,
            signatureHeader: "sig1=:dGVzdA==:",
            signatureInput: Rfc9421SignatureInput);

        // The resolver returns null for both the header key and (there is) no creator key.
        var validator = BuildValidator(
            new FallbackKeyResolver(null!), SeedActorPersistence());
        var result = await validator.ValidateAsync(context);

        Assert.NotNull(result);
        Assert.False(
            result!.IsValid,
            "a Delete whose RFC 9421 header key is unresolvable and has NO embedded proof must be rejected.");
    }

    // --- 3. RFC 9421 header key unresolvable + TAMPERED embedded proof -> rejected ---------

    [Fact]
    public async Task Rfc9421_UnresolvableHeaderKey_TamperedEmbeddedProof_IsRejected()
    {
        var keyId = new Iri(CreatorKeyIri);
        var key = KeyPairGenerator.GenerateRsa(keyId);
        var body = BuildDeleteBodyWithEmbeddedProof(key, ActorIri, CreatorKeyIri);
        // Tamper with the body after signing (change the object IRI) so the embedded proof no longer
        // verifies.
        var tamperedText = Encoding.UTF8.GetString(body).Replace("deleted#delete", "evil#delete");
        var context = BuildHttpContext(
            Encoding.UTF8.GetBytes(tamperedText),
            signatureHeader: "sig1=:dGVzdA==:",
            signatureInput: Rfc9421SignatureInput);

        var validator = BuildValidator(new FallbackKeyResolver(key), SeedActorPersistence());
        var result = await validator.ValidateAsync(context);

        Assert.NotNull(result);
        Assert.False(
            result!.IsValid,
            "a Delete whose RFC 9421 header key is unresolvable and whose embedded proof is tampered " +
            "must be rejected (the fallback must not accept a bad proof).");
    }

    // --- 4. RFC 9421 header key resolvable but signature fails + valid embedded proof ------
    //       -> accepted (the second fallback call site: the cryptographic-verification-failed path).

    [Fact]
    public async Task Rfc9421_HeaderSignatureFails_ValidEmbeddedProof_IsAccepted()
    {
        var headerKeyId = new Iri(HeaderKeyIri);
        var headerKey = KeyPairGenerator.GenerateRsa(headerKeyId); // resolvable, but its signature is bogus.
        var creatorKeyIriIri = new Iri(CreatorKeyIri);
        var creatorKey = KeyPairGenerator.GenerateRsa(creatorKeyIriIri);
        var body = BuildDeleteBodyWithEmbeddedProof(creatorKey, ActorIri, CreatorKeyIri);

        // A resolver that resolves BOTH the header key and the creator key. The header key resolves
        // (so the primary path proceeds to cryptographic verification), but the RFC 9421 Signature
        // header carries a bogus base64 ("dGVzdA==") that does not verify against headerKey -> the
        // verification-failed fallback must then verify the embedded proof.
        var resolver = new DualKeyResolver(headerKey, creatorKey);
        var context = BuildHttpContext(
            body,
            signatureHeader: "sig1=:dGVzdA==:", // bogus header signature -> verification fails.
            signatureInput: Rfc9421SignatureInput);

        var validator = BuildValidator(resolver, SeedActorPersistence());
        var result = await validator.ValidateAsync(context);

        Assert.NotNull(result);
        Assert.True(
            result!.IsValid,
            "a Delete whose RFC 9421 header key resolves but whose header signature fails " +
            "cryptographic verification must still be accepted via the embedded-proof fallback.");
    }

    /// <summary>
    /// A resolver that returns a distinct key for each of the two key IRIs (the RFC 9421 header key
    /// and the embedded proof's creator key).
    /// </summary>
    private sealed class DualKeyResolver(ISigningKey headerKey, ISigningKey creatorKey) : IInboundKeyResolver
    {
        public Task<ISigningKey?> ResolveAsync(Iri keyId, CancellationToken ct = default)
        {
            ISigningKey? result = keyId.Value switch
            {
                HeaderKeyIri => headerKey,
                CreatorKeyIri => creatorKey,
                _ => null,
            };
            return Task.FromResult(result);
        }
    }
}
