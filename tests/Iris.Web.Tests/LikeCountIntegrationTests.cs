using System.Text.Json;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Collections;
using Iris.Core;
using Iris.Core.Compose;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using Iris.Web;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for the like/boost count fix. They boot the real app (in-memory persistence) in a
/// <see cref="TestServer"/> and verify that a fresh <c>GetLikesAsync</c>/<c>GetSharesAsync</c> read — the
/// read the UI's <c>EngagementBar</c> and <c>ObjectDetail.LoadEngagementAsync</c> now perform with
/// <c>BypassCache = true</c> — reflects a like/boost made moments earlier. The server derives the count
/// live from the reverse index (no server-side cache on <c>/likes</c>), so the only staleness risk was the
/// client <c>CollectionPageCache</c>; bypassing it on count reads is what makes a just-made like appear
/// immediately instead of the count "resetting to 0".
/// </summary>
public sealed class LikeCountIntegrationTests : IDisposable
{
    private const string Base = "https://likes.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;
    private readonly IActivityPubClient _reader;

    public LikeCountIntegrationTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        WebAppFactory.ConfigureServices(builder, Base);
        var services = builder.Services;

        services.AddSingleton<IActorDocumentFetcher>(sp =>
            new LocalActorDocumentFetcher(sp.GetRequiredService<IPersistenceProvider>()));

        var webHostBuilder = new WebHostBuilder()
            .UseTestServer()
            .ConfigureServices(s =>
            {
                foreach (var descriptor in services)
                {
                    s.Add(descriptor);
                }
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseAntiforgery();
                webApp.UseSignatureValidation();
                webApp.UseAuthentication();
                webApp.UseAuthorization();
                webApp.UseStaticFiles();
                webApp.UseEndpoints(endpoints =>
                {
                    WebAppFactory.MapAuthEndpoints(endpoints);
                    endpoints.MapActivityPubEndpoints();
                });
            });

        _server = new TestServer(webHostBuilder);
        WebAppFactory.InitializePersistence(_server.Services, builder.Configuration, Base);
        _services = _server.Services;

        var diKeyStore = _services.GetRequiredService<IKeyStore>();
        _actorIri = new Iri($"{Base}/ap/v1/u/alice");
        var keyId = new Iri($"{_actorIri.Value}#key-1");
        if (!diKeyStore.TryGetKey(keyId, out var existing) || existing is null)
        {
            throw new InvalidOperationException("Seeded actor key not found.");
        }
        _actorKey = (KeyPair)existing;

        // A shared read-only client: /likes and /shares are per-object collections, so the acting actor
        // is irrelevant to the count read. (The real ActivityPubClient shares its CollectionPageCache per
        // instance, which is exactly the staleness the UI fix avoids by passing BypassCache = true.)
        _reader = BuildSignedClient(_actorIri, _actorKey);
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    // ------------------------------------------------------------------
    // The bug: a just-made like/boost must be reflected in a fresh (cache-
    // bypassing) count read — the read the UI now performs.
    // ------------------------------------------------------------------

    [Fact]
    public async Task LikeCount_FreshRead_ReflectsLikeMadeJustNow()
    {
        var alice = BuildSignedClient(_actorIri, _actorKey);
        var note = await PostNoteAsync(alice, "count me");

        // Pre-state: no likes (bypass, to be safe about a stale empty page).
        Assert.Equal(0, await CountLikesAsync(note, bypassCache: true));

        // A second, distinct actor likes the note.
        var bob = await SeedActorAsync("bob");
        var bobClient = BuildSignedClient(bob.Iri, bob.Key);
        var likeResult = await bobClient.LikeAsync(bob.Iri, note);
        Assert.True(likeResult.IsSuccess, $"Like should succeed, got HTTP {(int)likeResult.StatusCode}: {likeResult.Body}");

        // A fresh (cache-bypassing) read — what EngagementBar / ObjectDetail now do — reflects the like.
        Assert.Equal(1, await CountLikesAsync(note, bypassCache: true));
    }

    [Fact]
    public async Task BoostCount_FreshRead_ReflectsBoostMadeJustNow()
    {
        var alice = BuildSignedClient(_actorIri, _actorKey);
        var note = await PostNoteAsync(alice, "boost me");

        Assert.Equal(0, await CountSharesAsync(note, bypassCache: true));

        var carol = await SeedActorAsync("carol");
        var carolClient = BuildSignedClient(carol.Iri, carol.Key);
        var boostResult = await carolClient.AnnounceAsync(carol.Iri, note);
        Assert.True(boostResult.IsSuccess, $"Boost should succeed, got HTTP {(int)boostResult.StatusCode}: {boostResult.Body}");

        Assert.Equal(1, await CountSharesAsync(note, bypassCache: true));
    }

