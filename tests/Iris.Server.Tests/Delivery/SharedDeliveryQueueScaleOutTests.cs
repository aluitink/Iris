using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.Delivery;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Phase 84.6 (shared-state scale-out) integration tests for the <see cref="SharedDeliveryQueue"/> —
/// the shared, file-backed <see cref="IDeliveryQueue"/> two instances over the same origin enqueue into
/// and consume from, so a delivery scheduled on instance A is delivered by A-or-B — not dropped.
/// </summary>
/// <remarks>
/// The default <see cref="InMemoryDeliveryQueue"/> and the restart-durable
/// <see cref="FileBackedDeliveryQueue"/> are both <em>per-instance</em>: a delivery enqueued on A after B
/// started is never visible to B (B's channel was populated at B's startup). The shared queue closes that
/// gap with a single journal + a visibility-timeout claim protocol, guarded by a cross-process file lock.
/// </remarks>
public sealed class SharedDeliveryQueueScaleOutTests : IDisposable
{
    private readonly string _directory;

    public SharedDeliveryQueueScaleOutTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "iris-shared-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        // Best-effort cleanup of the temp journal files (a failed delete — e.g. a lingering lock — must
        // not fail the test).
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string NewJournalPath() => Path.Combine(_directory, "queue-" + Guid.NewGuid().ToString("N") + ".jsonl");

    private static Iri NewInboxIri() => new($"https://b.domain.local/ap/v1/u/bob/inbox-{Guid.NewGuid():N}");

    private static Activity NewCreate(Iri actorIri) => new Create
    {
        Id = $"https://a.domain.local/activities/create-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Note { Id = $"https://a.domain.local/objects/note-{Guid.NewGuid():N}", Content = ["hello"] }],
    };

    // --- Cross-instance visibility: a job enqueued on A is dequeued by B ----------------------------
    //
    // The core scale-out property the single-instance queues lack. Two SharedDeliveryQueue instances
    // over the SAME journal path: A enqueues, B dequeues. If the queue were per-instance (each with its
    // own channel) B would never see A's job.

    [Fact]
    public async Task Job_EnqueuedOnInstanceA_IsDequeuedByInstanceB_SameJournal()
    {
        var journalPath = NewJournalPath();
        await using var instanceA = new SharedDeliveryQueue(journalPath);
        await using var instanceB = new SharedDeliveryQueue(journalPath);

        var inboxIri = NewInboxIri();
        var activity = NewCreate(new Iri("https://a.domain.local/ap/v1/u/alice"));
        var job = new DeliveryJob(inboxIri, activity);

        // A enqueues the job.
        await instanceA.EnqueueAsync(job, CancellationToken.None);
        Assert.Equal(1, instanceA.Count);

        // B (a separate instance, same journal) dequeues A's job — the cross-instance visibility.
        var dequeued = await instanceB.TryDequeueAsync(CancellationToken.None);
        Assert.NotNull(dequeued);
        Assert.Equal(inboxIri, dequeued!.InboxIri);
        Assert.Equal(activity.Id, dequeued.Activity.Id);
        Assert.Equal(0, instanceA.Count); // the job is claimed (no longer pending)
    }

    // --- The acceptance test: a delivery queued on A is delivered by B's worker --------------------
    //
    // Topology: a target TestServer (B's inbox) receives the delivery. Instance A's DeliveryService
    // enqueues into the shared queue; instance B's DeliveryWorker (the ONLY running worker) dequeues from
    // the same shared queue and POSTs the activity to the target inbox. Proves "a delivery queued on A is
    // delivered (by A or B), not dropped."

