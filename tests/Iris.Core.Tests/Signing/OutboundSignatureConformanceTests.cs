using System.Text;
using Iris.Core;
using Iris.Core.Identity;

namespace Iris.Core.Tests.Signing;

/// <summary>
/// Deterministic, offline conformance tests for the <see cref="HttpSignatureSigner"/> outbound
/// path (Phase 82.1). These pin the exact <c>Signature</c> header a real, fixed RSA-2048 key
/// produces for the two ActivityPub profiles, so the outbound signature format can be verified
/// against the spec (and against the expectations of non-permissive peers such as
/// <c>mastodon.social</c>) without any network. The signature bytes are reproducible: RSA PKCS#1
/// v1.5 signatures are deterministic, the key is fixed, and the metadata (including the
/// <c>Date</c>) is fixed.
/// </summary>
/// <remarks>
/// The fixed key is a throwaway test key (a real PKCS#8 RSA-2048 private key, generated once and
/// baked in). It is safe to commit: it is a fixture for conformance pinning, not a production
/// credential, and the corresponding public key is what a peer would fetch from the actor doc.
/// </remarks>
public class OutboundSignatureConformanceTests
{
    private static readonly Iri Actor = new("https://a.domain.local/u/alice");
    private static readonly Iri KeyId = new("https://a.domain.local/u/alice#main-key");

    // A fixed, valid RFC 1123 date (Wed, 26 Aug 2026 12:00:00 UTC). 26 Aug 2026 is a Wednesday, so
    // the day-of-week is consistent and DateTimeOffset.TryParse succeeds (the created parameter is
    // derived from this date). Epoch seconds: 1787745600.
    private const string Date = "Wed, 26 Aug 2026 12:00:00 GMT";
    private const long ExpectedCreated = 1787745600;
    private const string Host = "a.domain.local";

    // The fixed RSA-2048 private key (PKCS#8). Baked in for deterministic signature pinning.
    private const string FixedPrivateKeyPem = """
        -----BEGIN PRIVATE KEY-----
        MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQCzEARSelNg5lw4
        qTJBLVcIUzW1dABg+0VnxiCO81EUwB9VndJmEmCsz/wCDDDWpdLDBhw7Oh/N+N0p
        m1C6uvKro5XElwYQ0t6uzgTAy112aWi6SRIx0D4ycERJE8itya82MSdSJ5kARwxp
        FdGq81lBr+lc/fxbRWFkA/HKYG0HSbqlwa7RYJVtjmmkDfavdXXncql5PPyzGbxu
        B2UZXQbECdfHrMY02mYHvXTN2uphc+C3KrgtIY1boZvCnmN1M/j+J6lDvJ+ul0Nw
        /uPqez8bwPA2hq1IF8XkxZxDhx+WPXlDrttRWYSi2F/tgghHPSSK6BUDFTIw4XFl
        iAuyCs17AgMBAAECggEADqCE0d33OKeoqeI8XjGfdekiLofizglIkqEPIM5Edc75
        4EsLmFXw+rzkp6AqTyZtkIvLu5TUa0Vkf5UV46MI1rd+sPfrQW2QTjQ7FCqooFcc
        /Haim1oY8pLUKSoKDxQ2EVWzkhT0/R5Qp7bmETJevKxrgjKnLid9PKfL1Q3Kajlb
        HPFCILDwCV2rku9ykzqHWmeNOcvh+7HTOxv6l/oiPA34z62QeKLQQK2XZBxg0kQd
        8uKWBotAl4IwmKZAYq/Qsbv7qCqNKuvvbDorrzS6xQWQX4NZedDu/7njFKF4ZqKI
        pFuTG94SqOPabHVcJU+071a2nodR1vdOELk7+UGNZQKBgQDcWzZn2Q7HNKdLb85i
        G31Dpp7sUsMW0v2aRaB9wTOstbpL60yxnIp5cDrWGW464UyJKjU3rf5ynHZB8TDc
        lNm+ghD9CmArqE8Ku7BDxwhqUrsxTLZmp5fhLEpBjSJ11YRl5WCCPZL2IXw6ugdC
        esmeGeG73Dbz81OgcUUNE/GNlQKBgQDQBty1vtyXACGs6lfAooylpOSvPf99yxQg
        lTp4T3KTpSV+6yhKz/XKVG1oqNDGO1InX2jYmOX8LYXFnDZdoqHX5zHvVMaGLFdd
        HWGQ8BhJZ3qxThqKI8R/uAh0Pz/RIHvm1HsVm24Q4ta2/KQt2FFGXJMzoEix5sFX
        A4ysrguKzwKBgQCKp8JmOgi4hIM4TpQY259IsFGT9sfXVtBJAMLqHmX7qSYem2LY
        592iaGI9UicwWZAlRy/RZ2SSja1D9RZ/1hHldEZoUt0M241Q/aT+IQFEleZAMTsd
        ARvqjknzUXF7n+z9iQXfLguJYKyg72meBVFUcIjAAuN5QYU/kcaXYhM+uQKBgHWv
        rCVVuM3kUSjV2pcsXo1HX+iUFno/7T8RrWZq69MDVtcaikzooZC5erv+5T2ASdXk
        cBg5R8MGretBmLAYVZ8jOGjBeR5m73XKLWwlqFe+pvavzOvhmET5BC9fqObSjcXk
        500uBXKgIgCbpPYarsAzl0NZpkae2To009zNCdKZAoGAM9P1e+TCj+VR2+viJ3I6
        IKVVf60fIxTk1MWSustT//LBTKMab/hqEL3s1ZWyCVGatuY/ucECroh7fJVLSllI
        NTL0IMceRORREo++aJCTd1B3fw+SotseycOAcCXxj04zcLdHMq7APcREPLP8MBDL
        qGPHKIY24pPfB86KGyt5wL8=
        -----END PRIVATE KEY-----
        """;

