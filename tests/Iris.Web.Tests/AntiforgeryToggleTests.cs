using Iris.Web;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Iris.Web.Tests;

/// <summary>
/// Unit tests for <see cref="WebAppFactory.IsAntiforgeryEnabled"/> — the Phase 94 antiforgery
/// toggle. The method is pure config-parsing logic (no I/O), so it is exercised directly rather than
/// through a full TestServer boot. It is <c>internal</c> to <c>Iris.Web</c> and visible here via
/// <c>InternalsVisibleTo</c>.
///
/// Contract: antiforgery is enabled (the production default) UNLESS the value bound to
/// <c>Iris:Security:EnableAntiforgery</c> parses to <c>false</c> (case-insensitive). Unset/blank and
/// any non-<c>false</c> value (including unparseable garbage) keep antiforgery ON — a
/// misconfiguration can never silently disable the protection.
/// </summary>
public sealed class AntiforgeryToggleTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var dict = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetOrBlank_Enabled(string? value)
    {
        var config = value is null ? Config() : Config((WebAppFactory.EnableAntiforgeryConfigKey, value));
        Assert.True(WebAppFactory.IsAntiforgeryEnabled(config));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    public void NonFalseValues_Enabled(string value)
    {
        var config = Config((WebAppFactory.EnableAntiforgeryConfigKey, value));
        Assert.True(WebAppFactory.IsAntiforgeryEnabled(config));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void FalseValues_Disabled(string value)
    {
        var config = Config((WebAppFactory.EnableAntiforgeryConfigKey, value));
        Assert.False(WebAppFactory.IsAntiforgeryEnabled(config));
    }

    [Theory]
    [InlineData("off-please")]
    [InlineData("0")]
    [InlineData("disabled")]
    public void UnparseableValue_Enabled(string value)
    {
        // A typo / garbage / numeric value must NOT silently disable antiforgery — fail closed (ON).
        // bool.TryParse only recognizes "true"/"false" (case-insensitive), so "0"/"disabled" parse as
        // unparseable and the flag fails closed to enabled.
        var config = Config((WebAppFactory.EnableAntiforgeryConfigKey, value));
        Assert.True(WebAppFactory.IsAntiforgeryEnabled(config));
    }
}
