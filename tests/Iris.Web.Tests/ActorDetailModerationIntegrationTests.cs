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
/// Integration tests for slice 43.3 — <em>moderation on actor detail (Block/Mute/Report + community
/// join-request queue)</em>. They boot the real app (in-memory persistence) in-process via
/// <see cref="WebAppFactory"/> in a <see cref="TestServer"/> and exercise the same client calls the
/// <c>ActorDetail</c> page makes: <c>BlockAsync</c>/<c>MuteAsync</c>/<c>FlagAsync</c> (and their inverses)
/// against a person actor, plus the community join-request queue (list/accept/reject) read via the
/// <c>LocalModeration</c> client that the page's Requests tab uses.
/// </summary>
public sealed class ActorDetailModerationIntegrationTests : IDisposable
{
    private const string Base = "https://freq.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;
    private readonly Iri _communityIri;
    private readonly KeyPair _communityKey;

    public ActorDetailModerationIntegrationTests()
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

        var persistence = GetPersistence();
        var diKeyStore = _services.GetRequiredService<IKeyStore>();

        // Person actor (alice) — the moderator.
        _actorIri = new Iri($"{Base}/ap/v1/u/alice");
        var aliceKeyId = new Iri($"{_actorIri.Value}#key-1");
        if (!diKeyStore.TryGetKey(aliceKeyId, out var aliceExisting) || aliceExisting is null)
        {
            throw new InvalidOperationException("Seeded alice key not found.");
        }
        _actorKey = (KeyPair)aliceExisting;

        // Community (a Group actor) — the join-request target.
        _communityIri = new Iri($"{Base}/ap/v1/c/testcomm");
        var communityKeyId = new Iri($"{_communityIri.Value}#key-1");
        _communityKey = KeyPairGenerator.GenerateRsa(communityKeyId);
        persistence.Keys.PutKey(_communityKey);
        diKeyStore.PutKey(_communityKey);
        _services.GetRequiredService<IKeyProvider>().RegisterKey(_communityIri, communityKeyId);

