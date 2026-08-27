using Iris.Core;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SignatureHeader"/> parse/format round-trips.
/// </summary>
public class SignatureHeaderTests
{
    private const string Full =
        "keyId=\"https://a.domain.local/u/alice#main-key\", algorithm=\"rsa-sha256\", headers=\"(request-target) host date\", signature=\"c2ln\"";

    [Fact]
    public void TryParse_WellFormed_Succeeds()
    {
        Assert.True(SignatureHeader.TryParse(Full, out var header));
        Assert.NotNull(header);
        Assert.Equal("https://a.domain.local/u/alice#main-key", header!.KeyId);
        Assert.Equal("rsa-sha256", header.Algorithm);
        Assert.Equal("(request-target) host date", header.Headers);
        Assert.Equal("c2ln", header.Signature);
    }

    [Fact]
    public void TryParse_MissingField_Fails()
    {
        const string missingSignature =
            "keyId=\"https://a.domain.local/u/alice#main-key\", algorithm=\"rsa-sha256\", headers=\"(request-target) host date\"";

        Assert.False(SignatureHeader.TryParse(missingSignature, out _));
    }

    [Fact]
    public void TryParse_UnquotedValue_Fails()
    {
        const string unquoted = "keyId=https://a.domain.local/u/alice#main-key, algorithm=\"rsa-sha256\", headers=\"(request-target) host date\", signature=\"c2ln\"";

        Assert.False(SignatureHeader.TryParse(unquoted, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_EmptyOrWhitespace_Fails(string? value)
    {
        Assert.False(SignatureHeader.TryParse(value, out _));
    }

    [Fact]
    public void Format_RoundTrips()
    {
        var header = new SignatureHeader(
            "https://a.domain.local/u/alice#main-key",
            "ecdsa-p256-sha256",
            "(request-target) host date digest content-type",
            "c2lnYXR1cmU=");

        var wire = header.Format();
        Assert.True(SignatureHeader.TryParse(wire, out var reparsed));
        Assert.Equal(header, reparsed);
    }

    [Fact]
    public void Format_QuotedAndCommaSeparated()
    {
        var header = new SignatureHeader("k", "rsa-sha256", "(request-target) host date", "c2ln");

        var wire = header.Format();

        Assert.StartsWith("keyId=\"k\"", wire);
        Assert.Contains(", algorithm=\"rsa-sha256\"", wire);
        Assert.Contains(", headers=\"(request-target) host date\"", wire);
        Assert.EndsWith(", signature=\"c2ln\"", wire);
    }

    [Fact]
    public void TryParse_ExtraUnknownFields_AreIgnored()
    {
        // An extra component (e.g. a future extension) must not break parsing of the required fields.
        const string withExtra =
            "keyId=\"https://a.domain.local/u/alice#main-key\", algorithm=\"rsa-sha256\", headers=\"(request-target) host date\", signature=\"c2ln\", created=\"1700000000\"";

        Assert.True(SignatureHeader.TryParse(withExtra, out var header));
        Assert.Equal("c2ln", header!.Signature);
    }
}
