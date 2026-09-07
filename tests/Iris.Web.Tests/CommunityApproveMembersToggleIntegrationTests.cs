using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Pipeline;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
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
/// Integration tests for slice 42.1 — <em>community "manuallyApprovesMembers" toggle</em>. They boot
/// the real app (in-memory persistence) in-process via <see cref="WebAppFactory"/> in a
/// <see cref="TestServer"/> and verify that the client's <c>SetManuallyApprovesMembersAsync</c>
/// (the write the UI checkbox triggers) round-trips through the signed outbox pipeline to the
/// community document, and that <c>GetManuallyApprovesMembers</c> reads the flag back.
/// </summary>
public sealed class CommunityApproveMembersToggleIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _communityIri;
    private readonly KeyPair _communityKey;

    public CommunityApproveMembersToggleIntegrationTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        WebAppFactory.ConfigureServices(builder, Base);
        var services = builder.Services;

        // Replace the default IActorDocumentFetcher (which uses a real HttpClientHandler that can't
        // reach the TestServer) with one that reads local actors/communities directly from the
        // in-process persistence. This lets the signature validator resolve keys for local actors
        // without an HTTP round-trip.
        services.AddSingleton<Iris.Server.Security.IActorDocumentFetcher>(sp =>
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

        // Seed the community with a signing key (so it can sign its own outbox publications).
        var persistence = GetPersistence();
        var diKeyStore = _services.GetRequiredService<IKeyStore>();
        _communityIri = new Iri($"{Base}/ap/v1/c/testcomm");
        var keyId = new Iri($"{_communityIri.Value}#key-1");
        _communityKey = KeyPairGenerator.GenerateRsa(keyId);
        persistence.Keys.PutKey(_communityKey);
        diKeyStore.PutKey(_communityKey);

        var community = new KristofferStrube.ActivityStreams.Group
        {
            Id = _communityIri.Value,
            PreferredUsername = "testcomm",
            Name = ["Test Community"],
        };
        community.ExtensionData ??= new Dictionary<string, JsonElement>();
        community.ExtensionData[ActivityPubExtensionNames.PublicKey] =
            JsonSerializer.SerializeToElement(new
            {
                id = keyId.Value,
                owner = _communityIri.Value,
                publicKeyPem = _communityKey.ExportPublicKeyPem(),
            });
        persistence.Communities.PutCommunityAsync(community).GetAwaiter().GetResult();

        // Register the community's key with the key provider (so the server can validate its signatures).
        _services.GetRequiredService<IKeyProvider>().RegisterKey(_communityIri, keyId);
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task FreshCommunity_GetManuallyApprovesMembers_ReturnsNull()
    {
        var doc = await FetchCommunityDocAsync();
        Assert.Null(doc.GetManuallyApprovesMembers());
    }

    [Fact]
    public async Task SetFlag_Enable_SetsFlagOnDocument()
    {
        Assert.True(await CallSetFlagAsync(true), "the Add should be accepted (202)");

        var doc = await FetchCommunityDocAsync();
        Assert.True(doc.GetManuallyApprovesMembers());
    }

    [Fact]
    public async Task SetFlag_Disable_ClearsFlagOnDocument()
    {
        Assert.True(await CallSetFlagAsync(true), "enable should succeed");
        Assert.True(await CallSetFlagAsync(false), "disable should succeed");

        var doc = await FetchCommunityDocAsync();
        Assert.Null(doc.GetManuallyApprovesMembers());
    }

    [Fact]
    public async Task Flag_EnablesJoinRequestRecording()
    {
        // Without the flag, a Join auto-grants membership.
        var joiner1Iri = new Iri($"{Base}/ap/v1/u/joiner1");
        await DeliverJoinAsync(joiner1Iri);
        Assert.True(await IsMemberAsync(joiner1Iri));

        // Enable the flag, then join again — should be recorded as a request, not a member.
        Assert.True(await CallSetFlagAsync(true), "enable should succeed");

        var joiner2Iri = new Iri($"{Base}/ap/v1/u/joiner2");
        await DeliverJoinAsync(joiner2Iri);

        Assert.False(await IsMemberAsync(joiner2Iri));
        Assert.True(await HasJoinRequestAsync(joiner2Iri));
    }

    [Fact]
    public async Task Flag_Disabled_Again_AllowsAutoJoin()
    {
        Assert.True(await CallSetFlagAsync(true));
        var joiner1Iri = new Iri($"{Base}/ap/v1/u/joiner1");
        await DeliverJoinAsync(joiner1Iri);
        Assert.True(await HasJoinRequestAsync(joiner1Iri));

        Assert.True(await CallSetFlagAsync(false));
        var joiner2Iri = new Iri($"{Base}/ap/v1/u/joiner2");
        await DeliverJoinAsync(joiner2Iri);
        Assert.True(await IsMemberAsync(joiner2Iri));
    }

    // --- Helpers ------------------------------------------------------------------------

    private IPersistenceProvider GetPersistence()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPersistenceProvider>();
    }

    private async Task<bool> CallSetFlagAsync(bool enabled)
    {
        var client = BuildSignedClient(_communityIri, _communityKey);
        var result = await client.SetManuallyApprovesMembersAsync(_communityIri, enabled);
        return result.IsSuccess;
    }

    private async Task<KristofferStrube.ActivityStreams.Group> FetchCommunityDocAsync()
    {
        var http = _server.CreateClient();
        var response = await http.GetAsync(_communityIri.Value);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<KristofferStrube.ActivityStreams.Group>(json);
        Assert.NotNull(doc);
        return doc!;
    }

    private async Task<bool> IsMemberAsync(Iri actorIri)
        => await GetPersistence().Communities.IsMemberAsync(_communityIri, actorIri);

    private async Task<bool> HasJoinRequestAsync(Iri actorIri)
        => await GetPersistence().Communities.HasJoinRequestAsync(_communityIri, actorIri);

    private async Task DeliverJoinAsync(Iri joinerIri)
    {
        // Seed a local actor with a signing key so the Join can be signed.
        var persistence = GetPersistence();
        var diKeyStore = _services.GetRequiredService<IKeyStore>();
        var handle = joinerIri.Value.Split('/').Last();
        var joinerKeyIri = new Iri($"{joinerIri.Value}#key-1");
        var joinerKey = KeyPairGenerator.GenerateRsa(joinerKeyIri);
        persistence.Keys.PutKey(joinerKey);
        diKeyStore.PutKey(joinerKey);

        var joiner = new KristofferStrube.ActivityStreams.Person
        {
            Id = joinerIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        };
        joiner.ExtensionData ??= new Dictionary<string, JsonElement>();
        joiner.ExtensionData[ActivityPubExtensionNames.PublicKey] =
            JsonSerializer.SerializeToElement(new
            {
                id = joinerKeyIri.Value,
                owner = joinerIri.Value,
                publicKeyPem = joinerKey.ExportPublicKeyPem(),
            });
        persistence.Actors.PutActorAsync(joiner).GetAwaiter().GetResult();
        _services.GetRequiredService<IKeyProvider>().RegisterKey(joinerIri, joinerKeyIri);

        // Build the Join activity.
        var join = new KristofferStrube.ActivityStreams.Join
        {
            Id = $"{joinerIri.Value}/join-{Guid.NewGuid():N}",
            Actor = [new KristofferStrube.ActivityStreams.Link { Href = joinerIri.Uri }],
            Object = [new KristofferStrube.ActivityStreams.Link { Href = joinerIri.Uri }],
        };

        // Sign and deliver via a SigningHandler over the TestServer transport.
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(joinerKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(joinerIri, joinerKeyIri);
        var signer = new HttpSignatureSigner(keyStore);

        var signingHandler = new SigningHandler(signer, keyProvider, new LazyHandler(() => _server.CreateHandler()))
        {
            ActorId = joinerIri,
        };
        using var http = new HttpClient(signingHandler, disposeHandler: false);

        var json = ActivityJson.Serialize(join);
        var content = new StringContent(json, Encoding.UTF8, ActivityJson.ActivityJsonContentType);
        var response = await http.PostAsync($"{_communityIri.Value}/inbox", content);

        Assert.True(
            response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK,
            $"Expected 202/200 for Join delivery, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
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
    /// An <see cref="Iris.Server.Security.IActorDocumentFetcher"/> that reads local actors and
    /// communities directly from in-process persistence (no HTTP round-trip). Used in integration
    /// tests where the TestServer's default fetcher (real HttpClientHandler) cannot reach the
    /// in-process server.
    /// </summary>
    private sealed class LocalActorDocumentFetcher(IPersistenceProvider persistence)
        : Iris.Server.Security.IActorDocumentFetcher
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
