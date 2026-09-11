using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Caching;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Caching;

/// <summary>
/// Scale-out tests for the <see cref="CacheInvalidationChannel"/> (Phase 84.6, shared-state scale-out —
/// the cache-invalidation half). These prove the two-instance cache divergence is real and that the shared
/// invalidation journal closes it: an actor-document change on instance A (a key rotation re-stamping the
/// document's <c>publicKey</c>) invalidates the in-memory actor cache on instance B — without a restart or
/// a cache TTL expiry.
/// </summary>
/// <remarks>
/// Two <see cref="RemoteActorCache"/> instances (one per "app instance") + one shared
/// <see cref="CacheInvalidationChannel"/> (a file-backed journal) + one shared
/// <see cref="InMemoryPersistenceProvider"/> (the shared actor store + key store) — the topology of two app
/// instances over one origin. Instance A rotates (a <see cref="KeyRotationService"/> with the channel as the
/// publisher re-stamps the shared actor document + publishes an invalidation event). Instance B's cache is
/// untouched by A's rotation (the in-process cache is per-instance) — so before a poll, B still serves the
/// <em>stale</em> document (the divergence). After B's <see cref="CacheInvalidationService"/> polls the
/// channel, B's cache is invalidated and B re-fetches the <em>fresh</em> document (convergence).
/// </remarks>
public sealed class CacheInvalidationChannelScaleOutTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("iris-cache-invalidation-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup; the temp dir is under the OS temp root
        }
    }

    [Fact]
    public async Task RotateOnInstanceA_InstanceBCache_InvalidatedOnPoll()
    {
        // One shared persistence = two instances over one origin.
        var persistence = new InMemoryPersistenceProvider();
        var (_, actorIri, originalKeyId) = TestSeeder.SeedPersonWithKey(persistence, "a.domain.local", "alice");

        // The shared invalidation channel (a file-backed journal two instances publish to and poll from).
        var journalPath = Path.Combine(_tempDir, "invalidation.jsonl");
        var channel = new CacheInvalidationChannel(journalPath);

        // Two per-instance actor caches (the in-memory caches are per-instance).
        var cacheA = new RemoteActorCache();
        var cacheB = new RemoteActorCache();

        // The stale actor document (the original, with the #key-1 publicKey.id). Clone it (a JSON round-trip)
        // so the cache holds an independent copy — the rotation re-stamps the persistence's actor object,
        // and a shared reference would alias the mutation (the cache would see the fresh publicKey.id).
        var staleDocument = CloneActor(await GetActorDocumentAsync(persistence, actorIri, originalKeyId));
        var (value, _, wasHit) = await cacheB.GetAsync(
            actorIri,
            bypassCache: false,
            factory: _ => Task.FromResult<IObject?>(staleDocument));
        Assert.NotNull(value);
        Assert.False(wasHit); // a miss (the factory was invoked)
        Assert.Equal(1, cacheB.Count);

        // Baseline: B's cache serves the stale document (the original publicKey.id).
        var (staleValue, _, staleHit) = await cacheB.GetAsync(
            actorIri,
            bypassCache: false,
            factory: _ => Task.FromResult<IObject?>(staleDocument));
        Assert.True(staleHit); // a hit (the cached stale document)
        Assert.Equal(originalKeyId.Value, ExtractPublicKeyId(staleValue as Actor));

        // Instance A rotates: mints #key-2, re-stamps the shared actor document, and publishes an
        // invalidation event (the channel is A's ICacheInvalidationPublisher).
        var rotation = new KeyRotationService(
            persistence,
            persistence.Keys,
            new InMemoryKeyProvider(persistence.Keys),
            NullLogger<KeyRotationService>.Instance,
            cacheInvalidation: channel);
        var newKeyId = await rotation.RotateAsync(actorIri);
        Assert.NotEqual(originalKeyId, newKeyId);

        // Divergence: B's cache still serves the stale document (its cache was not invalidated by A's
        // rotation — the in-process cache is per-instance, and B has not polled the channel yet).
        var (stillStale, _, stillHit) = await cacheB.GetAsync(
            actorIri,
            bypassCache: false,
            factory: _ => Task.FromResult<IObject?>(staleDocument));
        Assert.True(stillHit);
        Assert.Equal(originalKeyId.Value, ExtractPublicKeyId(stillStale as Actor));

        // Convergence: B's CacheInvalidationService polls the channel (applying the invalidation event) and
        // B's cache is invalidated. B re-fetches the fresh document (the rotated publicKey.id).
        var service = new CacheInvalidationService(
            channel,
            cacheB,
            new LocalActorDocumentCache(),
            Options.Create(new ActivityPubServerOptions()),
            NullLogger<CacheInvalidationService>.Instance);
        await service.PollOnceAsync(CancellationToken.None);

        var freshDocument = await GetActorDocumentAsync(persistence, actorIri, newKeyId);
        var (freshValue, _, freshHit) = await cacheB.GetAsync(
            actorIri,
            bypassCache: false,
            factory: _ => Task.FromResult<IObject?>(freshDocument));
        Assert.False(freshHit); // a miss (the cache was invalidated; the factory was invoked)
        Assert.NotNull(freshValue);
        Assert.Equal(newKeyId.Value, ExtractPublicKeyId(freshValue as Actor));

        // B's cache now holds the fresh document (subsequent reads are hits on the fresh document).
        var (cachedFresh, _, cachedHit) = await cacheB.GetAsync(
            actorIri,
            bypassCache: false,
            factory: _ => Task.FromResult<IObject?>(freshDocument));
        Assert.True(cachedHit);
        Assert.Equal(newKeyId.Value, ExtractPublicKeyId(cachedFresh as Actor));
    }

    [Fact]
    public async Task PublishAndPoll_CrossInstance_VisibleToOtherInstance()
    {
        // A publish on one instance is visible to the other instance's poll (the shared journal).
        var journalPath = Path.Combine(_tempDir, "cross.jsonl");
        var channel = new CacheInvalidationChannel(journalPath);

        var actorA = new Iri("https://a.domain.local/users/alice");
        var actorB = new Iri("https://b.domain.local/users/bob");

        await channel.PublishActorInvalidationAsync(actorA);
        await channel.PublishActorInvalidationAsync(actorB);

        // A fresh reader (cursor 0) sees both events, in order.
        var events = await channel.PollAsync(0);
        Assert.Equal(2, events.Count);
        Assert.Equal(actorA.Value, events[0].ActorIri);
        Assert.Equal(actorB.Value, events[1].ActorIri);
        Assert.Equal(1, events[0].Seq);
        Assert.Equal(2, events[1].Seq);

        // A reader that has already seen Seq 1 sees only the new event (Seq 2).
        var newEvents = await channel.PollAsync(1);
        Assert.Single(newEvents);
        Assert.Equal(actorB.Value, newEvents[0].ActorIri);
        Assert.Equal(2, newEvents[0].Seq);
    }

    [Fact]
    public async Task Publish_MonotonicSequence_IncrementsPerEvent()
    {
        // Each publish appends an event with the next Seq (monotonic).
        var journalPath = Path.Combine(_tempDir, "seq.jsonl");
        var channel = new CacheInvalidationChannel(journalPath);

        var actor = new Iri("https://a.domain.local/users/alice");
        for (var i = 0; i < 5; i++)
        {
            await channel.PublishActorInvalidationAsync(actor);
        }

        var events = await channel.PollAsync(0);
        Assert.Equal(5, events.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(i + 1, events[i].Seq);
        }
    }

    [Fact]
    public async Task Purge_RemovesOldEvents_RetainsRecent()
    {
        // Events older than the retention window are purged; recent events are retained.
        var journalPath = Path.Combine(_tempDir, "purge.jsonl");
        var channel = new CacheInvalidationChannel(journalPath, retention: TimeSpan.FromMinutes(1));

        var actor = new Iri("https://a.domain.local/users/alice");

        // Write an old event directly (an old timestamp, simulating an event published 2 minutes ago —
        // older than the 1-min retention).
        var oldTimestamp = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        var oldEventJson = System.Text.Json.JsonSerializer.Serialize(new CacheInvalidationEvent
        {
            Seq = 1,
            ActorIri = actor.Value,
            At = oldTimestamp,
        });
        File.WriteAllText(journalPath, oldEventJson + "\n");

        // Publish a fresh event (Seq 2, a current timestamp).
        await channel.PublishActorInvalidationAsync(actor);

        // Purge events older than the retention window (the old event is removed, the fresh event is kept).
        var removed = await channel.PurgeAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(1, removed);

        var remaining = await channel.PollAsync(0);
        Assert.Single(remaining);
        Assert.True(remaining[0].At > DateTime.UtcNow - TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Poll_TornJournalLine_Skipped()
    {
        // A torn or corrupt journal line (a crash mid-write) is skipped; the next line is still valid.
        var journalPath = Path.Combine(_tempDir, "torn.jsonl");
        var channel = new CacheInvalidationChannel(journalPath);

        var actor = new Iri("https://a.domain.local/users/alice");
        await channel.PublishActorInvalidationAsync(actor);

        // Append a torn line (a partial JSON object).
        await using var writer = new StreamWriter(journalPath, append: true);
        await writer.WriteLineAsync("{\"Seq\":2,\"ActorIri\":\"https://a.domain");

        // A fresh reader sees only the valid event (the torn line is skipped).
        var events = await channel.PollAsync(0);
        Assert.Single(events);
        Assert.Equal(actor.Value, events[0].ActorIri);
    }

    [Fact]
    public async Task CacheInvalidationService_InvalidatesRemoteActorCache()
    {
        // The CacheInvalidationService applies an invalidation event to the local RemoteActorCache.
        var journalPath = Path.Combine(_tempDir, "service.jsonl");
        var channel = new CacheInvalidationChannel(journalPath);
        var cache = new RemoteActorCache();
        var localDocs = new LocalActorDocumentCache();

        var actor = new Iri("https://a.domain.local/users/alice");

        // Populate the cache (a stale entry).
        var document = new Person { Id = actor.Value };
        await cache.GetAsync(actor, bypassCache: false, factory: _ => Task.FromResult<IObject?>(document));
        Assert.Equal(1, cache.Count);

        // Publish an invalidation event.
        await channel.PublishActorInvalidationAsync(actor);

        // The service polls and applies the invalidation.
        var service = new CacheInvalidationService(
            channel,
            cache,
            localDocs,
            Options.Create(new ActivityPubServerOptions()),
            NullLogger<CacheInvalidationService>.Instance);
        await service.PollOnceAsync(CancellationToken.None);

        // The cache entry is invalidated (a subsequent read is a miss).
        Assert.Equal(0, cache.Count);
        var (_, _, wasHit) = await cache.GetAsync(
            actor,
            bypassCache: false,
            factory: _ => Task.FromResult<IObject?>(null));
        Assert.False(wasHit);
    }

    private static async Task<Actor> GetActorDocumentAsync(
        InMemoryPersistenceProvider persistence,
        Iri actorIri,
        Iri expectedKeyId)
    {
        var found = await persistence.Actors.TryGetActorAsync(actorIri, out var actor);
        Assert.True(found);
        Assert.NotNull(actor);

        var keyId = actor!.GetPublicKeyIri();
        Assert.Equal(expectedKeyId, keyId);

        return (Actor)actor;
    }

    /// <summary>
    /// Clones an actor document (a JSON round-trip) so the caller holds an independent copy — the rotation
    /// re-stamps the persistence's actor object in place, and a shared reference would alias the mutation.
    /// </summary>
    private static Actor CloneActor(Actor actor)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(actor);
        return System.Text.Json.JsonSerializer.Deserialize<Actor>(json)
            ?? throw new InvalidOperationException("Failed to clone the actor document.");
    }

    private static string? ExtractPublicKeyId(Actor? actor)
    {
        if (actor?.ExtensionData is not { } extensions)
            return null;

        if (!extensions.TryGetValue("publicKey", out var element))
            return null;

        return element.ValueKind == System.Text.Json.JsonValueKind.Object
            ? element.GetProperty("id").GetString()
            : null;
    }
}
