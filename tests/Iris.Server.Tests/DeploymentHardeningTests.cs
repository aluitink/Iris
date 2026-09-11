using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Delivery;
using Iris.Server.InMemory;
using Iris.Server.Observability;
using Iris.Server.Stores;
using Iris.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 83.2 — <strong>Deployment hardening + config validation</strong>: verifies that a
/// misconfigured production deployment fails fast at host start (an actionable options-validation
/// error instead of a runtime 500) and that the instance's federation observability (the
/// resolvable-actor count + the delivery dead-letter count) is reported on the health endpoint.
/// </summary>
public sealed class DeploymentHardeningTests
{
    // --------------------------------------------------------------------- config validation

    /// <summary>
    /// Builds a minimal server host from the given <c>Iris:</c> configuration values and returns the
    /// <see cref="TestServer"/>. When <paramref name="expectStartupFailure"/> is true, the host start
    /// is expected to throw (a malformed option) and the thrown <see cref="Exception"/> is returned
    /// instead so the caller can assert on its message.
    /// </summary>
    private static object? BuildHost(
        Dictionary<string, string?> irisConfig,
        bool expectStartupFailure,
        out Exception? startupException)
    {
        startupException = null;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(irisConfig)
            .Build();

        var persistence = new InMemoryPersistenceProvider();

        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(services =>
            {
                services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                services.AddRouting();
                services.AddActivityPubServer(config);
                services.AddInMemoryPersistence();
                services.AddSingleton<IPersistenceProvider>(persistence);
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        try
        {
            var server = new TestServer(builder);
            if (expectStartupFailure)
            {
                Assert.Fail("Expected the host to fail to start, but it started successfully.");
            }

            return server;
        }
        catch (Exception ex)
        {
            startupException = ex;
            if (!expectStartupFailure)
            {
                throw; // an unexpected startup failure — let the test fail with the real cause
            }

            return null;
        }
    }

    [Fact]
    public void MalformedBaseUri_Relative_HostStartupFailsFast()
    {
        object? result = BuildHost(
            new Dictionary<string, string?>
            {
                ["Iris:BaseUri"] = "relative/path", // not an absolute http(s) IRI
            },
            expectStartupFailure: true,
            out Exception? ex);

        Assert.NotNull(ex);
        // Unwrap to the root cause (host start wraps the OptionsValidationException).
        var root = ex!.InnerException ?? ex;
        Assert.IsType<OptionsValidationException>(root);
        Assert.Contains("Iris:BaseUri", root.Message);
        Assert.Contains("absolute http(s) IRI", root.Message);
        Assert.Null(result);
    }

    [Fact]
    public void MalformedInstanceActorId_NonHttpScheme_HostStartupFailsFast()
    {
        object? result = BuildHost(
            new Dictionary<string, string?>
            {
                ["Iris:BaseUri"] = "https://harden.local",
                ["Iris:InstanceActorId"] = "ftp://harden.local/actor", // not http(s)
            },
            expectStartupFailure: true,
            out Exception? ex);

        Assert.NotNull(ex);
        var root = ex!.InnerException ?? ex;
        Assert.IsType<OptionsValidationException>(root);
        Assert.Contains("Iris:InstanceActorId", root.Message);
        Assert.Null(result);
    }

    [Fact]
    public void MalformedSharedInboxIri_Relative_HostStartupFailsFast()
    {
        object? result = BuildHost(
            new Dictionary<string, string?>
            {
                ["Iris:BaseUri"] = "https://harden.local",
                ["Iris:SharedInboxIri"] = "shared-inbox", // relative
            },
            expectStartupFailure: true,
            out Exception? ex);

        Assert.NotNull(ex);
        var root = ex!.InnerException ?? ex;
        Assert.IsType<OptionsValidationException>(root);
        Assert.Contains("Iris:SharedInboxIri", root.Message);
        Assert.Null(result);
    }

    [Fact]
    public void ValidConfig_HostStartsSuccessfully()
    {
        object? result = BuildHost(
            new Dictionary<string, string?>
            {
                ["Iris:BaseUri"] = "https://harden.local",
                ["Iris:InstanceActorId"] = "https://harden.local/ap/v1/u/alice",
                ["Iris:SharedInboxIri"] = "https://harden.local/ap/v1/shared-inbox",
            },
            expectStartupFailure: false,
            out Exception? ex);

        Assert.NotNull(result);
        Assert.Null(ex);
        using var server = Assert.IsType<TestServer>(result);
        // The host started — resolve the validated options to confirm they are usable.
        var options = server.Services.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;
        Assert.Equal(new Iri("https://harden.local"), options.BaseUri);
    }

    [Fact]
    public void AllNullOptions_HostStartsSuccessfully()
    {
        // No Iris: values at all — every option is null, which is allowed (a host may configure a
        // subset and rely on the defaults).
        object? result = BuildHost(
            new Dictionary<string, string?>(),
            expectStartupFailure: false,
            out Exception? ex);

        Assert.NotNull(result);
        Assert.Null(ex);
        using var server = Assert.IsType<TestServer>(result);
        var options = server.Services.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;
        Assert.Null(options.BaseUri);
        Assert.Null(options.InstanceActorId);
        Assert.Null(options.SharedInboxIri);
    }

    // ------------------------------------------------------- observability health check

    /// <summary>
    /// Builds an <see cref="InstanceObservabilityHealthCheck"/> over an in-memory persistence provider
    /// with <paramref name="actorCount"/> stored actors, of which <paramref name="resolvableCount"/>
    /// have a registered (resolvable) signing identity, and <paramref name="deadLetterCount"/>
    /// dead-lettered deliveries.
    /// </summary>
    private static InstanceObservabilityHealthCheck BuildObservabilityCheck(
        int actorCount,
        int resolvableCount,
        int deadLetterCount)
    {
        var persistence = new InMemoryPersistenceProvider();
        var keyStore = new InMemoryKeyStore();
        var keyProvider = new InMemoryKeyProvider(keyStore);

        // Seed actorCount actors, each with a real key in the persistence's key store.
        var seededActorIrises = new List<Iri>(actorCount);
        for (var i = 0; i < actorCount; i++)
        {
            var (_, actorIri, _) = TestSeeder.SeedPersonWithKey(persistence, "obs-test.local", $"actor{i}");
            seededActorIrises.Add(actorIri);
        }

        // Mirror the keys into the standalone key store + register a resolvable signing identity for
        // the first resolvableCount actors (the rest stay stored-but-not-resolvable, modelling a gap).
        for (var i = 0; i < resolvableCount; i++)
        {
            var actorIri = seededActorIrises[i];
            var keyId = new Iri($"{actorIri.Value}#key-1");
            if (persistence.Keys.TryGetKey(keyId, out var key) && key is not null)
            {
                keyStore.PutKey(key);
                keyProvider.RegisterKey(actorIri, keyId);
            }
        }

        // Seed deadLetterCount dead-lettered deliveries.
        var deadLetters = new InMemoryDeliveryDeadLetterStore();
        for (var i = 0; i < deadLetterCount; i++)
        {
            var activity = new KristofferStrube.ActivityStreams.Create
            {
                Object = [new KristofferStrube.ActivityStreams.Link { Href = new Uri($"https://obs-test.local/objects/dead{i}") }],
            };
            var entry = new DeadLetterEntry(
                InboxIri: new Iri($"https://peer{i}.local/inbox"),
                Activity: activity,
                ActorIri: null,
                Attempts: 5,
                FailureKind: DeadLetterFailureKind.NonSuccessStatus,
                FailureDetail: "503 Service Unavailable",
                DeadLetteredAtUtc: DateTimeOffset.UtcNow);
            deadLetters.AddAsync(entry).GetAwaiter().GetResult();
        }

        return new InstanceObservabilityHealthCheck(persistence, keyProvider, deadLetters);
    }

    [Fact]
    public async Task Observability_AllActorsResolvable_NoDeadLetters_ReportsHealthyWithCounts()
    {
        // 3 stored actors, all 3 resolvable, 0 dead letters.
        var check = BuildObservabilityCheck(actorCount: 3, resolvableCount: 3, deadLetterCount: 0);

        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, result.Status);
        Assert.Equal(3, Assert.IsAssignableFrom<Dictionary<string, object>>(result.Data)!["stored_actors"]);
        Assert.Equal(3, result.Data!["resolvable_actors"]);
        Assert.Equal(0, result.Data!["dead_letters"]);
    }

    [Fact]
    public async Task Observability_SomeActorsNotResolvable_ReportsHealthyWithGap()
    {
        // 5 stored actors, only 2 resolvable, 0 dead letters → healthy (no dead letters) but the
        // data surfaces the 2/5 resolvable gap.
        var check = BuildObservabilityCheck(actorCount: 5, resolvableCount: 2, deadLetterCount: 0);

        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, result.Status);
        Assert.Equal(5, result.Data!["stored_actors"]);
        Assert.Equal(2, result.Data!["resolvable_actors"]);
        Assert.Equal(0, result.Data!["dead_letters"]);
    }

