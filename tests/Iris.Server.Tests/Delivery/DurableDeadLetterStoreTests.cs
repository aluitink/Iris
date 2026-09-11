using Iris.Core;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Phase 84.1 — <strong>Config-driven durable dead-letter store</strong>: the dead-letter queue (83.3's
/// <c>GET /ap/v1/dead-letters</c>) is in-memory only by default — a restart loses every dead-lettered
/// delivery. The <see cref="FileBackedDeliveryDeadLetterStore"/> (Phase 16.2) already journals to disk;
/// this slice wires it in <em>config-driven</em> (independent of the delivery queue): a
/// <c>Iris:Delivery:DeadLetterJournalPath</c> key selects the file-backed store. These tests lock the
/// durability contract (a dead-lettered delivery persists + a fresh store over the same path reads it
/// back) + the config selection (a set key → file-backed store, unset → in-memory, and the
/// <see cref="IDeliveryQueue"/> binding is left untouched).
/// </summary>
public sealed class DurableDeadLetterStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("iris-dead-letter-durable-").FullName;

    public void Dispose()
    {
        // Best-effort cleanup; a failed test must not fail on a leftover temp dir.
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private string NewJournalPath() => Path.Combine(_dir, "dead-letters.jsonl");

    private static DeadLetterEntry MakeEntry(int inboxIndex, int attempts) => new(
        new Iri($"https://peer{inboxIndex}.local/inbox"),
        MakeActivity($"act-{inboxIndex}"),
        null,
        attempts,
        DeadLetterFailureKind.NonSuccessStatus,
        $"4{attempts}0",
        DateTimeOffset.UtcNow.AddMinutes(inboxIndex));

    // ------------------------------------------------- durability across a restart

    [Fact]
    public async Task DeadLetteredDelivery_PersistsAndSurvivesRestart()
    {
        var path = NewJournalPath();

        // "Process 1": add a few dead letters (journaled to disk).
        var first = new FileBackedDeliveryDeadLetterStore(path);
        for (var i = 1; i <= 3; i++)
        {
            await first.AddAsync(MakeEntry(i, i));
        }

        Assert.Equal(3, first.Count);
        var fileExists = File.Exists(path);

        // "Process 2": a fresh store over the SAME path restores the journaled entries (a restart).
        var second = new FileBackedDeliveryDeadLetterStore(path);

        Assert.True(fileExists, "the journal file should have been created on the first write");
        Assert.Equal(3, second.Count); // the same 3 entries survive the restart

        // And they are the same entries (newest-first, so the highest inbox index first).
        var restored = await second.ListAsync();
        Assert.Equal(3, restored.Count);
        Assert.Equal("https://peer3.local/inbox", restored[0].InboxIri.Value);
        Assert.Equal("https://peer1.local/inbox", restored[2].InboxIri.Value);

        // The round-tripped activity is a real Activity (not lost in serialization).
        Assert.NotNull(restored[0].Activity);
        Assert.Equal("act-3", restored[0].Activity.Id);
    }

    // ------------------------------------------------- capacity eviction + newest-first

    [Fact]
    public async Task CapacityEviction_EvictsOldest_NewestFirst_Kept()
    {
        var path = NewJournalPath();

        // A small in-memory bound (5). Add 8 entries: the file holds all 8 (it is the full log), but the
        // in-memory view keeps only the newest 5 (the oldest 3 are evicted).
        var store = new FileBackedDeliveryDeadLetterStore(path, capacity: 5);
        for (var i = 1; i <= 8; i++)
        {
            await store.AddAsync(MakeEntry(i, 1));
        }

        // In-memory bound: only 5 held (the newest: peer4..peer8).
        Assert.Equal(5, store.Count);
        var listed = await store.ListAsync();
        Assert.Equal(5, listed.Count);
        Assert.Equal("https://peer8.local/inbox", listed[0].InboxIri.Value); // newest first
        Assert.Equal("https://peer4.local/inbox", listed[4].InboxIri.Value); // the oldest still held

        // After a restart, the same bound applies to the restored set (the file has 8 lines; the in-memory
        // view drops the oldest 3, keeping the newest 5).
        var restarted = new FileBackedDeliveryDeadLetterStore(path, capacity: 5);
        Assert.Equal(5, restarted.Count);
        var restartedList = await restarted.ListAsync();
        Assert.Equal("https://peer8.local/inbox", restartedList[0].InboxIri.Value);
        Assert.Equal("https://peer4.local/inbox", restartedList[4].InboxIri.Value);
    }

    // ------------------------------------------------- config-driven selection

    [Fact]
    public void DeadLetterJournalPathConfig_SelectsFileBackedStore()
    {
        var path = NewJournalPath();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:Delivery:DeadLetterJournalPath"] = path,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddActivityPubServer(config);

        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDeliveryDeadLetterStore>();

        // The configured path selects the file-backed store (not the in-memory default).
        Assert.IsType<FileBackedDeliveryDeadLetterStore>(store);
        Assert.Equal(path, ((FileBackedDeliveryDeadLetterStore)store).JournalPath);
    }

    [Fact]
    public void NoDeadLetterJournalPath_KeepsInMemoryStore()
    {
        // Without the key, the default in-memory store is used (current behavior — existing tests
        // unaffected).
        var config = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddActivityPubServer(config);

        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDeliveryDeadLetterStore>();

        Assert.IsType<InMemoryDeliveryDeadLetterStore>(store);
    }

    [Fact]
    public void DeadLetterJournalPathConfig_DoesNotChangeDeliveryQueueBinding()
    {
        // The config key selects ONLY the dead-letter store — the delivery queue stays its in-memory
        // default (a host that wants a file-backed queue still calls UseFileBackedDelivery explicitly).
        var path = NewJournalPath();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:Delivery:DeadLetterJournalPath"] = path,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddActivityPubServer(config);

        var provider = services.BuildServiceProvider();

        Assert.IsType<FileBackedDeliveryDeadLetterStore>(provider.GetRequiredService<IDeliveryDeadLetterStore>());
        // The queue is the in-memory default (not the file-backed queue).
        Assert.IsType<InMemoryDeliveryQueue>(provider.GetRequiredService<IDeliveryQueue>());
    }

    private static Activity MakeActivity(string id) => new Create
    {
        Id = id,
        Object = [new Note { Id = $"{id}/note", Content = ["durable dead-letter test"] }],
    };
}
