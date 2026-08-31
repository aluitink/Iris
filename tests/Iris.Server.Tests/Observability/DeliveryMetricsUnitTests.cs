using Iris.Core;
using Iris.Server.Delivery;
using Iris.Server.Observability;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests.Observability;

/// <summary>
/// Phase 17.2 unit tests for <see cref="IrisDeliveryMetrics"/> — the delivery counters. These test the
/// pure recording logic (no host, no HTTP): drive each recording method and assert the
/// <see cref="IrisDeliveryMetrics.Snapshot"/> reflects the cumulative totals + per-label breakdowns.
/// </summary>
public sealed class DeliveryMetricsUnitTests
{
    [Fact]
    public void Snapshot_Fresh_IsAllZeros()
    {
        var metrics = new IrisDeliveryMetrics();

        var snapshot = metrics.Snapshot;

        Assert.Equal(0, snapshot.Enqueued);
        Assert.Equal(0, snapshot.Delivered);
        Assert.Equal(0, snapshot.AttemptFailed);
        Assert.Equal(0, snapshot.DeadLettered);
        Assert.Empty(snapshot.ByActivityType);
        Assert.Empty(snapshot.ByFailureKind);
    }

    [Fact]
    public void RecordEnqueued_IncrementsTheEnqueuedCounter()
    {
        var metrics = new IrisDeliveryMetrics();

        metrics.RecordEnqueued("Create");
        metrics.RecordEnqueued("Follow");

        var snapshot = metrics.Snapshot;
        Assert.Equal(2, snapshot.Enqueued);
        Assert.Equal(1, snapshot.ByActivityType["Create"].Enqueued);
        Assert.Equal(1, snapshot.ByActivityType["Follow"].Enqueued);
    }

    [Fact]
    public void RecordDelivered_IncrementsTheDeliveredCounter()
    {
        var metrics = new IrisDeliveryMetrics();

        metrics.RecordDelivered("Create");

        var snapshot = metrics.Snapshot;
        Assert.Equal(1, snapshot.Delivered);
        Assert.Equal(1, snapshot.ByActivityType["Create"].Delivered);
    }

    [Fact]
    public void RecordAttemptFailed_IncrementsTheAttemptFailedCounterAndTheFailureKind()
    {
        var metrics = new IrisDeliveryMetrics();

        metrics.RecordAttemptFailed("Create", DeadLetterFailureKind.NonSuccessStatus);
        metrics.RecordAttemptFailed("Follow", DeadLetterFailureKind.TransportError);

        var snapshot = metrics.Snapshot;
        Assert.Equal(2, snapshot.AttemptFailed);
        Assert.Equal(1, snapshot.ByFailureKind["NonSuccessStatus"]);
        Assert.Equal(1, snapshot.ByFailureKind["TransportError"]);
        Assert.Equal(1, snapshot.ByActivityType["Create"].AttemptFailed);
        Assert.Equal(1, snapshot.ByActivityType["Follow"].AttemptFailed);
    }

    [Fact]
    public void RecordDeadLettered_IncrementsTheDeadLetteredCounterAndTheFailureKind()
    {
        var metrics = new IrisDeliveryMetrics();

        metrics.RecordDeadLettered("Create", DeadLetterFailureKind.TransportError);

        var snapshot = metrics.Snapshot;
        Assert.Equal(1, snapshot.DeadLettered);
        Assert.Equal(1, snapshot.ByFailureKind["TransportError"]);
        Assert.Equal(1, snapshot.ByActivityType["Create"].DeadLettered);
    }

    [Fact]
    public void SnapshotChanged_FiresOnEachRecord()
    {
        var metrics = new IrisDeliveryMetrics();
        var fireCount = 0;
        metrics.SnapshotChanged += () => fireCount++;

        metrics.RecordEnqueued("Create");
        metrics.RecordDelivered("Create");
        metrics.RecordAttemptFailed("Create", DeadLetterFailureKind.NonSuccessStatus);
        metrics.RecordDeadLettered("Create", DeadLetterFailureKind.NonSuccessStatus);

        Assert.Equal(4, fireCount);
    }

    [Fact]
    public void Snapshot_ReflectsMultipleActivityTypes()
    {
        var metrics = new IrisDeliveryMetrics();

        metrics.RecordEnqueued("Create");
        metrics.RecordEnqueued("Follow");
        metrics.RecordDelivered("Create");
        metrics.RecordAttemptFailed("Follow", DeadLetterFailureKind.NonSuccessStatus);
        metrics.RecordDeadLettered("Follow", DeadLetterFailureKind.NonSuccessStatus);

        var snapshot = metrics.Snapshot;
        Assert.Equal(2, snapshot.Enqueued);
        Assert.Equal(1, snapshot.Delivered);
        Assert.Equal(1, snapshot.AttemptFailed);
        Assert.Equal(1, snapshot.DeadLettered);

        Assert.Equal(1, snapshot.ByActivityType["Create"].Enqueued);
        Assert.Equal(1, snapshot.ByActivityType["Create"].Delivered);
        Assert.Equal(0, snapshot.ByActivityType["Create"].AttemptFailed);
        Assert.Equal(0, snapshot.ByActivityType["Create"].DeadLettered);

        Assert.Equal(1, snapshot.ByActivityType["Follow"].Enqueued);
        Assert.Equal(0, snapshot.ByActivityType["Follow"].Delivered);
        Assert.Equal(1, snapshot.ByActivityType["Follow"].AttemptFailed);
        Assert.Equal(1, snapshot.ByActivityType["Follow"].DeadLettered);

        // The failure kind aggregates across both attempt-failures and dead-letters.
        Assert.Equal(2, snapshot.ByFailureKind["NonSuccessStatus"]);
    }

    // --- DeliveryService records Enqueued ------------------------------------------------

    [Fact]
    public async Task DeliveryService_Enqueue_RecordsEnqueuedMetric()
    {
        var metrics = new IrisDeliveryMetrics();
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryService(queue, null, null, NullLogger<DeliveryService>.Instance, metrics);

        await service.DeliverAsync(
            new Iri("https://b.test/inbox"),
            new Create { Id = "a/creates/1", Object = [new Note { Id = "a/notes/1", Content = ["hi"] }] });

        var snapshot = metrics.Snapshot;
        Assert.Equal(1, snapshot.Enqueued);
        Assert.Equal(1, snapshot.ByActivityType["Create"].Enqueued);
    }
}