    // The exact Follow activity body the ServerToServer case signs (a real-world shape).
    private static readonly byte[] FollowBody = Encoding.UTF8.GetBytes(
        """{"@context":"https://www.w3.org/ns/activitystreams","id":"https://a.domain.local/activities/1","type":"Follow","actor":"https://a.domain.local/u/alice","object":"https://b.social/users/bob"}""");

    private const string FollowDigest = "sha-256=4vsk1B3EzcWXQv9rbvZh5IHkZbdQnzn+/i+z0VQ797M=";

    private static KeyPair CreateFixedKey() => KeyPair.FromPem(FixedPrivateKeyPem, KeyAlgorithm.Rsa, KeyId);

    private static (HttpSignatureSigner Signer, InMemoryKeyStore Store) CreateSigner()
    {
        var store = new InMemoryKeyStore();
        store.PutKey(CreateFixedKey());
        return (new HttpSignatureSigner(store), store);
    }

    private static HttpRequestMetadata GetMetadata()
        => new(
            method: "GET",
            pathAndQuery: "/u/alice/inbox",
            host: Host,
            date: Date,
            contentType: null,
            body: [],
            headers: new Dictionary<string, string> { ["host"] = Host, ["date"] = Date });

    private static HttpRequestMetadata PostMetadata()
        => new(
            method: "POST",
            pathAndQuery: "/users/bob/inbox",
            host: Host,
            date: Date,
            contentType: "application/activity+json",
            body: FollowBody,
            headers: new Dictionary<string, string>
            {
                ["host"] = Host,
                ["date"] = Date,
                ["digest"] = FollowDigest,
            });

