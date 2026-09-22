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
        // S24-D2: the outbox is now ownership-filtered — foreign (inbound) Follow activities are no
        // longer returned. Use the dedicated follow-request queue (the production API) instead.
        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.GetFollowRequestsAsync(_actorIri);
        Assert.True(result.IsSuccess, $"queue list should succeed, got HTTP {(int)result.StatusCode}");
        var actors = System.Text.Json.JsonSerializer.Deserialize<List<string>>(result.Body) ?? [];
        Assert.Equal([followerIri.Value], actors);

        // No Accept activity should have been auto-sent (the follow is held for approval).
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);
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

        // S24-D2: the outbox is now ownership-filtered — foreign (inbound) Follow activities are no
        // longer returned. Use the dedicated follow-request queue (the production API) instead.
        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.AcceptFollowRequestAsync(_actorIri, followerIri);
        Assert.True(result.IsSuccess, $"Accept should succeed, got HTTP {(int)result.StatusCode}");
    }

    [Fact]
    public async Task RejectFollow_SendsRejectActivity()
    {
        Assert.True(await CallSetFlagAsync(true));

        var followerIri = new Iri($"{Base}/ap/v1/u/follower1");
        await DeliverFollowAsync(followerIri);

        // S24-D2: the outbox is now ownership-filtered — foreign (inbound) Follow activities are no
        // longer returned. Use the dedicated follow-request queue (the production API) instead.
        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.RejectFollowRequestAsync(_actorIri, followerIri);
        Assert.True(result.IsSuccess, $"Reject should succeed, got HTTP {(int)result.StatusCode}");
    }

    [Fact]
    public async Task MultipleFollows_AllAppearInOutbox()
    {
        Assert.True(await CallSetFlagAsync(true));

        var follower1Iri = new Iri($"{Base}/ap/v1/u/follower1");
        var follower2Iri = new Iri($"{Base}/ap/v1/u/follower2");
        await DeliverFollowAsync(follower1Iri);
        await DeliverFollowAsync(follower2Iri);

        // S24-D2: the outbox is now ownership-filtered — foreign (inbound) Follow activities are no
        // longer returned. Use the dedicated follow-request queue (the production API) instead.
        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.GetFollowRequestsAsync(_actorIri);
        Assert.True(result.IsSuccess, $"queue list should succeed, got HTTP {(int)result.StatusCode}");
        var actors = System.Text.Json.JsonSerializer.Deserialize<List<string>>(result.Body) ?? [];
        Assert.Equal(2, actors.Count);
        Assert.Contains(follower1Iri.Value, actors);
        Assert.Contains(follower2Iri.Value, actors);
    }

    // --- Phase 100: the dedicated follow-request queue (GET /local/v1/u/{handle}/requests) ----

    [Fact]
    public async Task GatedFollow_RecordsFollowRequestEdge_AppearsInQueue()
    {
        Assert.True(await CallSetFlagAsync(true));
        var followerIri = new Iri($"{Base}/ap/v1/u/follower1");
        await DeliverFollowAsync(followerIri);

        var persistence = GetPersistence();

        // The pending follow-request edge is recorded (the queue's source of truth, independent of the
        // outbox).
        Assert.True(
            await persistence.Follows.HasFollowRequestAsync(followerIri, _actorIri),
            "a pending follow-request edge should be recorded for a gated follow");

        // The queue endpoint lists the requester (owner-authenticated via the LocalModeration client).
        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.GetFollowRequestsAsync(_actorIri);
        Assert.True(result.IsSuccess, $"queue list should succeed, got HTTP {(int)result.StatusCode}");
        var actors = System.Text.Json.JsonSerializer.Deserialize<List<string>>(result.Body) ?? [];
        Assert.Equal([followerIri.Value], actors);
    }

    [Fact]
    public async Task AcceptFollowRequest_DrainsQueue()
    {
        Assert.True(await CallSetFlagAsync(true));
        var followerIri = new Iri($"{Base}/ap/v1/u/follower1");
        await DeliverFollowAsync(followerIri);

        var persistence = GetPersistence();
        Assert.True(await persistence.Follows.HasFollowRequestAsync(followerIri, _actorIri));

        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.AcceptFollowRequestAsync(_actorIri, followerIri);
        Assert.True(result.IsSuccess, $"accept should succeed, got HTTP {(int)result.StatusCode}");

        // The pending request is drained …
        Assert.False(
            await persistence.Follows.HasFollowRequestAsync(followerIri, _actorIri),
            "the pending follow-request should be removed after accept");

        // … the follow edge is confirmed (the held follow is now an actual follow) …
        Assert.True(
            await persistence.Follows.IsFollowingAsync(followerIri, _actorIri),
            "the follower→actor follow edge should be recorded after accept");

        // … and the queue is now empty.
        var listResult = await modClient.GetFollowRequestsAsync(_actorIri);
        var actors = System.Text.Json.JsonSerializer.Deserialize<List<string>>(listResult.Body) ?? [];
        Assert.Empty(actors);
    }

    [Fact]
    public async Task RejectFollowRequest_DrainsQueue_NoFollowEdge()
    {
        Assert.True(await CallSetFlagAsync(true));
        var followerIri = new Iri($"{Base}/ap/v1/u/follower1");
        await DeliverFollowAsync(followerIri);

        var persistence = GetPersistence();
        Assert.True(await persistence.Follows.HasFollowRequestAsync(followerIri, _actorIri));

        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.RejectFollowRequestAsync(_actorIri, followerIri);
        Assert.True(result.IsSuccess, $"reject should succeed, got HTTP {(int)result.StatusCode}");

        // The pending request is drained …
        Assert.False(
            await persistence.Follows.HasFollowRequestAsync(followerIri, _actorIri),
            "the pending follow-request should be removed after reject");

        // … and NO follow edge is granted (a rejected hold is not a follow).
        Assert.False(
            await persistence.Follows.IsFollowingAsync(followerIri, _actorIri),
            "no follow edge should be recorded after reject");
    }

    [Fact]
    public async Task AcceptFollowRequest_UnknownRequester_ReturnsNotFound()
    {
        Assert.True(await CallSetFlagAsync(true));
        var stranger = new Iri($"{Base}/ap/v1/u/stranger");
        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.AcceptFollowRequestAsync(_actorIri, stranger);
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task WithoutFlag_FollowDoesNotRecordFollowRequest()
    {
        // No flag set (the default): an inbound Follow auto-accepts and does NOT record a pending
        // follow-request (there is nothing to approve).
        var followerIri = new Iri($"{Base}/ap/v1/u/follower1");
        await DeliverFollowAsync(followerIri);

        var persistence = GetPersistence();
        Assert.False(
            await persistence.Follows.HasFollowRequestAsync(followerIri, _actorIri),
            "no pending follow-request should be recorded for an auto-accepted (no-flag) follow");

        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.GetFollowRequestsAsync(_actorIri);
        var actors = System.Text.Json.JsonSerializer.Deserialize<List<string>>(result.Body) ?? [];
        Assert.Empty(actors);
    }

    [Fact]
    public async Task LocalFollowOfGatedActor_RecordsFollowRequestEdge()
    {
        // A LOCAL follow (both the follower and the target are on this instance) is recorded via the
        // outbox-publish path (RecordFollowLocalAsync), not the inbox handler. When the target (alice)
        // has the manually-approve gate on, the pending follow-request edge must still be recorded here —
        // otherwise a local follower of a gated actor would never appear in the follow-approval queue.
        Assert.True(await CallSetFlagAsync(true));

        var followerIri = new Iri($"{Base}/ap/v1/u/bob");
        await DeliverLocalFollowAsync(followerIri);

        var persistence = GetPersistence();
        Assert.True(
            await persistence.Follows.HasFollowRequestAsync(followerIri, _actorIri),
            "a pending follow-request edge should be recorded for a local follow of a gated actor");

        // S34: the Follow edge is WITHHELD while the request is pending — a local gated follow must not
        // surface the requester in the target's public `followers` collection (backed by the Follow
        // edge) before the owner accepts. Only the pending request edge is recorded here.
        Assert.False(
            await persistence.Follows.IsFollowingAsync(followerIri, _actorIri),
            "the Follow edge should be withheld for a pending local follow of a gated actor (S34)");

        var modClient = BuildLocalModerationClient(_actorIri, "alice", "alice");
        var result = await modClient.GetFollowRequestsAsync(_actorIri);
        Assert.True(result.IsSuccess, $"queue list should succeed, got HTTP {(int)result.StatusCode}");
        var actors = System.Text.Json.JsonSerializer.Deserialize<List<string>>(result.Body) ?? [];
        Assert.Equal([followerIri.Value], actors);
    }

    [Fact]
    public async Task LocalFollowOfNonGatedActor_DoesNotRecordFollowRequest()
    {
        // No gate (the default): a local follow auto-accepts and does NOT record a pending follow-request.
        var followerIri = new Iri($"{Base}/ap/v1/u/bob");
        await DeliverLocalFollowAsync(followerIri);

        var persistence = GetPersistence();
        Assert.False(
            await persistence.Follows.HasFollowRequestAsync(followerIri, _actorIri),
            "no pending follow-request should be recorded for a local follow of a non-gated actor");
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

    /// <summary>
    /// Seeds a local follower actor and publishes a Follow to its OWN outbox (the local-follow path —
    /// the outbox-publish handler records the edge via <c>RecordFollowLocalAsync</c>, the outbox-side
    /// twin of the inbox <c>FollowActivityHandler</c>). Unlike <see cref="DeliverFollowAsync"/> (which
    /// delivers to the target's inbox, simulating a remote follow), this exercises the path a local user
    /// takes when they follow another local user from the UI.
    /// </summary>
    private async Task DeliverLocalFollowAsync(Iri followerIri)
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
        // The local-follow path: the follower publishes to its OWN outbox (Decision 055 — the client
        // never addresses the recipient's inbox for an activity it authors; the server records the edge
        // and handles the recipient hop).
        var response = await http.PostAsync($"{followerIri.Value}/outbox", content);

        Assert.True(
            response.StatusCode is System.Net.HttpStatusCode.Accepted or System.Net.HttpStatusCode.OK,
            $"Expected 202/200 for local Follow publish, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
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

    /// <summary>
    /// Builds a <see cref="ILocalModerationClient"/> bound to the actor, using Basic auth
    /// (<paramref name="user"/>/<paramref name="pass"/>) — the same surface the Profile page's Requests
    /// tab uses for the follow-request queue (Phase 100).
    /// </summary>
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
