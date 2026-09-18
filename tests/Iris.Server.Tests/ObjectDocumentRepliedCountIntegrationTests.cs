using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// 132.1 (interaction tracking) — the per-object interaction counters rendered on the <em>object-document</em>
/// endpoint. The object-document endpoint (<c>GET /ap/v1/{**path}</c>, the read the object-detail page and
/// the <c>EngagementBar</c> fast path use) renders the per-object counters <c>iris:likedCount</c> /
/// <c>iris:sharedCount</c> / <c>iris:repliedCount</c> from the local like / announce / reply reverse
/// indexes, so a client reads the counts off the document it already fetched instead of re-walking the
/// <c>/likes</c>, <c>/shares</c>, and <c>/replies</c> collections (54.8). Before 132.1 the object-document
/// endpoint rendered only <c>likedCount</c> + <c>sharedCount</c> — the <c>repliedCount</c> was missing on
/// the object document (it was only rendered on collection-page items), so the object-detail page /
/// EngagementBar could not read the reply count off the object and fell back to a (slow) collection walk,
/// showing 0 for the reply count.
/// </summary>
/// <remarks>
/// Single instance (replied-count.domain.local, actor <c>alice</c>). A note (n1) is stored directly with
/// its likers / announcers / replies recorded as the <see cref="Iris.Server.Inbox.LikeActivityHandler"/> /
/// <see cref="Iris.Server.Inbox.AnnounceActivityHandler"/> /
/// <see cref="Iris.Server.Inbox.CreateActivityHandler"/> would (bob + carol like, bob boosts, two replies).
/// A sibling note (n2) has no interactions (all three counters are 0 — an object with no interactions
/// still renders the counters as 0, not absent, because the endpoint computes them on every read). The
/// object document is fetched over HTTP (the same wire path the client uses) and the <c>iris:*</c>
/// counters are read off the JSON (the <c>https://iris.example/ns#</c> namespace, the server default).
/// </remarks>
[Collection("ObjectDocumentRepliedCount")]
public sealed class ObjectDocumentRepliedCountIntegrationTests : IAsyncLifetime
{
    internal const string Host = "replied-count.domain.local";
    internal const string Handle = "alice";
    internal static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");
    private static readonly Iri BobIri = new($"https://{Host}/ap/v1/u/bob");
    private static readonly Iri CarolIri = new($"https://{Host}/ap/v1/u/carol");
    private static readonly Iri Note1 = new($"{ActorIri.Value}/notes/n1");
    private static readonly Iri Note2 = new($"{ActorIri.Value}/notes/n2");
    private static readonly Iri Reply1 = new($"https://{Host}/ap/v1/u/bob/notes/r1");
    private static readonly Iri Reply2 = new($"https://{Host}/ap/v1/u/carol/notes/r2");

    // The server's default iris: extension namespace (ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri
    // == IrisDocumentExtensions.DefaultNamespaceIri). The object document renders the counters under
    // {ns}{term}, e.g. "https://iris.example/ns#repliedCount".
    private const string Ns = "https://iris.example/ns#";

    private readonly ObjectDocumentRepliedCountSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{Host}";

