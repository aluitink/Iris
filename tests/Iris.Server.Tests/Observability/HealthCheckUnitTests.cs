using Iris.Server.Delivery;
using Iris.Server.Observability;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Observability;

/// <summary>
/// Phase 17.1 unit tests for the health-check implementations and the graceful-shutdown service — the
/// pure-logic half of the observability slice (the endpoint's wiring is covered by
/// <see cref="HealthEndpointIntegrationTests"/>).
/// </summary>
public sealed class HealthCheckUnitTests
{
    // --- DeliveryQueueHealthCheck -----------------------------------------------------

    [Fact]
    public async Task DeliveryQueueHealth_EmptyQueue_Healthy()
    {
        var check = new DeliveryQueueHealthCheck(new InMemoryDeliveryQueue(), new DeliveryQueueHealthOptions());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(0, result.Data!["pending"]);
    }

    [Fact]
    public async Task DeliveryQueueHealth_PendingBelowWarning_Healthy()
    {
        var queue = new InMemoryDeliveryQueue();
        await EnqueueN(queue, 5);
        var check = new DeliveryQueueHealthCheck(queue, new DeliveryQueueHealthOptions { WarningPending = 10 });

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(5, result.Data!["pending"]);
    }

    [Fact]
    public async Task DeliveryQueueHealth_PendingAtWarning_Degraded()
    {
        var queue = new InMemoryDeliveryQueue();
        await EnqueueN(queue, 10);
        var check = new DeliveryQueueHealthCheck(queue, new DeliveryQueueHealthOptions { WarningPending = 10 });

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(10, result.Data!["pending"]);
    }

    [Fact]
    public async Task DeliveryQueueHealth_PendingAtCritical_Unhealthy()
    {
        var queue = new InMemoryDeliveryQueue();
        await EnqueueN(queue, 100);
        var check = new DeliveryQueueHealthCheck(queue, new DeliveryQueueHealthOptions
        {
            WarningPending = 10,
            CriticalPending = 100,
        });

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(100, result.Data!["pending"]);
    }

    [Fact]
    public async Task DeliveryQueueHealth_ThresholdsDisabled_DefaultsHealthy()
    {
        // Both thresholds at 0 (the default) → any finite count is healthy.
        var queue = new InMemoryDeliveryQueue();
        await EnqueueN(queue, 500);
        var check = new DeliveryQueueHealthCheck(queue, new DeliveryQueueHealthOptions());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // --- InstanceHealthCheck ----------------------------------------------------------

    [Fact]
    public async Task InstanceHealth_ConfiguredInstance_Healthy()
    {
        var options = Options.Create(new ActivityPubServerOptions
        {
            InstanceName = "iris-test",
            InstanceActorId = new Iri("https://a.domain.local/ap/v1/u/alice"),
        });
        var check = new InstanceHealthCheck(options);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("iris-test", result.Data!["instance"]);
        Assert.Equal("https://a.domain.local/ap/v1/u/alice", result.Data!["actor"]);
    }

    [Fact]
    public async Task InstanceHealth_MissingActor_Unhealthy()
    {
        var options = Options.Create(new ActivityPubServerOptions { InstanceName = "iris-test" });
        var check = new InstanceHealthCheck(options);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // --- DeliveryQueueShutdownService -------------------------------------------------

    [Fact]
    public async Task ShutdownService_StopAsync_CompletesTheQueue()
    {
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryQueueShutdownService(queue, NullLogger<DeliveryQueueShutdownService>.Instance);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        // After completion, an empty queue's TryDequeueAsync returns null (the worker can stop).
        DeliveryJob? job = await queue.TryDequeueAsync(CancellationToken.None);
        Assert.Null(job);
    }

    [Fact]
    public async Task ShutdownService_StartAsync_IsNoOp()
    {
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryQueueShutdownService(queue, NullLogger<DeliveryQueueShutdownService>.Instance);

        // StartAsync must not complete the queue (the queue stays open for enqueues).
        await service.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(MakeJob("https://a.test/inbox"));

        // The queue is NOT complete: TryDequeueAsync returns the enqueued job (not null).
        DeliveryJob? job = await queue.TryDequeueAsync(CancellationToken.None);
        Assert.NotNull(job);
    }

    // --- helpers -----------------------------------------------------------------------

    private static Task EnqueueN(IDeliveryQueue queue, int n)
    {
        for (var i = 0; i < n; i++)
        {
            queue.EnqueueAsync(MakeJob($"https://a.test/inbox/{i}")).GetAwaiter().GetResult();
        }

        return Task.CompletedTask;
    }

    private static DeliveryJob MakeJob(string inbox)
        => new(new Iri(inbox), BuildCreate($"obj-{inbox}"));

    private static Activity BuildCreate(string id) => new Create
    {
        Id = id,
        Object = [new Note { Id = id, Content = [$"note {id}"] }],
    };
}