    [Fact]
    public void Sign_ClientToServer_ProducesSpecConformingHeader()
    {
        var (signer, _) = CreateSigner();
        var identity = new SystemIdentity(Actor, KeyId);

        var wire = signer.Sign(GetMetadata(), identity, SigningProfile.ClientToServer);

        Assert.True(SignatureHeader.TryParse(wire, out var header));
        var h = header!;

        // keyId is the actor's publicKey IRI (the #fragment of the key, not the actor).
        Assert.Equal(KeyId.Value, h.KeyId);
        // algorithm label for an RSA key.
        Assert.Equal("rsa-sha256", h.Algorithm);
        // The ClientToServer profile covers exactly (request-target) host date — no digest/content-type.
        Assert.Equal("(request-target) host date", h.Headers);
        // created is the epoch seconds of the signed date (deterministic, not the wall clock).
        Assert.Equal(ExpectedCreated, h.Created);
        // The exact reproducible RSA PKCS#1 v1.5 signature over the pinned base.
        const string expectedSignature =
            "gJRbVZXso3vPTaeW/knMadHDeiaZJnvHCNbQLXMA3lhjMiP8tJ4jC4lfxdBUwdA1BjU9q1oTsIEh2xHmWloursnNQY4c1Gbi4FpoiIzUow/xEzEm6WDGSBtRIcAvHj5hkbfQ8wMQNxDZ6PjQyoIlE6ZDiawLwRbyuk78gMzs7kQTZQd3f7lW8jGQz6cVErWQGnITmVfvrGYjJE6raKpbnfWBVd2yjC5GkvbXTdrwr/YQTJ4hWoiIb1GsjfaUpFyXfZM3komLtrkOb5wG7uRS6GZ3MPLLz5d05YdoE0lCfoNNZCjZnzMLeBLuqKPVlWdlI1bkT+M3ZvPbW4OV+92vFw==";
        Assert.Equal(expectedSignature, h.Signature);

        // The wire format places created as an unquoted integer after the quoted parameters.
        Assert.EndsWith($", created={ExpectedCreated}", wire);
    }

    [Fact]
    public void Sign_ServerToServer_ProducesSpecConformingHeaderWithDigest()
    {
        var (signer, _) = CreateSigner();
        var identity = new SystemIdentity(Actor, KeyId);

        var wire = signer.Sign(PostMetadata(), identity, SigningProfile.ServerToServer);

        Assert.True(SignatureHeader.TryParse(wire, out var header));
        var h = header!;

        Assert.Equal(KeyId.Value, h.KeyId);
        Assert.Equal("rsa-sha256", h.Algorithm);
        // The ServerToServer profile adds digest + content-type (covers the body).
        Assert.Equal("(request-target) host date digest content-type", h.Headers);
        Assert.Equal(ExpectedCreated, h.Created);

        // The exact reproducible signature over the body-carrying base (digest + content-type covered).
        const string expectedSignature =
            "by0mXbvHHvsPZNEuHYx7Y8sdOnaxa89lvl/yQwR3SPVLAwUtioX/b+MvQFcLMAFvBNnfRBr36P4hNtDlrI7MpIxYzpEgJzA7kGDtxwgGnPTHmLYHBYW2D3hFr+qxgDFaOdOtfKZCXZqbXGrGsHMsvIuWdbn+VRhBDEKasHI/SwwBN62kYrjZiEXM7Ii0QG79qIsDQ9+g/zZ+sG/cdlmRNpo663cqIHWKKS/bJ/5B8tVRKx3q6/cwKXQw6HEGiHCjSqXjCyXb//x3O4NMdXGg+6vzhmqz2AQHO/WGuSOtACoi5H18gGvoP0LLMEjGWVr63m/LtOQdcx4+UHb9Pf7O/g==";
        Assert.Equal(expectedSignature, h.Signature);
    }

    [Fact]
    public void Sign_ClientToServer_SignatureVerifiesAgainstPublicKey()
    {
        // The signature must be verifiable by a peer holding only the public key (what a receiving
        // instance fetches from the actor doc's publicKeyPem).
        var (signer, store) = CreateSigner();
        var identity = new SystemIdentity(Actor, KeyId);
        var metadata = GetMetadata();

        var wire = signer.Sign(metadata, identity, SigningProfile.ClientToServer);

        // A peer resolves the public key by the keyId from its own store (here, the same store —
        // the point is the signature validates against the key's public half, not the private one).
        using var publicOnly = CreateFixedKey();
        var verifier = new HttpSignatureVerifier(new InMemoryKeyStore { });
        // Re-create a store with the key so the verifier can resolve by keyId.
        var peerStore = new InMemoryKeyStore();
        peerStore.PutKey(publicOnly);
        var peerVerifier = new HttpSignatureVerifier(peerStore);

        Assert.True(peerVerifier.Verify(metadata, wire));
    }

