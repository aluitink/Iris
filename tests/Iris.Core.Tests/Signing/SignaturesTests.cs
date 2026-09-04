using System.Security.Cryptography;
using System.Text;
using Iris.Core;
using Iris.Core.Identity;

namespace Iris.Core.Tests.Signing;

/// <summary>
/// Unit tests for the shared <see cref="Signatures"/> helpers: algorithm labels, digest, and
/// signature-base construction.
/// </summary>
public class SignaturesTests
{
    [Theory]
    [InlineData(KeyAlgorithm.Rsa, "rsa-sha256")]
    [InlineData(KeyAlgorithm.EcP256, "ecdsa-p256-sha256")]
    public void AlgorithmLabel_ReturnsExpectedValue(KeyAlgorithm algorithm, string expected)
        => Assert.Equal(expected, Signatures.AlgorithmLabel(algorithm));

    [Fact]
    public void HeadersForProfile_ClientToServer()
        => Assert.Equal("(request-target) host date", Signatures.HeadersForProfile(SigningProfile.ClientToServer));

    [Fact]
    public void HeadersForProfile_ServerToServer()
        => Assert.Equal("(request-target) host date digest content-type", Signatures.HeadersForProfile(SigningProfile.ServerToServer));

    [Fact]
    public void ComputeDigest_EmptyBody_MatchesKnownSha256()
    {
        var digest = Signatures.ComputeDigest([]);

        // The exact base64 must match the SHA-256 of the empty byte array (a known constant).
        var expected = Convert.ToBase64String(SHA256.HashData([]));
        Assert.Equal($"sha-256={expected}", digest);
    }

    [Fact]
    public void ComputeDigest_ProducesSha256Prefix()
    {
        var digest = Signatures.ComputeDigest(Encoding.UTF8.GetBytes("hello"));

        Assert.StartsWith("sha-256=", digest);
    }

