using Iris.Core;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Phase 16.2 tests for the persistent, file-backed <see cref="FileBackedDeliveryQueue"/> and
/// <see cref="FileBackedDeliveryDeadLetterStore"/>: pending outbound deliveries (and dead-lettered
/// deliveries) survive a process restart — a new queue/store constructed over the same journal file
/// replays the entries that were pending when the previous process stopped. The default
/// <see cref="InMemoryDeliveryQueue"/> / <see cref="InMemoryDeliveryDeadLetterStore"/> are ephemeral;
/// these file-backed implementations are the production-persistence swap for the
/// <see cref="IDeliveryQueue"/> / <see cref="IDeliveryDeadLetterStore"/> seams.
/// </summary>
/// <remarks>
/// A "restart" is simulated by disposing one queue/store and constructing a fresh one over the same
/// journal path — exactly what happens when a host process stops and starts. The journal is a
/// line-delimited JSON file on a temp directory per test.
/// </remarks>
public sealed class FileBackedDeliveryPersistenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("iris-delivery-persist-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private string JournalPath(string name) => Path.Combine(_dir, name);

    // --- Queue: pending jobs survive a restart (replay on construction) -------------------

    [Fact]
    public async Task Queue_PendingJobs_SurviveRestart_AndAreReplayed()
    {
        var path = JournalPath("queue-replay.ndjson");

        // Process 1: enqueue 3 jobs, then stop (no dequeue — they are still pending).
        await using (var q1 = new FileBackedDeliveryQueue(path))
        {
            for (var i = 0; i < 3; i++)
            {
                await q1.EnqueueAsync(new DeliveryJob(new Iri(InboxIri), BuildActivity($"note-{i}")));
            }

            Assert.Equal(3, q1.Count);
        } // process 1 stops here

        // Process 2: a fresh queue over the same path replays the 3 pending jobs.
        await using var q2 = new FileBackedDeliveryQueue(path);
        Assert.Equal(3, q2.Count);

        // Dequeue exactly the 3 replayed jobs (the channel is not completed, so we drain a known count).
        for (var i = 0; i < 3; i++)
        {
            var job = await q2.TryDequeueAsync(CancellationToken.None);
            Assert.NotNull(job);
            Assert.NotNull(job!.Activity);
            Assert.Equal(InboxIri, job.InboxIri.Value);
        }

        Assert.Equal(0, q2.Count);
    }

    // --- Queue: a dequeued (delivered) job is NOT re-delivered on restart ------------------

    [Fact]
    public async Task Queue_DequeuedJob_IsNotReplayed_WhenTruncated()
    {
        var path = JournalPath("queue-truncate.ndjson");

        // Process 1: enqueue 3, dequeue (deliver) 2, truncate the journal to the 1 still pending, stop.
        await using (var q1 = new FileBackedDeliveryQueue(path))
        {
            for (var i = 0; i < 3; i++)
            {
                await q1.EnqueueAsync(new DeliveryJob(new Iri(InboxIri), BuildActivity($"note-{i}")));
            }

            await q1.TryDequeueAsync(CancellationToken.None); // delivered note-0
            await q1.TryDequeueAsync(CancellationToken.None); // delivered note-1
            Assert.Equal(1, q1.Count); // note-2 still pending

            await q1.TruncateAsync(CancellationToken.None); // journal now holds only note-2
        }

        // Process 2: only the 1 pending job is replayed (the 2 delivered are not re-delivered).
        await using var q2 = new FileBackedDeliveryQueue(path);
        Assert.Equal(1, q2.Count);

        var job = await q2.TryDequeueAsync(CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(InboxIri, job!.InboxIri.Value);
        // Only 1 job was pending; the channel is now empty (Count == 0). We do not call
        // TryDequeueAsync again because the channel is not completed (it would block).
        Assert.Equal(0, q2.Count);
    }

    // --- Queue: the journal file is written to disk --------------------------------------

    [Fact]
    public async Task Queue_Enqueue_WritesJournalFile_ToDisk()
    {
        var path = JournalPath("queue-file.ndjson");
        await using var q = new FileBackedDeliveryQueue(path);
        await q.EnqueueAsync(new DeliveryJob(new Iri(InboxIri), BuildActivity("note-0")));

        Assert.True(File.Exists(path));
        var lines = File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Single(lines);
    }

    // --- Dead-letter: entries survive a restart (restore on construction) ----------------

    [Fact]
    public async Task DeadLetter_Entries_SurviveRestart_AndAreRestored()
    {
        var path = JournalPath("deadletter-restore.ndjson");

        // Process 1: dead-letter 2 entries, then stop.
        {
            var store1 = new FileBackedDeliveryDeadLetterStore(path);
            await store1.AddAsync(MakeEntry("inbox-0", attempts: 5));
            await store1.AddAsync(MakeEntry("inbox-1", attempts: 3));
            Assert.Equal(2, store1.Count);
        } // process 1 stops here

        // Process 2: a fresh store over the same path restores the 2 entries (newest first).
        var store2 = new FileBackedDeliveryDeadLetterStore(path);
        Assert.Equal(2, store2.Count);

        var entries = await store2.ListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal("inbox-1", entries[0].InboxIri.Value); // newest first
        Assert.Equal("inbox-0", entries[1].InboxIri.Value);
        Assert.Equal(5, entries[1].Attempts);
    }

    // --- Dead-letter: the activity round-trips (polymorphic type preserved) ---------------

    [Fact]
    public async Task DeadLetter_Activity_RoundTrips_PreservingType()
    {
        var path = JournalPath("deadletter-activity.ndjson");

        // Process 1: dead-letter one entry, then stop.
        {
            var store1 = new FileBackedDeliveryDeadLetterStore(path);
            await store1.AddAsync(MakeEntry("inbox-0", attempts: 5));
        }

        // Process 2: restore and verify the activity's polymorphic type is preserved.
        var store2 = new FileBackedDeliveryDeadLetterStore(path);
        var entry = (await store2.ListAsync()).Single();
        // The activity deserialized back as a Create (the polymorphic type is preserved via the
        // ActivityStreams JSON).
        Assert.IsType<Create>(entry.Activity);
        Assert.NotNull(entry.Activity.Id);
    }

    // --- Dead-letter: capacity eviction is applied to the restored set --------------------

    [Fact]
    public async Task DeadLetter_Restore_AppliesCapacity_EvictsOldest()
    {
        var path = JournalPath("deadletter-capacity.ndjson");

        // Process 1: dead-letter 3 entries (no in-memory bound hit at capacity 1000, but the file has 3).
        {
            var store1 = new FileBackedDeliveryDeadLetterStore(path);
            await store1.AddAsync(MakeEntry("inbox-0", attempts: 1));
            await store1.AddAsync(MakeEntry("inbox-1", attempts: 1));
            await store1.AddAsync(MakeEntry("inbox-2", attempts: 1));
        }

        // Process 2: restore with capacity 2 → the oldest (inbox-0) is evicted, inbox-1 + inbox-2 kept.
        var store2 = new FileBackedDeliveryDeadLetterStore(path, capacity: 2);
        Assert.Equal(2, store2.Count);
        var entries = await store2.ListAsync();
        Assert.Equal("inbox-2", entries[0].InboxIri.Value); // newest first
        Assert.Equal("inbox-1", entries[1].InboxIri.Value);
    }

    // --- Queue: CompleteAsync + empty → TryDequeueAsync returns null (drain) --------------

    [Fact]
    public async Task Queue_CompletedAndEmpty_TryDequeueReturnsNull()
    {
        var path = JournalPath("queue-drain.ndjson");
        await using var q = new FileBackedDeliveryQueue(path);
        await q.CompleteAsync(CancellationToken.None);
        Assert.Null(await q.TryDequeueAsync(CancellationToken.None));
    }

    // --- DI: UseFileBackedDelivery swaps the in-memory defaults for the file-backed types --

    [Fact]
    public void UseFileBackedDelivery_RegistersFileBackedQueueAndDeadLetterStore()
    {
        var services = new ServiceCollection();
        var queuePath = JournalPath("di-queue.ndjson");
        var deadLetterPath = JournalPath("di-deadletter.ndjson");

        services.AddActivityPubServer();
        services.UseFileBackedDelivery(queuePath, deadLetterPath);

        var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IDeliveryQueue>();
        var deadLetter = provider.GetRequiredService<IDeliveryDeadLetterStore>();

        Assert.IsType<FileBackedDeliveryQueue>(queue);
        Assert.IsType<FileBackedDeliveryDeadLetterStore>(deadLetter);
        Assert.Equal(queuePath, ((FileBackedDeliveryQueue)queue).JournalPath);
        Assert.Equal(deadLetterPath, ((FileBackedDeliveryDeadLetterStore)deadLetter).JournalPath);
    }

    // --- DI: default registration is still in-memory (opt-in only) -----------------------

    [Fact]
    public void AddActivityPubServer_DefaultsToInMemoryDeliveryQueue()
    {
        var services = new ServiceCollection();
        services.AddActivityPubServer();

        var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IDeliveryQueue>();
        var deadLetter = provider.GetRequiredService<IDeliveryDeadLetterStore>();

        Assert.IsType<InMemoryDeliveryQueue>(queue);
        Assert.IsType<InMemoryDeliveryDeadLetterStore>(deadLetter);
    }

    // --- Helpers ------------------------------------------------------------------------

    private const string InboxIri = "https://b.domain.local/ap/v1/u/bob/inbox";
    private const string ActorIri = "https://a.domain.local/ap/v1/u/alice";

    private static Activity BuildActivity(string noteId) => new Create
    {
        Id = $"{ActorIri}/creates/{noteId}",
        Actor = [new Link { Href = new Uri(ActorIri) }],
        Object = [new Note { Id = $"{ActorIri}/notes/{noteId}", Content = ["hello"] }],
    };

    private static DeadLetterEntry MakeEntry(string inbox, int attempts) => new(
        new Iri(inbox),
        BuildActivity("dead"),
        new Iri(ActorIri),
        attempts,
        DeadLetterFailureKind.NonSuccessStatus,
        "500",
        DateTimeOffset.UtcNow);
}
