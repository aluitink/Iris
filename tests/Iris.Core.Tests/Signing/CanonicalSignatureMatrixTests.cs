using System.Text;

namespace Iris.Core.Tests.Signing;

/// <summary>
/// Phase 136.3 — the <strong>canonical verification matrix</strong>: the acceptance / rejection
/// contract for <see cref="HttpSignatureVerifier"/> against every <c>Signature</c> header shape a real
/// Fediverse peer emits. This is the wire-level matrix the 135.1b(4) Lemmy blocker asked for
/// ("exit when valid signatures pass consistently and invalid signatures fail deterministically").
/// </summary>
/// <remarks>
/// <para>
/// <strong>Accept side.</strong> The verifier reconstructs the signature base from the declared
/// <c>headers</c> component list and verifies cryptographically, so it accepts any well-formed
/// draft-cavage-03 header whose component list matches the request — independent of the <em>order</em>
/// of the components, the presence/absence of the optional <c>created</c> parameter, and the
/// <c>keyId</c> fragment convention. The two cases that matter for cross-platform interop are called
/// out explicitly: (a) a <c>keyId</c> with the <c>#main-key</c> fragment (Lemmy's convention, distinct
/// from Iris's <c>#key-1</c>) and (b) a header with a <c>created</c> timestamp. Both verify, proving
/// Iris does not reject a peer simply because it uses a different (still-valid) keyId fragment or
/// includes the optional created parameter.
/// </para>
/// <para>
/// <strong>Reject side.</strong> Deterministic failures for: a malformed header (unparseable), a
/// cryptographic mismatch (the signature was made over a different base), an unparseable <c>keyId</c>,
/// an empty component list, and a signature value that is not valid base64.
/// </para>
/// </remarks>
public class CanonicalSignatureMatrixTests
{
    private static readonly Iri Actor = new("https://a.domain.local/u/alice");
    private static readonly Iri KeyId = new("https://a.domain.local/u/alice#main-key");

    private static readonly byte[] Body =
        Encoding.UTF8.GetBytes("{\"id\":\"https://a.domain.local/a/1\",\"type\":\"Follow\"}");

    private static string Digest => Signatures.ComputeDigest(Body);

    /// <summary>
    /// A body-carrying POST (the ServerToServer profile's base: request-target, host, date, digest,
    /// content-type).
    /// </summary>
    private static HttpRequestMetadata PostWithBody()
        => new(
            method: "POST",
            pathAndQuery: "/u/alice/inbox",
            host: "a.domain.local",
            date: "Tue, 26 Aug 2026 12:00:00 GMT",
            contentType: "application/activity+json",
            body: Body,
            headers: new Dictionary<string, string> { ["digest"] = Digest });

    /// <summary>
    /// A bodyless GET (the ClientToServer profile's base: request-target, host, date).
    /// </summary>
    private static HttpRequestMetadata GetNoBody()
        => new(
            method: "GET",
            pathAndQuery: "/u/alice",
            host: "a.domain.local",
            date: "Tue, 26 Aug 2026 12:00:00 GMT",
            contentType: null,
            body: [],
            headers: new Dictionary<string, string>());

    // --- Accept: valid signatures in the shapes real peers emit --------------------