    [Fact]
    public void BuildSignatureBase_ClientToServer_MatchesSpecExample()
    {
        var metadata = new HttpRequestMetadata(
            method: "POST",
            pathAndQuery: "/u/alice/inbox",
            host: "a.domain.local",
            date: "Tue, 26 Aug 2026 12:00:00 GMT",
            contentType: null,
            body: [],
            headers: new Dictionary<string, string>());

        var baseBytes = Signatures.BuildSignatureBase(metadata, ["(request-target)", "host", "date"]);

        // (request-target) is the lowercased "method path". Lines are joined with a newline
        // separator and there is NO trailing newline (matches Mastodon/Pleroma/Misskey).
        var expected =
            "(request-target): post /u/alice/inbox\n" +
            "host: a.domain.local\n" +
            "date: Tue, 26 Aug 2026 12:00:00 GMT";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), baseBytes);
    }

    [Fact]
    public void BuildSignatureBase_ServerToServer_IncludesDigestAndContentType()
    {
        var body = Encoding.UTF8.GetBytes("{\"@context\":\"https://www.w3.org/ns/activitystreams\"}");
        var digest = Signatures.ComputeDigest(body);
        var metadata = new HttpRequestMetadata(
            method: "POST",
            pathAndQuery: "/u/alice/inbox",
            host: "a.domain.local",
            date: "Tue, 26 Aug 2026 12:00:00 GMT",
            contentType: "application/activity+json",
            body: body,
            headers: new Dictionary<string, string> { ["digest"] = digest });

        var baseBytes = Signatures.BuildSignatureBase(
            metadata,
            ["(request-target)", "host", "date", "digest", "content-type"]);

        var expected =
            "(request-target): post /u/alice/inbox\n" +
            "host: a.domain.local\n" +
            "date: Tue, 26 Aug 2026 12:00:00 GMT\n" +
            $"digest: {digest}\n" +
            "content-type: application/activity+json";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), baseBytes);
    }

    // --- Digest header casing: the value is embedded VERBATIM, so a strict draft-10 / Mastodon
    //     sender's uppercase `SHA-256=…` wire form reconstructs the same base and verifies --------

    [Fact]
    public void BuildSignatureBase_ServerToServer_EmbodiesUppercaseDigestVerbatim()
    {
        // A strict draft-10 / Mastodon peer emits `Digest: SHA-256=<b64>` (uppercase algorithm label).
        // The signature base must embed that EXACT wire string — the verifier trusts the declared
        // header value (it does not recompute the digest), so the base must byte-match what the
        // sender signed over. This is what makes an uppercase-Digest request verifiable.
        var body = Encoding.UTF8.GetBytes("{\"@context\":\"https://www.w3.org/ns/activitystreams\"}");
        var uppercaseDigest = $"SHA-256={Convert.ToBase64String(SHA256.HashData(body))}";
        var metadata = new HttpRequestMetadata(
            method: "POST",
            pathAndQuery: "/u/alice/inbox",
            host: "a.domain.local",
            date: "Tue, 26 Aug 2026 12:00:00 GMT",
            contentType: "application/activity+json",
            body: body,
            headers: new Dictionary<string, string> { ["digest"] = uppercaseDigest });

        var baseBytes = Signatures.BuildSignatureBase(
            metadata,
            ["(request-target)", "host", "date", "digest", "content-type"]);

        var expected =
            "(request-target): post /u/alice/inbox\n" +
            "host: a.domain.local\n" +
            "date: Tue, 26 Aug 2026 12:00:00 GMT\n" +
            $"digest: {uppercaseDigest}\n" +
            "content-type: application/activity+json";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), baseBytes);
    }

    [Fact]
    public void ServerToServer_UppercaseDigestHeader_SignAndVerifyRoundTrips()
    {
        // The full round-trip that a strict draft-10 / Mastodon peer performs: the sender computes the
        // digest, formats the header value with the UPPERCASE algorithm label (the draft-10 wire form),
        // signs over a base embedding that uppercase value, and the verifier reconstructs the same base
        // from the wire header (verbatim) and accepts. If the verifier recomputed or canonicalized the
        // digest label, the base would drift and this would fail.
        var keyId = new Iri("https://remote.example.org/ap/v1/u/alice#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyId);
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var realSigner = new HttpSignatureSigner(keyStore);

        var body = Encoding.UTF8.GetBytes("{\"@context\":\"https://www.w3.org/ns/activitystreams\"}");
        var uppercaseDigest = $"SHA-256={Convert.ToBase64String(SHA256.HashData(body))}";
        var date = "Tue, 26 Aug 2026 12:00:00 GMT";
        var contentType = "application/activity+json";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.HostHeaderName] = "remote.example.org",
            [Signatures.DateHeaderName] = date,
            [Signatures.ContentTypeHeaderName] = contentType,
            [Signatures.DigestHeaderName] = uppercaseDigest,
        };
        var metadata = new HttpRequestMetadata("POST", "/ap/v1/u/alice/inbox", "remote.example.org", date, contentType, body, headers);
        var identity = new SystemIdentity(new Iri("https://remote.example.org/ap/v1/u/alice"), keyId);
        var signatureHeader = realSigner.Sign(metadata, identity, SigningProfile.ServerToServer);

        // The verifier reconstructs the base from the wire (uppercase digest verbatim) and must accept.
        var verifier = new HttpSignatureVerifier(keyStore);
        Assert.True(
            verifier.Verify(metadata, signatureHeader),
            "An inbound ServerToServer request carrying the uppercase draft-10 Digest header must verify.");
    }

    [Fact]
    public void BuildSignatureBase_UsesOnlyDeclaredComponents()
    {
        var metadata = new HttpRequestMetadata("GET", "/u/alice", "a.domain.local", "D", null, [], new Dictionary<string, string>());

        // Declaring only (request-target) yields a single line (no trailing newline).
        var baseBytes = Signatures.BuildSignatureBase(metadata, ["(request-target)"]);

        Assert.Equal(Encoding.UTF8.GetBytes("(request-target): get /u/alice"), baseBytes);
    }

    // --- ResolveDateComponent: the date component must be the signed value, not the wire Date ---

    [Fact]
    public void ResolveDateComponent_PrefersXSignatureDateOverDate()
    {
        // The browser client signs over X-Signature-Date and the browser overrides the wire Date on
        // the wire. The date component must come from X-Signature-Date (the signed value), not Date.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.DateHeaderName] = "Wed, 02 Sep 2026 10:00:00 GMT",
            [Signatures.SignatureDateHeaderName] = "Wed, 02 Sep 2026 09:59:59 GMT",
        };

        Assert.Equal("Wed, 02 Sep 2026 09:59:59 GMT", Signatures.ResolveDateComponent(headers));
    }

    [Fact]
    public void ResolveDateComponent_FallsBackToDateWhenNoXSignatureDate()
    {
        // A non-browser client signs over its Date header and does not set X-Signature-Date; the
        // date component must fall back to the wire Date.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.DateHeaderName] = "Wed, 02 Sep 2026 10:00:00 GMT",
        };

        Assert.Equal("Wed, 02 Sep 2026 10:00:00 GMT", Signatures.ResolveDateComponent(headers));
    }

    [Fact]
    public void ResolveDateComponent_EmptyWhenNeitherPresent()
    {
        Assert.Equal("", Signatures.ResolveDateComponent(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ResolveDateComponent_IgnoresEmptyXSignatureDateAndUsesDate()
    {
        // An empty X-Signature-Date is treated as absent, so the value falls back to the wire Date.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.DateHeaderName] = "Wed, 02 Sep 2026 10:00:00 GMT",
            [Signatures.SignatureDateHeaderName] = "",
        };

        Assert.Equal("Wed, 02 Sep 2026 10:00:00 GMT", Signatures.ResolveDateComponent(headers));
    }
}
