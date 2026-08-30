using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Delivery;
using Iris.Server.InMemory;
using Iris.Server.Observability;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Observability;

/// <summary>
/// Phase 17.1 integration tests for the <c>GET /ap/v1/health</c> observability endpoint: a healthy
/// instance returns 200 with an aggregate <c>status</c> of <c>healthy</c> and per-check entries; a
/// delivery backlog at the warning threshold degrades the aggregate to <c>degraded</c> (still 200); and
/// a backlog at the critical threshold makes the endpoint return 503 with <c>status</c> of
/// <c>unhealthy</c>. The endpoint is public (no ActivityPub signature) so a load balancer or
/// orchestrator probe can reach it.
/// </summary>
public sealed class HealthEndpointIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Base = $"https://{AHost}";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly FixedCountQueue _queue;

    public HealthEndpointIntegrationTests()
    {
        var persistence = new InMemoryPersistenceProvider();
        _queue = new FixedCountQueue();
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
            // Bind a test-controlled queue (the default InMemoryDeliveryQueue would be drained by the
            // hosted DeliveryWorker, racing the test's assertion on the pending count). The fixed-count
            // queue exposes a settable Count and a TryDequeueAsync that never returns a job, so the
            // worker blocks (never drains) and the health check reports exactly the count the test sets.
            // Tighten the thresholds so the test's small backlogs cross the warning/critical lines.
            ExtraServices = s =>
            {
                s.AddSingleton<IDeliveryQueue>(_queue);
                s.AddSingleton(new DeliveryQueueHealthOptions { WarningPending = 5, CriticalPending = 20 });
            },
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task Health_EmptyQueue_Returns200Healthy()
    {
        HttpResponseMessage response = await _http.GetAsync($"{Base}/ap/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
        // The two registered checks are present.
        var checks = doc.RootElement.GetProperty("checks");
        Assert.True(checks.TryGetProperty("InstanceHealthCheck", out _));
        Assert.True(checks.TryGetProperty("DeliveryQueueHealthCheck", out _));
        Assert.Equal("healthy", checks.GetProperty("DeliveryQueueHealthCheck").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_PendingAtWarning_Returns200Degraded()
    {
        _queue.Pending = 5; // exactly the warning threshold

        HttpResponseMessage response = await _http.GetAsync($"{Base}/ap/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("degraded", doc.RootElement.GetProperty("status").GetString());
        var queueCheck = doc.RootElement.GetProperty("checks").GetProperty("DeliveryQueueHealthCheck");
        Assert.Equal("degraded", queueCheck.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_PendingAtCritical_Returns503Unhealthy()
    {
        _queue.Pending = 20; // exactly the critical threshold

        HttpResponseMessage response = await _http.GetAsync($"{Base}/ap/v1/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unhealthy", doc.RootElement.GetProperty("status").GetString());
        var queueCheck = doc.RootElement.GetProperty("checks").GetProperty("DeliveryQueueHealthCheck");
        Assert.Equal("unhealthy", queueCheck.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_ResponseCarriesVersionHeader()
    {
        // The group-level endpoint filter adds the Iris-Version header to every /ap/v1 response.
        HttpResponseMessage response = await _http.GetAsync($"{Base}/ap/v1/health");

        Assert.True(response.Headers.TryGetValues("Iris-Version", out var values));
        Assert.Contains("1", values!);
    }

    /// <summary>
    /// A test-controlled <see cref="IDeliveryQueue"/>: a settable <see cref="Pending"/> count and a
    /// <see cref="TryDequeueAsync"/> that never yields a job (so the hosted <c>DeliveryWorker</c> blocks
    /// and never drains the "backlog" the test sets). This isolates the health endpoint's reporting from
    /// the worker's real-time draining of the default in-memory queue.
    /// </summary>
    private sealed class FixedCountQueue : IDeliveryQueue
    {
        public int Pending { get; set; }

        public int Count => Pending;

        public Task EnqueueAsync(DeliveryJob job, CancellationToken ct = default)
            => Task.CompletedTask; // no-op: the count is set directly

        public Task<DeliveryJob?> TryDequeueAsync(CancellationToken ct = default)
            => Task.FromResult<DeliveryJob?>(null); // never yields a job (worker blocks / sees empty)

        public Task CompleteAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
