using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Observability;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Observability;

/// <summary>
/// Phase 83.4 — <strong>Graceful degradation / read-only mode on store-unavailable</strong>: the server
/// starts in a degraded (read-only) mode when its durable store is unreachable (not crash), surfaces it,
/// and recovers. A degraded instance serves <em>reads</em> but refuses <em>writes</em> (inbound + outbound
/// federation activities) with <c>503 Service Unavailable</c> rather than throwing.
/// </summary>
public sealed class DegradedModeTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";

    private readonly InMemoryPersistenceProvider _persistence;
    private readonly TestServer _server;
    private readonly HttpClient _http;

    public DegradedModeTests()
    {
        // Seed a real local actor (via the shared TestSeeder) so the actor-document read path does not
        // fall through to a remote fetch (which would hang on an unresolvable host). The factory binds this
        // persistence provider as the host's IPersistenceProvider seam.
        _persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPerson(_persistence, Host, Handle);

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = _persistence,
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false)
        {
            BaseAddress = new Uri($"https://{Host}"),
        };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // ------------------------------------------------------- gate: stateful + thread-safe

    [Fact]
    public void DefaultGate_MarkAndClear_IsStateful()
    {
        var gate = new DefaultDegradedModeGate();

        Assert.False(gate.IsDegraded); // default: read-write

        gate.MarkDegraded();
        Assert.True(gate.IsDegraded);

        gate.ClearDegraded();
        Assert.False(gate.IsDegraded);

        gate.MarkDegraded();
        Assert.True(gate.IsDegraded); // re-enterable
    }

    // ------------------------------------------------------- probe: failing store -> degraded

    [Fact]
    public async Task Probe_FailingStore_StartupProbe_MarksDegraded()
    {
        var gate = new DefaultDegradedModeGate();
        var probe = new PersistenceDegradedModeProbe(
            new FailingPersistenceProvider(),
            Options.Create(new ActivityPubServerOptions { InstanceActorId = new Iri($"https://{Host}/ap/v1/u/{Handle}") }),
            gate,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PersistenceDegradedModeProbe>.Instance,
            recheckInterval: TimeSpan.FromSeconds(1));

        await probe.StartAsync(CancellationToken.None);

        // The startup probe runs at the start of ExecuteAsync; poll briefly until the gate flips.
        await WaitUntilAsync(() => gate.IsDegraded, TimeSpan.FromSeconds(5));
        await probe.StopAsync(CancellationToken.None);

        Assert.True(gate.IsDegraded); // the failing store drove the instance into degraded mode
    }

    // ------------------------------------------------------- probe: healthy store -> recovers

    [Fact]
    public async Task Probe_HealthyStore_PreDegradedGate_ClearsDegraded()
    {
        // The store is healthy (in-memory) but the gate was pre-marked degraded (e.g. a prior outage). The
        // startup probe's successful read clears the gate — the instance recovers to read-write.
        var gate = new DefaultDegradedModeGate();
        gate.MarkDegraded();

        var probe = new PersistenceDegradedModeProbe(
            new InMemoryPersistenceProvider(),
            Options.Create(new ActivityPubServerOptions { InstanceActorId = new Iri($"https://{Host}/ap/v1/u/{Handle}") }),
            gate,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PersistenceDegradedModeProbe>.Instance,
            recheckInterval: TimeSpan.FromSeconds(1));

        await probe.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => !gate.IsDegraded, TimeSpan.FromSeconds(5));
        await probe.StopAsync(CancellationToken.None);

        Assert.False(gate.IsDegraded); // recovered to read-write
    }

    // ------------------------------------------------- endpoint: degraded -> writes refused (503)

    [Fact]
    public async Task DegradedInstance_InboxWrite_IsRefusedWith503()
    {
        // Manually mark the instance degraded (read-only) — simulating the probe's effect (the store is
        // healthy here, but the degraded gate is what the write path consults). The inbox's degraded check
        // runs before signature validation, so an unsigned POST still gets 503 (not 401) when degraded.
        _server.Services.GetRequiredService<IDegradedModeGate>().MarkDegraded();

        var activity = new KristofferStrube.ActivityStreams.Create
        {
            Id = $"https://{Host}/activities/degraded-1",
            Actor = [new Link { Href = new Uri($"https://{Host}/ap/v1/u/{Handle}") }],
            Object = [new Note { Id = $"https://{Host}/notes/degraded-1", Content = ["should be refused"] }],
        };
        var body = new StringContent(
            ActivityJson.Serialize(activity),
            System.Text.Encoding.UTF8,
            "application/activity+json");

        var response = await _http.PostAsync($"/ap/v1/u/{Handle}/inbox", body);

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("degraded", json); // the response names the degraded mode (so a peer retries)
    }

    [Fact]
    public async Task DegradedInstance_ReadStillServed_NotRefused()
    {
        // Degraded (read-only) gates WRITES only — reads are still served. With the gate marked degraded
        // (and a seeded, healthy store), a read (the local actor's own document) still succeeds; only a
        // write is refused. This is the "read-only" half of the contract.
        _server.Services.GetRequiredService<IDegradedModeGate>().MarkDegraded();

        var response = await _http.GetAsync($"/ap/v1/u/{Handle}");
        Assert.True(response.IsSuccessStatusCode,
            $"Expected reads to still be served in degraded mode, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task DegradedInstance_OutboxWrite_RequiresSignature_First()
    {
        // The outbox handler's degraded check runs AFTER signature validation (precedence: auth first, so a
        // degraded instance never opens the write surface to unauthenticated clients). An unsigned outbox
        // POST therefore still 401s even when degraded — pinning that the 503 write-refusal applies to a
        // validly-signed write (the inbox test demonstrates the 503 contract at the write-surface seam,
        // where the degraded check precedes signature validation).
        _server.Services.GetRequiredService<IDegradedModeGate>().MarkDegraded();

        var activity = new KristofferStrube.ActivityStreams.Note
        {
            Id = $"https://{Host}/notes/degraded-outbox",
            Content = ["should be refused"],
        };
        var body = new StringContent(
            ActivityJson.Serialize(activity),
            System.Text.Encoding.UTF8,
            "application/activity+json");

        var response = await _http.PostAsync($"/ap/v1/u/{Handle}/outbox", body);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------------------

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// An <see cref="IPersistenceProvider"/> whose <see cref="IActorStore"/> throws on every read —
    /// simulating a down / unreachable durable store (the fault the <see cref="PersistenceDegradedModeProbe"/>
    /// detects). The other stores are in-memory (unused by the probe, which only reads <see cref="Actors"/>).
    /// </summary>
    private sealed class FailingPersistenceProvider : IPersistenceProvider
    {
        private readonly InMemoryPersistenceProvider _inner = new();

        public IActorStore Actors { get; } = new FailingActorStore();
        public IActivityStore Activities => _inner.Activities;
        public IFollowStore Follows => _inner.Follows;
        public ILikeStore Likes => _inner.Likes;
        public IReplyStore Replies => _inner.Replies;
        public IAnnounceStore Announces => _inner.Announces;
        public IModerationStore Moderation => _inner.Moderation;
        public IRelayStore Relays => _inner.Relays;
        public IObjectStore Objects => _inner.Objects;
        public ICreateIndex Creates => _inner.Creates;
        public ICommunityStore Communities => _inner.Communities;
        public IKeyStore Keys => _inner.Keys;
        public IMediaStore Media => _inner.Media;
    }

    /// <summary>
    /// An <see cref="IActorStore"/> that throws <see cref="InvalidOperationException"/> on
    /// <see cref="TryGetActorAsync"/> — the read the probe performs. A throw (not a "not found") is what
    /// signals an unreachable store (a missing actor is a configuration state, not a fault).
    /// </summary>
    private sealed class FailingActorStore : IActorStore
    {
        public Task<bool> TryGetActorAsync(Iri actorIri, out Actor? actor, CancellationToken ct = default)
        {
            actor = null;
            throw new InvalidOperationException("simulated unreachable persistence store");
        }

        public Task PutActorAsync(Actor actor, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> RemoveActorAsync(Iri actorIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Actor>> ListActorsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Actor>> SearchActorsAsync(string? query, int limit, int offset, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> CountSearchMatchesAsync(string? query, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
