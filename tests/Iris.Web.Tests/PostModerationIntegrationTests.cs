using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Pipeline;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using Iris.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for slice 43.1 — <em>per-user moderation actions on posts (Block/Mute/Flag)</em>.
/// They boot the real app (in-memory persistence) in-process via <see cref="WebAppFactory"/> in a
/// <see cref="TestServer"/> and verify that the client's Block/Flag (signed outbox delivery) and Mute
/// (local Basic-auth endpoint) methods round-trip to the moderation store.
/// </summary>
public sealed class PostModerationIntegrationTests : IDisposable
{
    private const string Base = "https://mod.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;

    public PostModerationIntegrationTests()
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
                webApp.UseEndpoints(endpoints =>
                {
                    endpoints.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
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
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task Block_RecordsEdgeInModerationStore()
    {
        var targetIri = new Iri($"{Base}/ap/v1/u/bob");
        var client = BuildSignedClient(_actorIri, _actorKey);

        var result = await client.BlockAsync(_actorIri, targetIri);
        Assert.True(result.IsSuccess, $"Block should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var moderation = GetModerationStore();
        Assert.True(await moderation.IsBlockedAsync(_actorIri, targetIri));
    }

    [Fact]
    public async Task Flag_RecordsEdgeInModerationStore()
    {
        var targetIri = new Iri($"{Base}/ap/v1/u/bob");
        var client = BuildSignedClient(_actorIri, _actorKey);

        var result = await client.FlagAsync(_actorIri, targetIri);
        Assert.True(result.IsSuccess, $"Flag should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var moderation = GetModerationStore();
        Assert.True(await moderation.HasFlaggedAsync(_actorIri, targetIri));
    }

    [Fact]
    public async Task Mute_RecordsEdgeInModerationStore()
    {
        var targetIri = new Iri($"{Base}/ap/v1/u/bob");
        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");

        var result = await modClient.MuteAsync(_actorIri, targetIri);
        Assert.True(result.IsSuccess, $"Mute should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var moderation = GetModerationStore();
        Assert.True(await moderation.IsMutedAsync(_actorIri, targetIri));
    }

    [Fact]
    public async Task Unblock_RemovesEdgeFromModerationStore()
    {
        var targetIri = new Iri($"{Base}/ap/v1/u/bob");
        var client = BuildSignedClient(_actorIri, _actorKey);

        var blockResult = await client.BlockAsync(_actorIri, targetIri);
        Assert.True(blockResult.IsSuccess);
        Assert.True(await GetModerationStore().IsBlockedAsync(_actorIri, targetIri));

        var blockId = blockResult.MintedId;
        Assert.NotNull(blockId);

        var unblockResult = await client.UnblockAsync(_actorIri, new Iri(blockId!));
        Assert.True(unblockResult.IsSuccess, $"Unblock should succeed, got HTTP {(int)unblockResult.StatusCode}");

        Assert.False(await GetModerationStore().IsBlockedAsync(_actorIri, targetIri));
    }

    [Fact]
    public async Task Unmute_RemovesEdgeFromModerationStore()
    {
        var targetIri = new Iri($"{Base}/ap/v1/u/bob");
        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");

        var muteResult = await modClient.MuteAsync(_actorIri, targetIri);
        Assert.True(muteResult.IsSuccess);
        Assert.True(await GetModerationStore().IsMutedAsync(_actorIri, targetIri));

        var unmuteResult = await modClient.UnmuteAsync(_actorIri, targetIri);
        Assert.True(unmuteResult.IsSuccess, $"Unmute should succeed, got HTTP {(int)unmuteResult.StatusCode}");

        Assert.False(await GetModerationStore().IsMutedAsync(_actorIri, targetIri));
    }

    [Fact]
    public async Task Unflag_RemovesEdgeFromModerationStore()
    {
        var targetIri = new Iri($"{Base}/ap/v1/u/bob");
        var client = BuildSignedClient(_actorIri, _actorKey);

        var flagResult = await client.FlagAsync(_actorIri, targetIri);
        Assert.True(flagResult.IsSuccess);
        Assert.True(await GetModerationStore().HasFlaggedAsync(_actorIri, targetIri));

        var flagId = flagResult.MintedId;
        Assert.NotNull(flagId);

        var unflagResult = await client.UnflagAsync(_actorIri, new Iri(flagId!));
        Assert.True(unflagResult.IsSuccess, $"Unflag should succeed, got HTTP {(int)unflagResult.StatusCode}");

        Assert.False(await GetModerationStore().HasFlaggedAsync(_actorIri, targetIri));
    }

    // --- Helpers ------------------------------------------------------------------------

    private IModerationStore GetModerationStore()
    {
        using var scope = _services.CreateScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IPersistenceProvider>();
        return persistence.Moderation;
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

    private ILocalModerationClient BuildLocalModerationClient(Iri actorIri, string user, string pass)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_actorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, _actorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.CreateLocalModerationClient(
            new ActivityPubClientOptions
            {
                ActorId = actorIri,
                EnableRetry = false,
                LocalCredentials = new ProxyCredentials(user, pass),
            },
            new LazyHandler(() => _server.CreateHandler()));
    }

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