    [Theory]
    [InlineData(KeyAlgorithm.Rsa, SigningProfile.ClientToServer)]
    [InlineData(KeyAlgorithm.Rsa, SigningProfile.ServerToServer)]
    [InlineData(KeyAlgorithm.EcP256, SigningProfile.ClientToServer)]
    [InlineData(KeyAlgorithm.EcP256, SigningProfile.ServerToServer)]
    public static void Accept_WellFormedHeader_BothAlgorithmsAndProfiles_Verifies(KeyAlgorithm algorithm, SigningProfile profile)
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.Generate(algorithm, KeyId);
        store.PutKey(key);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);
        var identity = new SystemIdentity(Actor, KeyId);

        var metadata = profile == SigningProfile.ServerToServer ? PostWithBody() : GetNoBody();
        var header = signer.Sign(metadata, identity, profile);

        Assert.True(verifier.Verify(metadata, header),
            $"a well-formed signature must verify for {algorithm}/{profile}");
    }

    [Fact]
    public void Accept_LemmyStyleMainKeyFragment_Verifies()
    {
        // Lemmy advertises its public key under the "#main-key" fragment (e.g.
        // https://lemmy.luit.ink/c/interop#main-key), not Iris's "#key-1" convention. A peer that signs
        // with such a keyId must verify: the verifier resolves the key by the exact keyId (the fragment
        // is part of the IRI) and is agnostic to which fragment the peer chose. This is the wire-level
        // proof that Iris accepts Lemmy-shaped keyIds — the interop property the 135.1b(4) blocker
        // required.
        var lemmyKeyId = new Iri("https://lemmy.luit.ink/c/interop#main-key");
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(lemmyKeyId);
        store.PutKey(key);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);
        var identity = new SystemIdentity(new Iri("https://lemmy.luit.ink/c/interop"), lemmyKeyId);

        var metadata = PostWithBody();
        var header = signer.Sign(metadata, identity, SigningProfile.ServerToServer);

        Assert.True(verifier.Verify(metadata, header),
            "a signature whose keyId uses Lemmy's #main-key fragment must verify");
    }

    [Fact]
    public void Accept_WithCreatedParameter_Verifies()
    {
        // A peer may include the optional draft-cavage-03 "created" parameter. Its presence must not
        // affect verification (the verifier ignores it — it is not part of the signature base).
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);
        var identity = new SystemIdentity(Actor, KeyId);

        var metadata = GetNoBody();
        Assert.True(SignatureHeader.TryParse(signer.Sign(metadata, identity, SigningProfile.ClientToServer), out var parsed));
        Assert.NotNull(parsed);

        // The default signer already emits a created (derived from the signed date); re-emit the header
        // with an explicit, different created value to prove the verifier does not bind to it.
        var withCreated = new SignatureHeader(parsed!.KeyId, parsed.Algorithm, parsed.Headers, parsed.Signature, 1789000000).Format();

        Assert.True(verifier.Verify(metadata, withCreated),
            "a signature with a (different) created parameter must still verify");
    }

    [Fact]
    public void Accept_ComponentOrderIsIrrelevant_Verifies()
    {
        // The signature base is built in the ORDER the components are declared in the header. A peer
        // that declares the same component set in a different order signs over a different byte string,
        // so the verifier (which reconstructs in the header's declared order) still verifies. This is
        // the canonicalization edge case "header ordering" — the verifier must honor the peer's order,
        // not impose its own.
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);

        var metadata = PostWithBody();

        // Iris's ServerToServer order: (request-target) host date digest content-type.
        var irisOrder = Signatures.ServerToServerHeaders;
        // A peer's (equally valid) reordered list: digest first, content-type second.
        const string PeerOrder = "digest content-type (request-target) host date";

        var baseIris = Signatures.BuildSignatureBase(metadata, irisOrder.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var sigIris = key.Sign(baseIris);
        var headerIris = new SignatureHeader(KeyId.Value, Signatures.AlgorithmLabel(key.Algorithm), irisOrder, Convert.ToBase64String(sigIris)).Format();

        var basePeer = Signatures.BuildSignatureBase(metadata, PeerOrder.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var sigPeer = key.Sign(basePeer);
        var headerPeer = new SignatureHeader(KeyId.Value, Signatures.AlgorithmLabel(key.Algorithm), PeerOrder, Convert.ToBase64String(sigPeer)).Format();

        Assert.True(new HttpSignatureVerifier(store).Verify(metadata, headerIris),
            "the Iris-ordered component list must verify");
        Assert.True(new HttpSignatureVerifier(store).Verify(metadata, headerPeer),
            "a peer's reordered component list must verify (the verifier honors the peer's order)");
    }

    // --- Reject: deterministic failures -------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("keyId=\"https://a.domain.local/u/alice#main-key\", algorithm=\"rsa-sha256\", headers=\"(request-target) host date\"")]
    public static void Reject_MalformedHeader_ReturnsFalse(string header)
    {
        // A missing/empty/unparseable header, or one missing a required parameter (here: no signature),
        // is rejected deterministically.
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);
        var verifier = new HttpSignatureVerifier(store);

        Assert.False(verifier.Verify(GetNoBody(), header),
            $"malformed header must be rejected: '{header}'");
    }

    [Fact]
    public void Reject_WrongKey_SignatureDoesNotVerify()
    {
        // A signature made with a different key than the one the verifier holds does not verify — the
        // cryptographic check is authoritative (a keyId that resolves to the wrong key is rejected).
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);

        // A DIFFERENT key (same keyId) signs the header.
        using var otherKey = KeyPairGenerator.GenerateRsa(KeyId);
        var metadata = GetNoBody();
        var baseBytes = Signatures.BuildSignatureBase(metadata, Signatures.ClientToServerHeaders.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var signature = otherKey.Sign(baseBytes);
        var header = new SignatureHeader(
            KeyId.Value, Signatures.AlgorithmLabel(key.Algorithm), Signatures.ClientToServerHeaders,
            Convert.ToBase64String(signature)).Format();

        // The verifier resolves keyId to `key` (the one in the store), which does NOT match the
        // signature made with otherKey.
        Assert.False(new HttpSignatureVerifier(store).Verify(metadata, header),
            "a signature made with the wrong key must not verify");
    }

    [Fact]
    public void Reject_UnparseableKeyId_ReturnsFalse()
    {
        // A keyId that is not a valid IRI (e.g. a bare relative path) is rejected before any
        // cryptographic check.
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);
        var signer = new HttpSignatureSigner(store);
        var verifier = new HttpSignatureVerifier(store);
        var identity = new SystemIdentity(Actor, KeyId);

        var metadata = GetNoBody();
        Assert.True(SignatureHeader.TryParse(signer.Sign(metadata, identity, SigningProfile.ClientToServer), out var header));
        // Rewrite the keyId to an unparseable value.
        var bad = new SignatureHeader("not-a-valid-iri", header!.Algorithm, header.Headers, header.Signature, header.Created).Format();

        Assert.False(verifier.Verify(metadata, bad), "an unparseable keyId must be rejected");
    }

    [Fact]
    public void Reject_EmptyComponentList_ReturnsFalse()
    {
        // A header whose component list is empty (no headers to sign over) is rejected — there is no
        // base to verify.
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);

        var metadata = GetNoBody();
        var header = new SignatureHeader(KeyId.Value, Signatures.AlgorithmLabel(key.Algorithm), "", "c2lnbmF0dXJl").Format();

        Assert.False(new HttpSignatureVerifier(store).Verify(metadata, header),
            "an empty component list must be rejected");
    }

    [Fact]
    public void Reject_NonBase64SignatureValue_ReturnsFalse()
    {
        // A signature value that is not valid base64 is rejected deterministically (no exception).
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);

        var metadata = GetNoBody();
        var header = new SignatureHeader(
            KeyId.Value, Signatures.AlgorithmLabel(key.Algorithm), Signatures.ClientToServerHeaders,
            "!!!not-base64!!!").Format();

        Assert.False(new HttpSignatureVerifier(store).Verify(metadata, header),
            "a non-base64 signature value must be rejected");
    }
}
