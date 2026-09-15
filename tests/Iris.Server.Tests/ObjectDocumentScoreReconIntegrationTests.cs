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
/// 138.18 (score reconciliation policy) — the per-object <c>iris:dislikedCount</c> and <c>iris:score</c>
/// extensions rendered on the <em>object-document</em> endpoint. Iris keeps separate like and dislike
/// counters (mirroring Lemmy's <c>upvotes</c> / <c>downvotes</c>) and derives the Lemmy-equivalent net
/// score as <c>likedCount - dislikedCount</c> (the <c>iris:score</c> extension). A client reads the net
/// score off the object document it already fetched — no separate wire field is needed.
/// </summary>
/// <remarks>
/// Single instance (score-recon.domain.local, actor <c>alice</c>). A note (n1) is stored directly with
/// likers / dislikers recorded as the Like / Dislike handlers would (bob + carol like, bob dislikes). The
/// object document is fetched over HTTP and the <c>iris:*</c> counters are read off the JSON.
/// </remarks>
[Collection("ObjectDocumentScoreRecon")]
public sealed class ObjectDocumentScoreReconIntegrationTests : IAsyncLifetime
{
    internal const string Host = "score-recon.domain.local";
    internal const string Handle = "alice";
    internal static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");
    private static readonly Iri BobIri = new($"https://{Host}/ap/v1/u/bob");
    private static readonly Iri CarolIri = new($"https://{Host}/ap/v1/u/carol");
    private static readonly Iri Note1 = new($"{ActorIri.Value}/notes/n1");
    private static readonly Iri Note2 = new($"{ActorIri.Value}/notes/n2");

    private const string Ns = "https://iris.example/ns#";

    private readonly ObjectDocumentScoreReconSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{Host}";

    public ObjectDocumentScoreReconIntegrationTests(ObjectDocumentScoreReconSharedHost fixture)
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

    [Fact]
    public async Task ObjectDocument_RendersDislikedCountAndScore()
    {
        var (liked, disliked, score) = await ReadScoreCountersAsync(Note1);

        // n1 has: bob + carol like (2), bob dislikes (1). Score = 2 - 1 = 1.
        Assert.Equal(2, liked);
        Assert.Equal(1, disliked);
        Assert.Equal(1, score);
    }

    [Fact]
    public async Task ObjectDocument_NoInteractions_RendersZeroDislikedAndZeroScore()
    {
        var (liked, disliked, score) = await ReadScoreCountersAsync(Note2);

        // n2 has no likers / dislikers: both counters are 0, score is 0.
        Assert.Equal(0, liked);
        Assert.Equal(0, disliked);
        Assert.Equal(0, score);
    }

    [Fact]
    public async Task ObjectDocument_MoreDislikesThanLikes_RendersNegativeScore()
    {
        // Add more dislikers to n1: carol also dislikes n1. Now 2 likes, 2 dislikes → score 0.
        await _persistence.Dislikes.RecordDislikeAsync(CarolIri, Note1);

        var (liked, disliked, score) = await ReadScoreCountersAsync(Note1);

        Assert.Equal(2, liked);
        Assert.Equal(2, disliked);
        Assert.Equal(0, score);

        // Add a third disliker: 2 likes, 3 dislikes → score -1.
        var DaveIri = new Iri($"https://{Host}/ap/v1/u/dave");
        await _persistence.Dislikes.RecordDislikeAsync(DaveIri, Note1);

        var (_, _, score2) = await ReadScoreCountersAsync(Note1);
        Assert.Equal(-1, score2);
    }

    // --- Helpers -------------------------------------------------------------------------------------

    private async Task<(int Liked, int Disliked, int Score)> ReadScoreCountersAsync(Iri objectIri)
    {
        var path = new Uri(objectIri.Value).AbsolutePath;
        var response = await _http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        return (
            ReadCounter(root, Ns + IrisExtensionTerms.LikedCount),
            ReadCounter(root, Ns + IrisExtensionTerms.DislikedCount),
            ReadCounter(root, Ns + IrisExtensionTerms.Score));
    }

    private static int ReadCounter(JsonElement root, string key)
    {
        Assert.True(root.TryGetProperty(key, out var value), $"object document is missing the '{key}' counter");
        return value.GetInt32();
    }

    internal static void SeedForFixture(InMemoryPersistenceProvider persistence)
    {
        var (_, aliceIri, _) = TestSeeder.SeedPersonWithKey(persistence, Host, Handle);
        var _ = aliceIri;

        var actor = new Link { Href = new Uri(ActorIri.Value) };

        // n1: the note with likes and dislikes.
        persistence.Objects.PutObjectAsync(new Note
        {
            Id = Note1.Value,
            Content = ["hello, world"],
            AttributedTo = [actor],
        }).GetAwaiter().GetResult();

        // The like edges: bob + carol like n1.
        persistence.Likes.RecordLikeAsync(BobIri, Note1).GetAwaiter().GetResult();
        persistence.Likes.RecordLikeAsync(CarolIri, Note1).GetAwaiter().GetResult();

        // The dislike edge: bob dislikes n1.
        persistence.Dislikes.RecordDislikeAsync(BobIri, Note1).GetAwaiter().GetResult();

        // n2: the note with no interactions.
        persistence.Objects.PutObjectAsync(new Note
        {
            Id = Note2.Value,
            Content = ["no interactions"],
            AttributedTo = [actor],
        }).GetAwaiter().GetResult();
    }

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

public sealed class ObjectDocumentScoreReconSharedHost : SharedHostFixture
{
    public ObjectDocumentScoreReconSharedHost()
        : base(BuildOptions())
    {
    }

    private static ActivityPubHostOptions BuildOptions()
    {
        var persistence = new InMemoryPersistenceProvider();
        ObjectDocumentScoreReconIntegrationTests.SeedForFixture(persistence);

        var keyId = new Iri($"{ObjectDocumentScoreReconIntegrationTests.ActorIri.Value}#key-1");
        persistence.Keys.TryGetKey(keyId, out var key);

        var serverRef = SharedHostFixture.ServerRefFor(persistence);

        return new ActivityPubHostOptions
        {
            Host = ObjectDocumentScoreReconIntegrationTests.Host,
            Handle = ObjectDocumentScoreReconIntegrationTests.Handle,
            Persistence = persistence,
            Fetcher = ObjectDocumentScoreReconIntegrationTests.BuildSelfFetcher(key!, serverRef),
        };
    }
}

[CollectionDefinition("ObjectDocumentScoreRecon")]
public sealed class ObjectDocumentScoreReconCollection : ICollectionFixture<ObjectDocumentScoreReconSharedHost>
{
}
