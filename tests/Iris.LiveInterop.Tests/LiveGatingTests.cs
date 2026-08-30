using Xunit;

namespace Iris.LiveInterop.Tests;

/// <summary>
/// Unit tests for the live-interop gating logic: <see cref="LiveInteropOptions.TryLoadFromEnvironment"/>
/// and <see cref="LiveGuard.TryRequires"/>. These run in the default <c>dotnet test</c> (no live
/// instance is contacted) and prove the gate works: when <c>IRIS_LIVE_INTEROP</c> is not <c>"1"</c>,
/// the suite is disabled; when it is <c>"1"</c> but the FQDN is not configured, the suite cannot run.
/// </summary>
public sealed class LiveGatingTests
{
    // --- TryLoadFromEnvironment: master switch off ----------------------------------------------

    [Fact]
    public void TryLoadFromEnvironment_MasterSwitchOff_ReturnsFalse()
    {
        // Arrange: ensure the master switch is not set to "1" or "true".
        var original = Environment.GetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar);
        Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, null);

        try
        {
            // Act.
            var result = LiveInteropOptions.TryLoadFromEnvironment(out var options);

            // Assert.
            Assert.False(result);
            Assert.False(options.IsEnabled);
            Assert.Null(options.OurBaseUri);
            Assert.Empty(options.Targets);
            Assert.Equal(LiveInteropOptions.DefaultRequestBudget, options.RequestBudget);
            Assert.Equal(LiveInteropOptions.DefaultRateLimitPerSecond, options.RateLimitPerSecond);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, original);
        }
    }

    // --- TryLoadFromEnvironment: master switch on, no FQDN ---------------------------------------

    [Fact]
    public void TryLoadFromEnvironment_MasterSwitchOn_NoFqdn_ReturnsTrue_CannotRun()
    {
        var originalEnabled = Environment.GetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar);
        var originalBaseUri = Environment.GetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar);
        Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, "1");
        Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, null);

        try
        {
            var result = LiveInteropOptions.TryLoadFromEnvironment(out var options);

            Assert.True(result);
            Assert.True(options.IsEnabled);
            Assert.Null(options.OurBaseUri);
            // CanRun requires both IsEnabled and OurBaseUri — so it is false here.
            Assert.False(options.CanRun);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, originalEnabled);
            Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, originalBaseUri);
        }
    }

    // --- TryLoadFromEnvironment: master switch on, FQDN set ---------------------------------------

    [Fact]
    public void TryLoadFromEnvironment_MasterSwitchOn_FqdnSet_ReturnsTrue_CanRun()
    {
        var originalEnabled = Environment.GetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar);
        var originalBaseUri = Environment.GetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar);
        var originalActor = Environment.GetEnvironmentVariable(LiveInteropOptions.OurActorIriEnvVar);
        var originalUsername = Environment.GetEnvironmentVariable(LiveInteropOptions.OurUsernameEnvVar);
        var originalPassword = Environment.GetEnvironmentVariable(LiveInteropOptions.OurPasswordEnvVar);
        var originalBudget = Environment.GetEnvironmentVariable(LiveInteropOptions.RequestBudgetEnvVar);
        var originalRate = Environment.GetEnvironmentVariable(LiveInteropOptions.RateLimitEnvVar);

        Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, "1");
        Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, "https://iris.example.org");
        Environment.SetEnvironmentVariable(LiveInteropOptions.OurActorIriEnvVar, "https://iris.example.org/ap/v1/u/alice");
        Environment.SetEnvironmentVariable(LiveInteropOptions.OurUsernameEnvVar, "alice");
        Environment.SetEnvironmentVariable(LiveInteropOptions.OurPasswordEnvVar, "secret");
        Environment.SetEnvironmentVariable(LiveInteropOptions.RequestBudgetEnvVar, "50");
        Environment.SetEnvironmentVariable(LiveInteropOptions.RateLimitEnvVar, "5");

        try
        {
            var result = LiveInteropOptions.TryLoadFromEnvironment(out var options);

            Assert.True(result);
            Assert.True(options.IsEnabled);
            Assert.Equal(new Iri("https://iris.example.org"), options.OurBaseUri);
            Assert.Equal(new Iri("https://iris.example.org/ap/v1/u/alice"), options.OurActorIri);
            Assert.Equal("alice", options.OurUsername);
            Assert.Equal("secret", options.OurPassword);
            Assert.Equal(50, options.RequestBudget);
            Assert.Equal(5, options.RateLimitPerSecond);
            Assert.True(options.CanRun);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, originalEnabled);
            Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, originalBaseUri);
            Environment.SetEnvironmentVariable(LiveInteropOptions.OurActorIriEnvVar, originalActor);
            Environment.SetEnvironmentVariable(LiveInteropOptions.OurUsernameEnvVar, originalUsername);
            Environment.SetEnvironmentVariable(LiveInteropOptions.OurPasswordEnvVar, originalPassword);
            Environment.SetEnvironmentVariable(LiveInteropOptions.RequestBudgetEnvVar, originalBudget);
            Environment.SetEnvironmentVariable(LiveInteropOptions.RateLimitEnvVar, originalRate);
        }
    }

    // --- TryLoadFromEnvironment: custom budget/rate limit fall back to defaults ---------------------

    [Fact]
    public void TryLoadFromEnvironment_NoBudgetOrRate_UsesDefaults()
    {
        var originalEnabled = Environment.GetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar);
        var originalBaseUri = Environment.GetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar);
        var originalBudget = Environment.GetEnvironmentVariable(LiveInteropOptions.RequestBudgetEnvVar);
        var originalRate = Environment.GetEnvironmentVariable(LiveInteropOptions.RateLimitEnvVar);

        Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, "true");
        Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, "https://iris.example.org");
        Environment.SetEnvironmentVariable(LiveInteropOptions.RequestBudgetEnvVar, null);
        Environment.SetEnvironmentVariable(LiveInteropOptions.RateLimitEnvVar, null);

        try
        {
            var result = LiveInteropOptions.TryLoadFromEnvironment(out var options);

            Assert.True(result);
            Assert.Equal(LiveInteropOptions.DefaultRequestBudget, options.RequestBudget);
            Assert.Equal(LiveInteropOptions.DefaultRateLimitPerSecond, options.RateLimitPerSecond);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, originalEnabled);
            Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, originalBaseUri);
            Environment.SetEnvironmentVariable(LiveInteropOptions.RequestBudgetEnvVar, originalBudget);
            Environment.SetEnvironmentVariable(LiveInteropOptions.RateLimitEnvVar, originalRate);
        }
    }

    // --- TryLoadFromEnvironment: invalid budget/rate limit fall back to defaults --------------------

    [Fact]
    public void TryLoadFromEnvironment_InvalidBudgetOrRate_UsesDefaults()
    {
        var originalEnabled = Environment.GetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar);
        var originalBaseUri = Environment.GetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar);
        var originalBudget = Environment.GetEnvironmentVariable(LiveInteropOptions.RequestBudgetEnvVar);
        var originalRate = Environment.GetEnvironmentVariable(LiveInteropOptions.RateLimitEnvVar);

        Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, "1");
        Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, "https://iris.example.org");
        Environment.SetEnvironmentVariable(LiveInteropOptions.RequestBudgetEnvVar, "not-a-number");
        Environment.SetEnvironmentVariable(LiveInteropOptions.RateLimitEnvVar, "also-not-a-number");

        try
        {
            var result = LiveInteropOptions.TryLoadFromEnvironment(out var options);

            Assert.True(result);
            Assert.Equal(LiveInteropOptions.DefaultRequestBudget, options.RequestBudget);
            Assert.Equal(LiveInteropOptions.DefaultRateLimitPerSecond, options.RateLimitPerSecond);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, originalEnabled);
            Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, originalBaseUri);
            Environment.SetEnvironmentVariable(LiveInteropOptions.RequestBudgetEnvVar, originalBudget);
            Environment.SetEnvironmentVariable(LiveInteropOptions.RateLimitEnvVar, originalRate);
        }
    }

    // --- LiveGuard.TryRequires: disabled suite ---------------------------------------------------

    [Fact]
    public void TryRequires_MasterSwitchOff_ReturnsFalse()
    {
        var original = Environment.GetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar);
        Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, null);

        try
        {
            var result = LiveGuard.TryRequires(out var options);

            Assert.False(result);
            Assert.False(options.IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, original);
        }
    }

    // --- LiveGuard.TryRequires: enabled but no FQDN -----------------------------------------------

    [Fact]
    public void TryRequires_MasterSwitchOn_NoFqdn_ReturnsFalse()
    {
        var originalEnabled = Environment.GetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar);
        var originalBaseUri = Environment.GetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar);
        Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, "1");
        Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, null);

        try
        {
            var result = LiveGuard.TryRequires(out var options);

            Assert.False(result);
            Assert.True(options.IsEnabled);
            Assert.Null(options.OurBaseUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, originalEnabled);
            Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, originalBaseUri);
        }
    }

    // --- LiveGuard.TryRequires: enabled with FQDN -------------------------------------------------

    [Fact]
    public void TryRequires_MasterSwitchOn_FqdnSet_ReturnsTrue()
    {
        var originalEnabled = Environment.GetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar);
        var originalBaseUri = Environment.GetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar);
        Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, "1");
        Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, "https://iris.example.org");

        try
        {
            var result = LiveGuard.TryRequires(out var options);

            Assert.True(result);
            Assert.Equal(new Iri("https://iris.example.org"), options.OurBaseUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveInteropOptions.EnabledEnvVar, originalEnabled);
            Environment.SetEnvironmentVariable(LiveInteropOptions.OurBaseUriEnvVar, originalBaseUri);
        }
    }
}