    public ObjectDocumentRepliedCountIntegrationTests(ObjectDocumentRepliedCountSharedHost fixture)
    {
        _fixture = fixture;
        _persistence = (InMemoryPersistenceProvider)fixture.Persistence;
        _http = new HttpClient(fixture.Server.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri(_base) };
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_persistence);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
    }

    // --- The object document renders the per-object counters -----------------------------------------

    [Fact]
    public async Task ObjectDocument_RendersLikedSharedAndRepliedCounts()
    {
        var (liked, shared, replied) = await ReadCountersAsync(Note1);

        // n1 has: bob + carol like (2), bob boosts (1), two replies (2).
        Assert.Equal(2, liked);
        Assert.Equal(1, shared);
        Assert.Equal(2, replied);
    }

    [Fact]
    public async Task ObjectDocument_NoInteractions_RendersZeroCounters()
    {
        // n2 has no likers / announcers / replies: all three counters render as 0 (the endpoint computes
        // them on every read; an object with no interactions is 0, not absent).
        var (liked, shared, replied) = await ReadCountersAsync(Note2);

        Assert.Equal(0, liked);
        Assert.Equal(0, shared);
        Assert.Equal(0, replied);
    }

    // --- Helpers -------------------------------------------------------------------------------------

    /// <summary>
    /// Fetches the object document for <paramref name="objectIri"/> over HTTP and reads the three
    /// <c>iris:*</c> interaction counters off the JSON (the <c>likedCount</c> / <c>sharedCount</c> /
    /// <c>repliedCount</c> extensions). Throws when a counter is absent (a test that expects a counter
    /// to be present).
    /// </summary>
    private async Task<(int Liked, int Shared, int Replied)> ReadCountersAsync(Iri objectIri)
    {
        var path = new Uri(objectIri.Value).AbsolutePath;
        var response = await _http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        return (
            ReadCounter(root, Ns + IrisExtensionTerms.LikedCount),
            ReadCounter(root, Ns + IrisExtensionTerms.SharedCount),
            ReadCounter(root, Ns + IrisExtensionTerms.RepliedCount));
    }

    private static int ReadCounter(JsonElement root, string key)
    {
        Assert.True(root.TryGetProperty(key, out var value), $"object document is missing the '{key}' counter");
        return value.GetInt32();
    }

    /// <summary>
    /// Seeds the fixture persistence: a note (n1) with likers / announcers / replies recorded as the
    /// Like / Announce / Create handlers would, and a sibling note (n2) with no interactions.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider persistence)
    {
        var (_, aliceIri, _) = TestSeeder.SeedPersonWithKey(persistence, Host, Handle);
        var _ = aliceIri;

        var actor = new Link { Href = new Uri(ActorIri.Value) };

        // n1: the note with interactions.
        persistence.Objects.PutObjectAsync(new Note
        {
            Id = Note1.Value,
            Content = ["hello, world"],
            AttributedTo = [actor],
        }).GetAwaiter().GetResult();

        // The like edges (as the LikeActivityHandler would record them): bob + carol like n1.
        persistence.Likes.RecordLikeAsync(BobIri, Note1).GetAwaiter().GetResult();
        persistence.Likes.RecordLikeAsync(CarolIri, Note1).GetAwaiter().GetResult();

        // The announce edge (as the AnnounceActivityHandler would record it): bob boosts n1.
        persistence.Announces.RecordAnnounceAsync(BobIri, Note1).GetAwaiter().GetResult();

        // The reply edges (as the CreateActivityHandler would record them for an inbound reply):
        // bob's r1 and carol's r2 reply to n1.
        persistence.Replies.RecordReplyAsync(Note1, Reply1).GetAwaiter().GetResult();
        persistence.Replies.RecordReplyAsync(Note1, Reply2).GetAwaiter().GetResult();

        // n2: the note with no interactions.
        persistence.Objects.PutObjectAsync(new Note
        {
            Id = Note2.Value,
            Content = ["no interactions"],
            AttributedTo = [actor],
        }).GetAwaiter().GetResult();
    }


    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that reaches the actor's own instance so the
    /// <c>SignatureValidationMiddleware</c> validates a signed inbound activity by resolving the actor's
    /// key from its own actor document (deferred: the TestServer does not exist yet while the server is
    /// being constructed).
    /// </summary>
    internal static IActorDocumentFetcher BuildSelfFetcher(ISigningKey key, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = ActorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}

/// <summary>
/// Shared-host fixture for <see cref="ObjectDocumentRepliedCountIntegrationTests"/> (single instance,
/// replied-count.domain.local, actor alice). Built once per xunit collection; the test class resets +
/// reseeds before each method.
/// </summary>
public sealed class ObjectDocumentRepliedCountSharedHost : SharedHostFixture
{
    public ObjectDocumentRepliedCountSharedHost()
        : base(BuildOptions())
    {
    }

    private static ActivityPubHostOptions BuildOptions()
    {
        var persistence = new InMemoryPersistenceProvider();
        ObjectDocumentRepliedCountIntegrationTests.SeedForFixture(persistence);

        var keyId = new Iri($"{ObjectDocumentRepliedCountIntegrationTests.ActorIri.Value}#key-1");
        persistence.Keys.TryGetKey(keyId, out var key);

        var serverRef = SharedHostFixture.ServerRefFor(persistence);

        return new ActivityPubHostOptions
        {
            Host = ObjectDocumentRepliedCountIntegrationTests.Host,
            Handle = ObjectDocumentRepliedCountIntegrationTests.Handle,
            Persistence = persistence,
            Fetcher = ObjectDocumentRepliedCountIntegrationTests.BuildSelfFetcher(key!, serverRef),
        };
    }
}

/// <summary>
/// xunit collection definition for the object-document replied-count shared-host fixture.
/// </summary>
[CollectionDefinition("ObjectDocumentRepliedCount")]
public sealed class ObjectDocumentRepliedCountCollection : ICollectionFixture<ObjectDocumentRepliedCountSharedHost>
{
}
