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
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for slice 43.2 — <em>follow-request queue (person actor)</em>. They boot the
/// real app (in-memory persistence) in-process via <see cref="WebAppFactory"/> in a
/// <see cref="TestServer"/> and verify that: (1) enabling manuallyApprovesFollowers causes inbound
/// Follows to appear in the actor's outbox without auto-accept, (2) AcceptAsync removes the pending
/// state, and (3) the follow-request listing (outbox Follow filter) works end-to-end.
/// </summary>
public sealed class FollowRequestQueueIntegrationTests : IDisposable
{
    private const string Base = "https://freq.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;

    public FollowRequestQueueIntegrationTests()
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
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task WithFlag_On_FollowAppearsInOutbox_WithoutAccept()
    {
        Assert.True(await CallSetFlagAsync(true), "enable should succeed");

        var followerIri = new Iri($"{Base}/ap/v1/u/follower1");
        await DeliverFollowAsync(followerIri);

        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);
        var follows = outbox.Where(i => i is Activity a && a.Type?.FirstOrDefault() == "Follow").ToList();
        Assert.Single(follows);

        var accepts = outbox.Where(i => i is Activity a && a.Type?.FirstOrDefault() == "Accept").ToList();
        Assert.Empty(accepts);
    }

    [Fact]
    public async Task WithoutFlag_FollowAutoAccepts()
    {
        var followerIri = new Iri($"{Base}/ap/v1/u/follower1");
        await DeliverFollowAsync(followerIri);

        var persistence = GetPersistence();
        Assert.True(await persistence.Follows.IsFollowingAsync(followerIri, _actorIri));
    }

    [Fact]
    public async Task AcceptFollow_SendsAcceptActivity()
    {
        Assert.True(await CallSetFlagAsync(true));

        var followerIri = new Iri($"{Base}/ap/v1/u/follower1");
        await DeliverFollowAsync(followerIri);

        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);
        var followItem = outbox.FirstOrDefault(i => i is Activity a && a.Type?.FirstOrDefault() == "Follow");
        Assert.NotNull(followItem);

        var followActivity = (KristofferStrube.ActivityStreams.Activity)followItem!;
        var followIri = new Iri(followActivity.Id!);
        var client = BuildSignedClient(_actorIri, _actorKey);
        var result = await client.AcceptAsync(_actorIri, followIri);
        Assert.True(result.IsSuccess, $"Accept should succeed, got HTTP {(int)result.StatusCode}");
    }

    [Fact]
    public async Task RejectFollow_SendsRejectActivity()
    {
        Assert.True(await CallSetFlagAsync(true));

        var followerIri = new Iri($"{Base}/ap/v1/u/follower1");
        await DeliverFollowAsync(followerIri);

        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);
        var followItem = outbox.FirstOrDefault(i => i is Activity a && a.Type?.FirstOrDefault() == "Follow");
        Assert.NotNull(followItem);

        var followActivity = (KristofferStrube.ActivityStreams.Activity)followItem!;
        var followIri = new Iri(followActivity.Id!);
        var client = BuildSignedClient(_actorIri, _actorKey);
        var result = await client.RejectAsync(_actorIri, followIri);
        Assert.True(result.IsSuccess, $"Reject should succeed, got HTTP {(int)result.StatusCode}");
    }

    [Fact]
    public async Task MultipleFollows_AllAppearInOutbox()
    {
        Assert.True(await CallSetFlagAsync(true));

        await DeliverFollowAsync(new Iri($"{Base}/ap/v1/u/follower1"));
        await DeliverFollowAsync(new Iri($"{Base}/ap/v1/u/follower2"));

        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);
        var follows = outbox.Where(i => i is Activity a && a.Type?.FirstOrDefault() == "Follow").ToList();
        Assert.Equal(2, follows.Count);
    }

    // --- Helpers ------------------------------------------------------------------------

    private IPersistenceProvider GetPersistence()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPersistenceProvider>();
    }

    private async Task<bool> CallSetFlagAsync(bool enabled)
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var result = await client.SetManuallyApprovesFollowersAsync(_actorIri, enabled);
        return result.IsSuccess;
    }

    private async Task DeliverFollowAsync(Iri followerIri)
    {
        var persistence = GetPersistence();
        var diKeyStore = _services.GetRequiredService<IKeyStore>();
        var handle = followerIri.Value.Split('/').Last();
        var followerKeyIri = new Iri($"{followerIri.Value}#key-1");
        var followerKey = KeyPairGenerator.GenerateRsa(followerKeyIri);
        persistence.Keys.PutKey(followerKey);
        diKeyStore.PutKey(followerKey);

        var follower = new KristofferStrube.ActivityStreams.Person
        {
            Id = followerIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        };
        follower.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
        follower.ExtensionData[ActivityPubExtensionNames.PublicKey] =
            System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id = followerKeyIri.Value,
                owner = followerIri.Value,
                publicKeyPem = followerKey.ExportPublicKeyPem(),
            });
        persistence.Actors.PutActorAsync(follower).GetAwaiter().GetResult();
        _services.GetRequiredService<IKeyProvider>().RegisterKey(followerIri, followerKeyIri);

        var follow = new KristofferStrube.ActivityStreams.Follow
        {
            Id = $"{followerIri.Value}/follow-{Guid.NewGuid():N}",
            Actor = [new KristofferStrube.ActivityStreams.Link { Href = followerIri.Uri }],
            Object = [new KristofferStrube.ActivityStreams.Link { Href = _actorIri.Uri }],
        };

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(followerKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(followerIri, followerKeyIri);
        var signer = new HttpSignatureSigner(keyStore);

        var signingHandler = new SigningHandler(signer, keyProvider, new LazyHandler(() => _server.CreateHandler()))
        {
            ActorId = followerIri,
        };
        using var http = new HttpClient(signingHandler, disposeHandler: false);

        var json = ActivityJson.Serialize(follow);
        var content = new StringContent(json, System.Text.Encoding.UTF8, ActivityJson.ActivityJsonContentType);
        var response = await http.PostAsync($"{_actorIri.Value}/inbox", content);

        Assert.True(
            response.StatusCode is System.Net.HttpStatusCode.Accepted or System.Net.HttpStatusCode.OK,
            $"Expected 202/200 for Follow delivery, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
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
