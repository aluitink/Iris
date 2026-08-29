using System.Security.Cryptography;
using System.Text;
using Iris.Core;

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
    public void ComputeDigest_EmptyBody_MatchesKnownSha512()
    {
        var digest = Signatures.ComputeDigest([]);

        // The exact base64 must match the SHA-512 of the empty byte array (a known constant).
        var expected = Convert.ToBase64String(SHA512.HashData([]));
        Assert.Equal($"sha-512={expected}", digest);
    }

    [Fact]
    public void ComputeDigest_ProducesSha512Prefix()
    {
        var digest = Signatures.ComputeDigest(Encoding.UTF8.GetBytes("hello"));

        Assert.StartsWith("sha-512=", digest);
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

        // (request-target) is the lowercased "method path".
        var expected =
            "(request-target): post /u/alice/inbox\n" +
            "host: a.domain.local\n" +
            "date: Tue, 26 Aug 2026 12:00:00 GMT\n";
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
            "content-type: application/activity+json\n";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), baseBytes);
    }

    [Fact]
    public void BuildSignatureBase_UsesOnlyDeclaredComponents()
    {
        var metadata = new HttpRequestMetadata("GET", "/u/alice", "a.domain.local", "D", null, [], new Dictionary<string, string>());

        // Declaring only (request-target) yields a single line.
        var baseBytes = Signatures.BuildSignatureBase(metadata, ["(request-target)"]);

        Assert.Equal(Encoding.UTF8.GetBytes("(request-target): get /u/alice\n"), baseBytes);
    }
}
