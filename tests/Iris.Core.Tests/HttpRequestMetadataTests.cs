using Iris.Core;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for <see cref="HttpRequestMetadata"/>.
/// </summary>
public class HttpRequestMetadataTests
{
    [Fact]
    public void GetHeader_CaseInsensitive()
    {
        var metadata = new HttpRequestMetadata(
            "POST", "/u/alice/inbox", "a.domain.local", "D", "application/activity+json", [],
            new Dictionary<string, string> { ["Digest"] = "sha-512=abc" });

        Assert.Equal("sha-512=abc", metadata.GetHeader("digest"));
        Assert.Equal("sha-512=abc", metadata.GetHeader("DIGEST"));
        Assert.Null(metadata.GetHeader("missing"));
    }

    [Fact]
    public void IsValueComparable()
    {
        var a = new HttpRequestMetadata(
            "GET", "/x", "h", "d", null, [1, 2, 3], new Dictionary<string, string> { ["k"] = "v" });
        var b = new HttpRequestMetadata(
            "GET", "/x", "h", "d", null, [1, 2, 3], new Dictionary<string, string> { ["k"] = "v" });

        // Value equality: equal fields and equal (by contents) body/headers compare equal.
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void With_ReplacesOnlySpecifiedFields()
    {
        var a = new HttpRequestMetadata(
            "GET", "/x", "h", "d", null, [], new Dictionary<string, string>());

        var b = a.With(date: "new-date");

        Assert.Equal("new-date", b.Date);
        Assert.Equal("GET", b.Method);
        Assert.Equal("/x", b.PathAndQuery);
    }
}
