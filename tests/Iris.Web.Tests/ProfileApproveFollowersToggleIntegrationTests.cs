using System.Net;
using System.Text.Json;
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
/// Integration tests for slice 42.2 — <em>person "manuallyApprovesFollowers" toggle</em>. They boot
/// the real app (in-memory persistence) in-process via <see cref="WebAppFactory"/> in a
/// <see cref="TestServer"/> and verify that the client's <c>SetManuallyApprovesFollowersAsync</c>
/// (the write the UI checkbox triggers) round-trips through the signed outbox pipeline to the
/// actor document, and that <c>GetManuallyApprovesFollowers</c> reads the flag back.
/// </summary>
public sealed class ProfileApproveFollowersToggleIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;

    public ProfileApproveFollowersToggleIntegrationTests()
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

        // The seeded actor (alice) is the one under test.
        var persistence = GetPersistence();
        var diKeyStore = _services.GetRequiredService<IKeyStore>();
        _actorIri = new Iri($"{Base}/ap/v1/u/alice");

        // The seeded actor already has a key; retrieve it.
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
    public async Task FreshActor_GetManuallyApprovesFollowers_ReturnsNull()
    {
        var doc = await FetchActorDocAsync();
        Assert.Null(doc.GetManuallyApprovesFollowers());
    }

    [Fact]
    public async Task SetFlag_Enable_SetsFlagOnDocument()
    {
        Assert.True(await CallSetFlagAsync(true), "the Add should be accepted (202)");

        var doc = await FetchActorDocAsync();
        Assert.True(doc.GetManuallyApprovesFollowers());
    }

    [Fact]
    public async Task SetFlag_Disable_ClearsFlagOnDocument()
    {
        Assert.True(await CallSetFlagAsync(true), "enable should succeed");
        Assert.True(await CallSetFlagAsync(false), "disable should succeed");

        var doc = await FetchActorDocAsync();
        Assert.Null(doc.GetManuallyApprovesFollowers());
    }

    [Fact]
    public async Task Flag_Set_AppearOnPublicActorDocument()
    {
        // The UI reads the flag from the public actor document (GET /ap/v1/u/alice).
        Assert.True(await CallSetFlagAsync(true), "enable should succeed");

        var http = _server.CreateClient();
        var response = await http.GetAsync(_actorIri.Value);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("manuallyApprovesFollowers", json);
        Assert.Contains("true", json);
    }

    [Fact]
    public async Task Flag_Clear_RemovedFromPublicActorDocument()
    {
        Assert.True(await CallSetFlagAsync(true));
        Assert.True(await CallSetFlagAsync(false));

        var http = _server.CreateClient();
        var response = await http.GetAsync(_actorIri.Value);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("manuallyApprovesFollowers", json);
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

    private async Task<KristofferStrube.ActivityStreams.Person> FetchActorDocAsync()
    {
        var http = _server.CreateClient();
        var response = await http.GetAsync(_actorIri.Value);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<KristofferStrube.ActivityStreams.Person>(json);
        Assert.NotNull(doc);
        return doc!;
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