    [Fact]
    public async Task LikeCount_FreshRead_CountsEachLikerOnce()
    {
        var alice = BuildSignedClient(_actorIri, _actorKey);
        var note = await PostNoteAsync(alice, "two likers");

        var bob = await SeedActorAsync("bob");
        Assert.True((await BuildSignedClient(bob.Iri, bob.Key).LikeAsync(bob.Iri, note)).IsSuccess);

        var carol = await SeedActorAsync("carol");
        Assert.True((await BuildSignedClient(carol.Iri, carol.Key).LikeAsync(carol.Iri, note)).IsSuccess);

        Assert.Equal(2, await CountLikesAsync(note, bypassCache: true));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<int> CountLikesAsync(Iri objectIri, bool bypassCache, int limit = 100)
        => await CountAsync(
            q => _reader.GetLikesAsync(objectIri, q, CancellationToken.None),
            bypassCache, limit);

    private async Task<int> CountSharesAsync(Iri objectIri, bool bypassCache, int limit = 100)
        => await CountAsync(
            q => _reader.GetSharesAsync(objectIri, q, CancellationToken.None),
            bypassCache, limit);

    private static async Task<int> CountAsync(
        Func<CollectionQuery, IAsyncEnumerable<IObjectOrLink>> enumerate,
        bool bypassCache, int limit)
    {
        var query = new CollectionQuery { Limit = limit, BypassCache = bypassCache };
        var count = 0;
        await foreach (var _ in enumerate(query))
        {
            count++;
        }

        return count;
    }

    private async Task<Iri> PostNoteAsync(IActivityPubClient client, string content)
    {
        var note = ComposeNote.Build(_actorIri, content, to: [Public]);
        var result = await client.PostNoteAsync(_actorIri, note);
        Assert.True(result.IsSuccess, $"Post should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);
        foreach (var item in outbox)
        {
            if (item is not KristofferStrube.ActivityStreams.Create create || create.Object is not { } objects)
            {
                continue;
            }

            var embedded = objects.FirstOrDefault() as IObject;
            if (embedded?.Content is null)
            {
                continue;
            }

            if (!string.Join(" ", embedded.Content).Contains(content, StringComparison.Ordinal))
            {
                continue;
            }

            if (embedded.Id is { Length: > 0 } id)
            {
                return new Iri(id);
            }
        }

        throw new InvalidOperationException($"No created note containing '{content}' found.");
    }

    /// <summary>
    /// Seeds a fresh local actor (a <c>Person</c> with its own key pair) into the persistence + DI key
    /// stores and the DI key provider, so a signed client can act as that actor and the server can verify
    /// its signatures.
    /// </summary>
    private async Task<(Iri Iri, KeyPair Key)> SeedActorAsync(string name)
    {
        var persistence = GetPersistence();
        var diKeyStore = _services.GetRequiredService<IKeyStore>();
        var actorIri = new Iri($"{Base}/ap/v1/u/{name}");
        var keyId = new Iri($"{actorIri.Value}#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyId);
        persistence.Keys.PutKey(key);
        diKeyStore.PutKey(key);

        var actor = new KristofferStrube.ActivityStreams.Person
        {
            Id = actorIri.Value,
            PreferredUsername = name,
            Name = [name],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData[ActivityPubExtensionNames.PublicKey] =
            JsonSerializer.SerializeToElement(new
            {
                id = keyId.Value,
                owner = actorIri.Value,
                publicKeyPem = key.ExportPublicKeyPem(),
            });
        await persistence.Actors.PutActorAsync(actor);
        _services.GetRequiredService<IKeyProvider>().RegisterKey(actorIri, keyId);

        return (actorIri, key);
    }

    private IActivityPubClient BuildSignedClient(Iri actorIri, KeyPair key)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => _server.CreateHandler()));
    }

    private IPersistenceProvider GetPersistence()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPersistenceProvider>();
    }

    private static readonly Iri Public = new("https://www.w3.org/ns/activitystreams#Public");

    private sealed class LocalActorDocumentFetcher(IPersistenceProvider persistence)
        : IActorDocumentFetcher
    {
        public async Task<KristofferStrube.ActivityStreams.Actor?> GetActorAsync(
            Iri actorIri, CancellationToken ct = default)
        {
            if (await persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct) && actor is not null)
            {
                return actor;
            }

            if (actorIri.Value.Contains("/c/"))
            {
                if (await persistence.Communities.TryGetCommunityAsync(actorIri, out var community, ct)
                    && community is not null)
                {
                    return community;
                }
            }

            return null;
        }
    }
}