    [Fact]
    public void Sign_ServerToServer_SignatureVerifiesAgainstPublicKey()
    {
        var (signer, _) = CreateSigner();
        var identity = new SystemIdentity(Actor, KeyId);
        var metadata = PostMetadata();

        var wire = signer.Sign(metadata, identity, SigningProfile.ServerToServer);

        var peerStore = new InMemoryKeyStore();
        peerStore.PutKey(CreateFixedKey());
        var peerVerifier = new HttpSignatureVerifier(peerStore);

        Assert.True(peerVerifier.Verify(metadata, wire));
    }

    [Fact]
    public void Sign_SignatureBase_MatchesPinnedSpecExample()
    {
        // Pin the exact signed-string bytes (the thing the signature covers) for both profiles, so a
        // regression that reorders or re-formats a component is caught. This is the de facto
        // Fediverse convention: name: value lines joined by \n with no trailing newline.
        var getMeta = GetMetadata();
        var getBase = Signatures.BuildSignatureBase(
            getMeta, Signatures.HeadersForProfile(SigningProfile.ClientToServer).Split(' '));
        Assert.Equal(
            "(request-target): get /u/alice/inbox\nhost: a.domain.local\ndate: Wed, 26 Aug 2026 12:00:00 GMT",
            Encoding.UTF8.GetString(getBase));

        var postMeta = PostMetadata();
        var postBase = Signatures.BuildSignatureBase(
            postMeta, Signatures.HeadersForProfile(SigningProfile.ServerToServer).Split(' '));
        Assert.Equal(
            "(request-target): post /users/bob/inbox\nhost: a.domain.local\ndate: Wed, 26 Aug 2026 12:00:00 GMT\ndigest: sha-256=4vsk1B3EzcWXQv9rbvZh5IHkZbdQnzn+/i+z0VQ797M=\ncontent-type: application/activity+json",
            Encoding.UTF8.GetString(postBase));
    }

    [Fact]
    public void Sign_Created_DerivedFromSignedDate_IsDeterministic()
    {
        // Two signatures over the same fixed date must carry the same created (not the wall clock).
        var (signer, _) = CreateSigner();
        var identity = new SystemIdentity(Actor, KeyId);

        var first = SignatureHeader.TryParse(signer.Sign(GetMetadata(), identity, SigningProfile.ClientToServer), out var f);
        var second = SignatureHeader.TryParse(signer.Sign(GetMetadata(), identity, SigningProfile.ClientToServer), out var s);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(ExpectedCreated, f!.Created);
        Assert.Equal(f!.Created, s!.Created);
        // And the signature itself is byte-identical (RSA PKCS#1 v1.5 is deterministic).
        Assert.Equal(f!.Signature, s!.Signature);
    }

    [Fact]
    public void ToUnixSeconds_ValidHttpDate_ReturnsEpochSeconds()
    {
        Assert.Equal(1787745600, Signatures.ToUnixSeconds("Wed, 26 Aug 2026 12:00:00 GMT"));
        // +0000 offset form parses to the same instant.
        Assert.Equal(1787745600, Signatures.ToUnixSeconds("Wed, 26 Aug 2026 12:00:00 +0000"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void ToUnixSeconds_UnparseableDate_FallsBackToZero(string? bad)
    {
        // Defensive fallback: a malformed date must not throw (created simply becomes 0).
        Assert.Equal(0, Signatures.ToUnixSeconds(bad));
    }

    [Fact]
    public void TryParse_LegacyHeaderWithoutCreated_StillParses()
    {
        // Backward compatibility: a peer (or Iris pre-Phase-82.1) that omits created must still parse,
        // and the created value defaults to 0.
        const string legacy =
            "keyId=\"https://a.domain.local/u/alice#main-key\", algorithm=\"rsa-sha256\", headers=\"(request-target) host date\", signature=\"c2ln\"";

        Assert.True(SignatureHeader.TryParse(legacy, out var header));
        Assert.Equal(0, header!.Created);
        Assert.Equal("c2ln", header.Signature);
    }

    [Fact]
    public void TryParse_HeaderWithCreated_ParsesCreated()
    {
        const string withCreated =
            "keyId=\"https://a.domain.local/u/alice#main-key\", algorithm=\"rsa-sha256\", headers=\"(request-target) host date\", signature=\"c2ln\", created=1787745600";

        Assert.True(SignatureHeader.TryParse(withCreated, out var header));
        Assert.Equal(1787745600, header!.Created);
    }
}