    [Fact]
    public async Task Observability_DeadLettersPresent_ReportsDegraded()
    {
        // 2 stored actors (1 resolvable), 4 dead letters → degraded (a failed-delivery backlog).
        var check = BuildObservabilityCheck(actorCount: 2, resolvableCount: 1, deadLetterCount: 4);

        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, result.Status);
        Assert.Equal(4, result.Data!["dead_letters"]);
        Assert.Contains("dead-lettered", result.Description);
    }

    // ------------------------------------------------------------- validator unit coverage

    [Theory]
    [InlineData("https://valid.local", "https://valid.local/actor", "https://valid.local/inbox", true)]
    [InlineData("https://valid.local", "relative/actor", "https://valid.local/inbox", false)]
    [InlineData("relative/base", "https://valid.local/actor", "https://valid.local/inbox", false)]
    [InlineData("https://valid.local", "https://valid.local/actor", "not-an-iri", false)]
    [InlineData(null, null, null, true)] // all null → allowed
    public void Validator_AcceptsAndRejectsAsExpected(
        string? baseUri,
        string? instanceActorId,
        string? sharedInboxIri,
        bool expectedValid)
    {
        var options = new ActivityPubServerOptions
        {
            BaseUri = baseUri is { } b ? new Iri(b) : null,
            InstanceActorId = instanceActorId is { } a ? new Iri(a) : null,
            SharedInboxIri = sharedInboxIri is { } s ? new Iri(s) : null,
        };

        var validator = new ActivityPubServerOptionsValidator();
        var result = validator.Validate("test", options);

        Assert.Equal(expectedValid, result.Succeeded);
    }
}