        var community = new KristofferStrube.ActivityStreams.Group
        {
            Id = _communityIri.Value,
            PreferredUsername = "testcomm",
            Name = ["Test Community"],
            AttributedTo = [new KristofferStrube.ActivityStreams.Link { Href = _actorIri.Uri }],
        };
        community.ExtensionData ??= new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>();
        community.ExtensionData[ActivityPubExtensionNames.PublicKey] =
            System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id = communityKeyId.Value,
                owner = _communityIri.Value,
                publicKeyPem = _communityKey.ExportPublicKeyPem(),
            });
        persistence.Communities.PutCommunityAsync(community).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task BlockOnActorDetail_RecordsEdge()
    {
        var target = new Iri($"{Base}/ap/v1/u/bob");
        var client = BuildSignedClient(_actorIri, _actorKey);

        var result = await client.BlockAsync(_actorIri, target);
        Assert.True(result.IsSuccess, $"Block should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        Assert.True(await GetModerationStore().IsBlockedAsync(_actorIri, target));
    }

    [Fact]
    public async Task MuteOnActorDetail_RecordsEdge()
    {
        var target = new Iri($"{Base}/ap/v1/u/bob");
        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");

        var result = await modClient.MuteAsync(_actorIri, target);
        Assert.True(result.IsSuccess, $"Mute should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        Assert.True(await GetModerationStore().IsMutedAsync(_actorIri, target));
    }

    [Fact]
    public async Task ReportOnActorDetail_RecordsEdge()
    {
        var target = new Iri($"{Base}/ap/v1/u/bob");
        var client = BuildSignedClient(_actorIri, _actorKey);

        var result = await client.FlagAsync(_actorIri, target);
        Assert.True(result.IsSuccess, $"Flag should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        Assert.True(await GetModerationStore().HasFlaggedAsync(_actorIri, target));
    }

    [Fact]
    public async Task UnblockOnActorDetail_RemovesEdge()
    {
        var target = new Iri($"{Base}/ap/v1/u/bob");
        var client = BuildSignedClient(_actorIri, _actorKey);

        var blockResult = await client.BlockAsync(_actorIri, target);
        Assert.True(blockResult.IsSuccess);
        Assert.True(await GetModerationStore().IsBlockedAsync(_actorIri, target));

        var unblockResult = await client.UnblockAsync(_actorIri, new Iri(blockResult.MintedId!));
        Assert.True(unblockResult.IsSuccess, $"Unblock should succeed, got HTTP {(int)unblockResult.StatusCode}");

        Assert.False(await GetModerationStore().IsBlockedAsync(_actorIri, target));
    }

    [Fact]
    public async Task UnmuteOnActorDetail_RemovesEdge()
    {
        var target = new Iri($"{Base}/ap/v1/u/bob");
        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");

        var muteResult = await modClient.MuteAsync(_actorIri, target);
        Assert.True(muteResult.IsSuccess);
        Assert.True(await GetModerationStore().IsMutedAsync(_actorIri, target));

        var unmuteResult = await modClient.UnmuteAsync(_actorIri, target);
        Assert.True(unmuteResult.IsSuccess, $"Unmute should succeed, got HTTP {(int)unmuteResult.StatusCode}");

        Assert.False(await GetModerationStore().IsMutedAsync(_actorIri, target));
    }

    [Fact]
    public async Task CommunityJoinRequests_Listed_WhenApprovesMembers()
    {
        var joiner = new Iri($"{Base}/ap/v1/u/joiner1");
        var client = BuildSignedClient(_actorIri, _actorKey);
        var flagResult = await client.SetManuallyApprovesMembersAsync(_communityIri, true);
        Assert.True(flagResult.IsSuccess, $"enable flag should succeed, got HTTP {(int)flagResult.StatusCode}");

        await GetPersistence().Communities.AddJoinRequestAsync(_communityIri, joiner);

        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.GetCommunityJoinRequestsAsync(_communityIri);
        Assert.True(result.IsSuccess, $"list should succeed, got HTTP {(int)result.StatusCode}");
        Assert.Contains(joiner.Value, result.Body);
    }

    [Fact]
    public async Task CommunityJoinRequest_Accept_AddsMemberAndRemovesRequest()
    {
        var joiner = new Iri($"{Base}/ap/v1/u/joiner1");
        await GetPersistence().Communities.AddJoinRequestAsync(_communityIri, joiner);

        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.AcceptCommunityJoinRequestAsync(_communityIri, joiner);
        Assert.True(result.IsSuccess, $"accept should succeed, got HTTP {(int)result.StatusCode}");

        var persistence = GetPersistence();
        Assert.True(await persistence.Communities.IsMemberAsync(_communityIri, joiner));
        Assert.False(await persistence.Communities.HasJoinRequestAsync(_communityIri, joiner));
    }

    [Fact]
    public async Task CommunityJoinRequest_Reject_RemovesRequestWithoutMember()
    {
        var joiner = new Iri($"{Base}/ap/v1/u/joiner1");
        await GetPersistence().Communities.AddJoinRequestAsync(_communityIri, joiner);

        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.RejectCommunityJoinRequestAsync(_communityIri, joiner);
        Assert.True(result.IsSuccess, $"reject should succeed, got HTTP {(int)result.StatusCode}");

        var persistence = GetPersistence();
        Assert.False(await persistence.Communities.IsMemberAsync(_communityIri, joiner));
        Assert.False(await persistence.Communities.HasJoinRequestAsync(_communityIri, joiner));
    }

    // --- Helpers ------------------------------------------------------------------------

    private IPersistenceProvider GetPersistence()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPersistenceProvider>();
    }

    private IModerationStore GetModerationStore()
        => GetPersistence().Moderation;

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
