using Iris.Client;
using Iris.Client.Auth;
using Iris.Core.Identity;
using Iris.Server.Delivery;
using Iris.Server.InMemory;
using Iris.Server.InMemory.Stores;
using Iris.Server.Observability;
using Iris.Server.Stores;
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

    // --- PersistenceHealthCheck -----------------------------------------------------

    [Fact]
    public async Task PersistenceHealth_StoreReachable_Healthy()
    {
        var options = Options.Create(new ActivityPubServerOptions
        {
            InstanceActorId = new Iri("https://a.domain.local/ap/v1/u/alice"),
        });
        var check = new PersistenceHealthCheck(new TestPersistenceProvider(new InMemoryActorStore()), options);

        // The actor is not stored, but the store answered (reachable) — not a persistence fault.
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.False((bool)result.Data!["actor_stored"]);
    }

    [Fact]
    public async Task PersistenceHealth_ActorStored_HealthyWithActorStored()
    {
        var actorIri = new Iri("https://a.domain.local/ap/v1/u/alice");
        var store = new InMemoryActorStore();
        await store.PutActorAsync(new Person { Id = actorIri.Value });
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = actorIri });
        var check = new PersistenceHealthCheck(new TestPersistenceProvider(store), options);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True((bool)result.Data!["actor_stored"]);
    }

    [Fact]
    public async Task PersistenceHealth_StoreThrows_Unhealthy()
    {
        var options = Options.Create(new ActivityPubServerOptions
        {
            InstanceActorId = new Iri("https://a.domain.local/ap/v1/u/alice"),
        });
        var check = new PersistenceHealthCheck(new TestPersistenceProvider(new ThrowingActorStore()), options);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    // --- DeliveryWorkerHealthCheck ----------------------------------------------------

    [Fact]
    public async Task DeliveryWorkerHealth_NoWorker_Unhealthy()
    {
        var check = new DeliveryWorkerHealthCheck(Array.Empty<Microsoft.Extensions.Hosting.IHostedService>());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task DeliveryWorkerHealth_WorkerNotStarted_Degraded()
    {
        // A freshly-constructed DeliveryWorker (ExecuteAsync never ran) has not started pumping.
        var worker = new DeliveryWorker(
            new InMemoryDeliveryQueue(),
            new NoopClientFactory(),
            () => new HttpClientHandler(),
            Options.Create(new ActivityPubServerOptions
            {
                InstanceActorId = new Iri("https://a.domain.local/ap/v1/u/alice"),
            }),
            NullLogger<DeliveryWorker>.Instance);
        var check = new DeliveryWorkerHealthCheck([worker]);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task DeliveryWorkerHealth_WorkerStarted_Healthy()
    {
        // A DeliveryWorker whose ExecuteAsync ran (with a configured instance actor) has started pumping.
        var worker = new DeliveryWorker(
            new InMemoryDeliveryQueue(),
            new NoopClientFactory(),
            () => new HttpClientHandler(),
            Options.Create(new ActivityPubServerOptions
            {
                InstanceActorId = new Iri("https://a.domain.local/ap/v1/u/alice"),
            }),
            NullLogger<DeliveryWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // StartAsync returns before the background ExecuteAsync task reaches the _running = true line,
            // so poll until the pump has actually started (the flag is set at the top of ExecuteAsync).
            await Iris.Testing.TestFederation.WaitForAsync(
                () => Task.FromResult(worker.IsRunning),
                TimeSpan.FromSeconds(2));
            Assert.True(worker.IsRunning);

            var check = new DeliveryWorkerHealthCheck([worker]);
            HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // --- DefaultReadinessGate ---------------------------------------------------------

    [Fact]
    public async Task Readiness_NoActor_NotReady()
    {
        var gate = new DefaultReadinessGate(
            new InMemoryKeyProvider(new InMemoryKeyStore()),
            new InMemoryKeyStore(),
            Options.Create(new ActivityPubServerOptions()));

        Assert.False(await gate.IsReadyAsync());
    }

    [Fact]
    public async Task Readiness_KeyNotRegistered_NotReady()
    {
        var keyStore = new InMemoryKeyStore();
        var provider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri("https://a.domain.local/ap/v1/u/alice");
        var keyId = new Iri($"{actorIri}#key-1");
        keyStore.PutKey(KeyPairGenerator.GenerateRsa(keyId)); // key present, but not registered
        var gate = new DefaultReadinessGate(
            provider,
            keyStore,
            Options.Create(new ActivityPubServerOptions { InstanceActorId = actorIri }));

        Assert.False(await gate.IsReadyAsync());
    }

    [Fact]
    public async Task Readiness_KeyRegisteredAndPresent_Ready()
    {
        var keyStore = new InMemoryKeyStore();
        var provider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri("https://a.domain.local/ap/v1/u/alice");
        var keyId = new Iri($"{actorIri}#key-1");
        keyStore.PutKey(KeyPairGenerator.GenerateRsa(keyId));
        provider.RegisterKey(actorIri, keyId);
        var gate = new DefaultReadinessGate(
            provider,
            keyStore,
            Options.Create(new ActivityPubServerOptions { InstanceActorId = actorIri }));

        Assert.True(await gate.IsReadyAsync());
    }

    [Fact]
    public async Task Readiness_KeyRegisteredButMissingFromStore_NotReady()
    {
        // The identity is registered but the key has been removed from the store (e.g. rotation): not ready.
        var keyStore = new InMemoryKeyStore();
        var provider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri("https://a.domain.local/ap/v1/u/alice");
        var keyId = new Iri($"{actorIri}#key-1");
        keyStore.PutKey(KeyPairGenerator.GenerateRsa(keyId));
        provider.RegisterKey(actorIri, keyId);
        keyStore.RemoveKey(keyId);
        var gate = new DefaultReadinessGate(
            provider,
            keyStore,
            Options.Create(new ActivityPubServerOptions { InstanceActorId = actorIri }));

        Assert.False(await gate.IsReadyAsync());
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

    // --- test seams --------------------------------------------------------------------

    /// <summary>
    /// A minimal <see cref="IPersistenceProvider"/> exposing only an
    /// <see cref="IActorStore"/> (the <see cref="PersistenceHealthCheck"/> reads
    /// <c>IPersistenceProvider.Actors</c>; every other store seam throws if touched).
    /// </summary>
    private sealed class TestPersistenceProvider(Iris.Server.Stores.IActorStore actors) :
        Iris.Server.Stores.IPersistenceProvider
    {
        public Iris.Server.Stores.IActorStore Actors => actors;

        public Iris.Server.Stores.IActivityStore Activities => throw new NotSupportedException();
        public Iris.Server.Stores.IFollowStore Follows => throw new NotSupportedException();
        public Iris.Server.Stores.ILikeStore Likes => throw new NotSupportedException();
        public Iris.Server.Stores.IAnnounceStore Announces => throw new NotSupportedException();
        public Iris.Server.Stores.IReplyStore Replies => throw new NotSupportedException();
        public Iris.Server.Stores.IModerationStore Moderation => throw new NotSupportedException();
        public Iris.Server.Stores.IRelayStore Relays => throw new NotSupportedException();
        public Iris.Server.Stores.IObjectStore Objects => throw new NotSupportedException();
        public Iris.Server.Stores.ICreateIndex Creates => throw new NotSupportedException();
        public Iris.Server.Stores.ICommunityStore Communities => throw new NotSupportedException();
        public IKeyStore Keys => throw new NotSupportedException();
        public Iris.Server.Stores.IMediaStore Media => throw new NotSupportedException();
    }

    /// <summary>
    /// An <see cref="Iris.Server.Stores.IActorStore"/> whose read throws (stands in for an unreachable
    /// persistence backing store) — exercises <see cref="PersistenceHealthCheck"/>'s fault path.
    /// </summary>
    private sealed class ThrowingActorStore : Iris.Server.Stores.IActorStore
    {
        public Task<bool> TryGetActorAsync(Iri actorIri, out Actor? actor, CancellationToken ct = default)
            => throw new InvalidOperationException("persistence unreachable");

        public Task PutActorAsync(Actor actor, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> RemoveActorAsync(Iri actorIri, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<Actor>> ListActorsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Actor>>([]);
    }

    /// <summary>
    /// An <see cref="IActivityPubClientFactory"/> that never builds a client (the
    /// <see cref="DeliveryWorker"/> unit tests only need the worker to start its pump; no delivery occurs).
    /// </summary>
    private sealed class NoopClientFactory : IActivityPubClientFactory
    {
        public IActivityPubClient Create(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
            => throw new NotSupportedException("NoopClientFactory does not build clients.");

        public ILocalModerationClient CreateLocalModerationClient(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
            => throw new NotSupportedException("NoopClientFactory does not build clients.");

        public IMediaClient CreateMediaClient(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
            => throw new NotSupportedException("NoopClientFactory does not build clients.");
    }
}
