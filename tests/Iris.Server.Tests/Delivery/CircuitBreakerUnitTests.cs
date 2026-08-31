using Iris.Core;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Phase 17.3 unit tests for the <see cref="PerPeerDeliveryCircuitBreaker"/>: the per-peer circuit
/// breaker transitions closed → open → half-open → closed based on consecutive delivery failures.
/// </summary>
/// <remarks>
/// These tests drive the breaker directly (not through the worker) to verify the state machine:
/// a peer's circuit opens after <c>FailureThreshold</c> consecutive failures, stays open for
/// <c>OpenDuration</c>, transitions to half-open (allowing one probe), and closes on a probe success
/// (or re-opens on a probe failure). A disabled breaker (threshold 0) is a no-op.
/// </remarks>
public sealed class CircuitBreakerUnitTests
{
    private static readonly Iri AliceInbox = new("https://a.example/ap/v1/u/alice/inbox");
    private static readonly Iri BobInbox = new("https://b.example/ap/v1/u/bob/inbox");

    // --- A disabled breaker (threshold 0) is a no-op --------------------------------------------

    [Fact]
    public async Task DisabledBreaker_AlwaysPermits_NoStateTracking()
    {
        var breaker = new PerPeerDeliveryCircuitBreaker(failureThreshold: 0, openDuration: TimeSpan.FromMinutes(1));

        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
        Assert.True(await breaker.TryAcquireAsync(BobInbox, CancellationToken.None));

        // Record failures — a disabled breaker ignores them.
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        // Still permitted.
        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
    }

    [Fact]
    public void DisabledBreaker_ThrowsOnNegativeThreshold()
    {
        Assert.Throws<ArgumentException>(() => new PerPeerDeliveryCircuitBreaker(-1, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void EnabledBreaker_ThrowsOnNegativeOpenDuration()
    {
        Assert.Throws<ArgumentException>(() => new PerPeerDeliveryCircuitBreaker(3, TimeSpan.FromSeconds(-1)));
    }

    // --- Closed state: failures below the threshold are permitted --------------------------------

    [Fact]
    public async Task ClosedState_FailuresBelowThreshold_StaysClosed()
    {
        var breaker = new PerPeerDeliveryCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(1));

        // 2 failures (below threshold of 3): circuit stays closed.
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
    }

    [Fact]
    public async Task ClosedState_Success_ResetsFailureCount()
    {
        var breaker = new PerPeerDeliveryCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(1));

        // 2 failures, then a success (resets the count), then 2 more failures — still below threshold.
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);
        await breaker.RecordSuccessAsync(AliceInbox, CancellationToken.None);
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        // 2 consecutive failures (the count was reset by the success): circuit stays closed.
        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
    }

    // --- Open state: threshold failures opens the circuit ----------------------------------------

    [Fact]
    public async Task OpenState_ThresholdFailures_OpensCircuit()
    {
        var breaker = new PerPeerDeliveryCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(1));

        // 3 consecutive failures (at threshold): circuit opens.
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        // Circuit is open: delivery is not permitted.
        Assert.False(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
    }

    [Fact]
    public async Task OpenState_IsPerPeer_OtherPeersUnaffected()
    {
        var breaker = new PerPeerDeliveryCircuitBreaker(failureThreshold: 2, openDuration: TimeSpan.FromMinutes(1));

        // Open Alice's circuit.
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        // Alice is open, Bob is unaffected.
        Assert.False(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
        Assert.True(await breaker.TryAcquireAsync(BobInbox, CancellationToken.None));
    }

    // --- Half-open state: after OpenDuration, a probe is allowed ---------------------------------

    [Fact]
    public async Task HalfOpenState_AfterOpenDuration_AllowsSingleProbe()
    {
        // OpenDuration of 0 means the circuit transitions to half-open immediately.
        var breaker = new PerPeerDeliveryCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.Zero);

        // Open the circuit (1 failure at threshold 1).
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        // OpenDuration is 0, so the circuit is already half-open: one probe is allowed.
        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
    }

    [Fact]
    public async Task HalfOpenState_SingleProbe_SecondDeliveryDenied()
    {
        var breaker = new PerPeerDeliveryCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.Zero);

        // Open the circuit.
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        // First probe is allowed (half-open).
        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));

        // A second delivery while the probe is in flight is denied.
        Assert.False(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
    }

    [Fact]
    public async Task HalfOpenState_ProbeSuccess_ClosesCircuit()
    {
        var breaker = new PerPeerDeliveryCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.Zero);

        // Open the circuit.
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        // Probe is allowed (half-open).
        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));

        // Probe succeeds: circuit closes.
        await breaker.RecordSuccessAsync(AliceInbox, CancellationToken.None);

        // Circuit is closed: deliveries are permitted again.
        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
    }

    [Fact]
    public async Task HalfOpenState_ProbeFailure_ReOpensCircuit()
    {
        var breaker = new PerPeerDeliveryCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.Zero);

        // Open the circuit.
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        // Probe is allowed (half-open).
        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));

        // Probe fails: circuit re-opens (OpenDuration = 0, so it's immediately half-open again).
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        // A new probe is allowed (half-open again).
        Assert.True(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
    }

    // --- Open state: stays open while OpenDuration has not elapsed ------------------------------

    [Fact]
    public async Task OpenState_BeforeOpenDuration_StaysOpen()
    {
        // OpenDuration of 10 minutes: the circuit stays open for 10 minutes.
        var breaker = new PerPeerDeliveryCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMinutes(10));

        // Open the circuit.
        await breaker.RecordFailureAsync(AliceInbox, CancellationToken.None);

        // Immediately after: circuit is open (OpenDuration has not elapsed).
        Assert.False(await breaker.TryAcquireAsync(AliceInbox, CancellationToken.None));
    }
}
