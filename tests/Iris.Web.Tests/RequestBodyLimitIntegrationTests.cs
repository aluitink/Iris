using System.Net;
using Iris.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for the production host's <em>inbound request-body size cap</em> (slice 33.4).
/// </summary>
/// <remarks>
/// The cap is a Kestrel server-level setting applied in <see cref="WebAppFactory.ConfigureServices"/>
/// via <c>builder.WebHost.ConfigureKestrel</c> (Kestrel rejects a body over the limit with 413 before the
/// pipeline reads it). Because the inbox handler rejects an <em>unsigned</em> body with 401 before it is
/// read (the signature gate runs first — correct: it rejects unauthenticated oversized bodies cheaply),
/// the size gate is a defense-in-depth backstop for a <em>signed</em> peer streaming an oversized body.
/// These tests therefore assert the production wiring directly: the Kestrel server options resolved from
/// the built host carry the expected <c>MaxRequestBodySize</c> (the 1 MiB default, and a raised value when
/// <c>Iris:MaxRequestBodySize</c> is set). The host is built (not started), so no resources are held and
/// nothing needs disposing.
/// </remarks>
public sealed class RequestBodyLimitIntegrationTests
{
    private const string Base = "https://web.test.local";

    private static KestrelServerOptions BuildKestrelOptions(string? maxBodySizeConfig)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (maxBodySizeConfig is not null)
        {
            builder.Configuration["Iris:MaxRequestBodySize"] = maxBodySizeConfig;
        }
        WebAppFactory.ConfigureServices(builder, Base);

        var app = builder.Build();
        return app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
    }

    [Fact]
    public void DefaultCap_IsOneMegabyte()
    {
        var options = BuildKestrelOptions(maxBodySizeConfig: null);

        Assert.Equal(WebAppFactory.DefaultMaxRequestBodySize, options.Limits.MaxRequestBodySize);
        Assert.Equal(1024L * 1024L, options.Limits.MaxRequestBodySize);
    }

    [Fact]
    public void ConfiguredCap_IsApplied()
    {
        // Raising the cap via Iris:MaxRequestBodySize (env IRIS_MAX_REQUEST_BODY_SIZE) must flow through
        // to the Kestrel server options.
        var options = BuildKestrelOptions(maxBodySizeConfig: "4194304"); // 4 MiB

        Assert.Equal(4194304L, options.Limits.MaxRequestBodySize);
    }

    [Fact]
    public void InvalidConfig_FallsBackToDefault()
    {
        // A non-numeric value must fall back to the default cap (not 0 / not crash).
        var options = BuildKestrelOptions(maxBodySizeConfig: "not-a-number");

        Assert.Equal(WebAppFactory.DefaultMaxRequestBodySize, options.Limits.MaxRequestBodySize);
    }

    [Fact]
    public void NegativeConfig_FallsBackToDefault()
    {
        var options = BuildKestrelOptions(maxBodySizeConfig: "-5");

        Assert.Equal(WebAppFactory.DefaultMaxRequestBodySize, options.Limits.MaxRequestBodySize);
    }
}