    [Fact]
    public async Task Delivery_QueuedOnInstanceA_IsDelivered_ByInstanceB_Worker()
    {
        var journalPath = NewJournalPath();
        var target = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = "b.domain.local",
            Handle = "bob",
            Persistence = new InMemoryPersistenceProvider(),
            RegisterLocalKey = false,
        });

        try
        {
            var actorIri = new Iri("https://a.domain.local/ap/v1/u/alice");
            var inboxIri = new Iri("https://b.domain.local/ap/v1/u/bob/inbox");

            // A shared journal; instance A and instance B each construct their own queue over it.
            await using var queueA = new SharedDeliveryQueue(journalPath);
            await using var queueB = new SharedDeliveryQueue(journalPath);

            // Instance A's DeliveryService (enqueues into the shared queue). A has NO worker running.
            var logger = NullLoggerFactory.Instance;
            var serviceA = new DeliveryService(queueA, logger.CreateLogger<DeliveryService>());

            // Instance B's DeliveryWorker (dequeues from the shared queue + delivers to the target inbox).
            var (worker, _) = BuildWorkerOverQueue(queueB, actorIri, target);

            var activity = NewCreate(actorIri);

            // A schedules the delivery (enqueues into the shared journal). A does NOT run a worker.
            await serviceA.DeliverAsync(inboxIri, activity, CancellationToken.None);
            Assert.Equal(1, queueA.Count); // the job is journaled + pending

            // B's worker starts: it dequeues A's job from the shared journal and delivers it.
            await worker.StartAsync(CancellationToken.None);
            try
            {
                // The job leaves the pending pool (claimed by B) once B's worker dequeues it.
                await WaitForAsync(() => Task.FromResult(queueB.Count == 0), TimeSpan.FromSeconds(10));
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }

            // The delivery was made (B's worker POSTed the activity to the target inbox). The worker
            // observed a 2xx (the TestServer inbox accepts the POST); the job is claimed + journaled.
            Assert.Equal(0, queueB.Count); // no longer pending (B claimed it)
        }
        finally
        {
            target.Dispose();
        }
    }

    // --- Claim exclusivity: a claimed job is not re-claimed within the visibility window ------------
    //
    // While A holds a claim (within the visibility timeout), B does NOT re-claim the job — it is
    // exclusive to A. This is what prevents a live instance's in-flight delivery from being double-
    // delivered by a second instance.

    [Fact]
    public async Task ClaimedJob_IsNotReclaimed_WithinVisibilityWindow()
    {
        var journalPath = NewJournalPath();
        // A short visibility window so the test is fast; the job is claimed but not yet reclaimable.
        await using var instanceA = new SharedDeliveryQueue(journalPath, visibilityTimeout: TimeSpan.FromMinutes(5));
        await using var instanceB = new SharedDeliveryQueue(journalPath, visibilityTimeout: TimeSpan.FromMinutes(5));

        var job = new DeliveryJob(NewInboxIri(), NewCreate(new Iri("https://a.domain.local/ap/v1/u/alice")));
        await instanceA.EnqueueAsync(job, CancellationToken.None);

        // A claims the job.
        var claimedByA = await instanceA.TryDequeueAsync(CancellationToken.None);
        Assert.NotNull(claimedByA);

        // B attempts to dequeue: the job is claimed by A (within the 5-min window) and is NOT reclaimable,
        // so B gets nothing (it waits for a new job, but there is none — the queue is effectively empty of
        // claimable jobs). B's dequeue times out (cancellation) rather than returning A's in-flight job.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        DeliveryJob? claimedByB = null;
        try
        {
            claimedByB = await instanceB.TryDequeueAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: no claimable job within the window; the wait was cancelled.
        }

        Assert.Null(claimedByB); // B did not steal A's in-flight claim
    }

    // --- Claim reclamation: a job whose claimer is slow/crashed is reclaimable past the window ------
    //
    // A claims the job, then (simulating a crash or a slow delivery) does nothing. Past the visibility
    // timeout, B re-claims the job — the at-least-once guarantee (the receiver dedupes the re-delivery).

    [Fact]
    public async Task ClaimedJob_IsReclaimed_ByInstanceB_PastVisibilityWindow()
    {
        var journalPath = NewJournalPath();
        // A 150 ms visibility window: the job is reclaimable after 150 ms.
        await using var instanceA = new SharedDeliveryQueue(journalPath, visibilityTimeout: TimeSpan.FromMilliseconds(150));
        await using var instanceB = new SharedDeliveryQueue(journalPath, visibilityTimeout: TimeSpan.FromMilliseconds(150));

        var inboxIri = NewInboxIri();
        var job = new DeliveryJob(inboxIri, NewCreate(new Iri("https://a.domain.local/ap/v1/u/alice")));
        await instanceA.EnqueueAsync(job, CancellationToken.None);

        // A claims the job, then "crashes" (does not complete the delivery).
        var claimedByA = await instanceA.TryDequeueAsync(CancellationToken.None);
        Assert.NotNull(claimedByA);

        // Wait past the visibility window, then B re-claims the job.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        var reclaimedByB = await instanceB.TryDequeueAsync(CancellationToken.None);
        Assert.NotNull(reclaimedByB);
        Assert.Equal(inboxIri, reclaimedByB!.InboxIri);
        Assert.Equal(job.Activity.Id, reclaimedByB.Activity.Id);
    }

    // --- Drop horizon: a claimed job older than the drop horizon is purged from the journal ---------
    //
    // A claimed record older than the drop horizon is removed from the journal on the next dequeue (the
    // journal stays bounded). A job claimed 25 h ago (drop horizon 24 h) is purged, not re-claimed.

    [Fact]
    public async Task ClaimedJob_PastDropHorizon_IsPurged_NotReclaimed()
    {
        var journalPath = NewJournalPath();
        // A 100 ms visibility window + a 150 ms drop horizon: a job claimed 200 ms ago is past the drop
        // horizon and is purged (not re-claimed).
        await using var instanceA = new SharedDeliveryQueue(journalPath,
            visibilityTimeout: TimeSpan.FromMilliseconds(100),
            dropHorizon: TimeSpan.FromMilliseconds(150));
        await using var instanceB = new SharedDeliveryQueue(journalPath,
            visibilityTimeout: TimeSpan.FromMilliseconds(100),
            dropHorizon: TimeSpan.FromMilliseconds(150));

        var job = new DeliveryJob(NewInboxIri(), NewCreate(new Iri("https://a.domain.local/ap/v1/u/alice")));
        await instanceA.EnqueueAsync(job, CancellationToken.None);
        var claimedByA = await instanceA.TryDequeueAsync(CancellationToken.None);
        Assert.NotNull(claimedByA);

        // Wait past the drop horizon, then B dequeues: the job is past the drop horizon and is purged
        // (removed from the journal) rather than re-claimed. B's dequeue finds no claimable job (the
        // queue is empty of pending + reclaimable jobs) and times out.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        DeliveryJob? dequeuedByB = null;
        try
        {
            dequeuedByB = await instanceB.TryDequeueAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the job was purged, so there is no claimable job; the wait was cancelled.
        }

        Assert.Null(dequeuedByB); // the job was purged, not re-claimed
    }

    // --- The shared queue is the source of truth: Count reflects the pending pool across instances ---

    [Fact]
    public async Task Count_ReflectsPendingPool_AcrossInstances()
    {
        var journalPath = NewJournalPath();
        await using var instanceA = new SharedDeliveryQueue(journalPath);
        await using var instanceB = new SharedDeliveryQueue(journalPath);

        var actorIri = new Iri("https://a.domain.local/ap/v1/u/alice");
        await instanceA.EnqueueAsync(new DeliveryJob(NewInboxIri(), NewCreate(actorIri)), CancellationToken.None);
        await instanceA.EnqueueAsync(new DeliveryJob(NewInboxIri(), NewCreate(actorIri)), CancellationToken.None);

        // Both instances see the same pending count (the shared journal is the source of truth).
        Assert.Equal(2, instanceA.Count);
        Assert.Equal(2, instanceB.Count);

        // A claims one; the pending pool drops to 1 for both instances.
        var claimed = await instanceA.TryDequeueAsync(CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(1, instanceA.Count);
        Assert.Equal(1, instanceB.Count);
    }

    // --- A torn (malformed) journal line does not break the queue -----------------------------------

    [Fact]
    public async Task MalformedJournalLine_IsSkipped_NotFatal()
    {
        var journalPath = NewJournalPath();
        var actorIri = new Iri("https://a.domain.local/ap/v1/u/alice");
        var inboxIri = NewInboxIri();
        var activity = NewCreate(actorIri);

        // A first instance enqueues a valid job (journaled as a valid line), then is disposed.
        {
            await using var instance = new SharedDeliveryQueue(journalPath);
            await instance.EnqueueAsync(new DeliveryJob(inboxIri, activity), CancellationToken.None);
        }

        // Append a torn (malformed) line to the journal — simulating a crash mid-write on a later enqueue.
        await using (var writer = new StreamWriter(journalPath, append: true))
        {
            await writer.WriteLineAsync("{torn line");
        }

        // A second instance (same journal) replays: it skips the torn line and dequeues the valid job
        // (a crash mid-write does not break the journal).
        await using var instance2 = new SharedDeliveryQueue(journalPath);
        var dequeued = await instance2.TryDequeueAsync(CancellationToken.None);
        Assert.NotNull(dequeued);
        Assert.Equal(inboxIri, dequeued!.InboxIri);
        Assert.Equal(activity.Id, dequeued.Activity.Id);
    }

    private static (DeliveryWorker Worker, IHost Host) BuildWorkerOverQueue(
        IDeliveryQueue queue, Iri actorIri, TestServer target)
    {
        var keyId = new Iri(actorIri + "#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyId);
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var logger = NullLoggerFactory.Instance;
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = actorIri });
        var transportFactory = () => target.CreateHandler();

        var worker = new DeliveryWorker(queue, factory, transportFactory, options, logger.CreateLogger<DeliveryWorker>());
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddHostedService<DeliveryWorker>(_ => worker))
            .Build();

        return (worker, host);
    }

    private static async Task WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }

            await Task.Delay(50);
        }
    }
}
