using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Pipeline;
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
/// Integration tests for slice 44.1 — <em>content warning / sensitive flag on compose (F-28)</em>. They
/// boot the real app (in-memory persistence) in-process via <see cref="WebAppFactory"/> in a
/// <see cref="TestServer"/> and verify that the client's <c>PostNoteAsync</c> with a
/// <see cref="ComposeNote.Build"/>-built sensitive note round-trips the <c>sensitive</c> term and
/// <c>summary</c> through the signed outbox pipeline to the server's object store — the data the feed's
/// reveal toggle (<c>ObjectView</c>) reads via <c>IsSensitive</c>/<c>GetSummary</c>.
/// </summary>
public sealed class ContentWarningComposeIntegrationTests : IDisposable
{
    private const string Base = "https://cw.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;

    public ContentWarningComposeIntegrationTests()
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
    public async Task SensitiveNote_RoundTripsSensitiveAndSummary()
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(
            _actorIri,
            "Behind the curtain",
            sensitive: true,
            summary: "Mild spoilers",
            to: [Public]);

        var result = await client.PostNoteAsync(_actorIri, note);
        Assert.True(result.IsSuccess, $"Post should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var (objectIri, obj) = await FindCreatedNoteAsync("Behind the curtain");
        Assert.NotNull(obj);
        Assert.True(obj!.IsSensitive(), "the stored note must carry the sensitive term");
        Assert.Equal("Mild spoilers", obj.GetSummary());
        Assert.NotNull(objectIri);
    }

    [Fact]
    public async Task PlainNote_IsNotSensitive()
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "Just a normal note", to: [Public]);

        var result = await client.PostNoteAsync(_actorIri, note);
        Assert.True(result.IsSuccess, $"Post should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var (_, obj) = await FindCreatedNoteAsync("Just a normal note");
        Assert.NotNull(obj);
        Assert.False(obj!.IsSensitive(), "a plain note must not be sensitive");
        Assert.Null(obj.GetSummary());
    }

    [Fact]
    public async Task SensitiveNote_SummaryOnlyWhenSensitive()
    {
        // A summary without the sensitive flag is ignored by ComposeNote.Build (not set on the note).
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "Note with ignored summary", sensitive: false, summary: "Ignored", to: [Public]);

        var result = await client.PostNoteAsync(_actorIri, note);
        Assert.True(result.IsSuccess, $"Post should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var (_, obj) = await FindCreatedNoteAsync("Note with ignored summary");
        Assert.NotNull(obj);
        Assert.False(obj!.IsSensitive());
        Assert.Null(obj.GetSummary());
    }

    [Fact]
    public async Task SensitiveNote_AppearsInPublicDocument()
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "CW check", sensitive: true, summary: "Careful", to: [Public]);

        var result = await client.PostNoteAsync(_actorIri, note);
        Assert.True(result.IsSuccess, $"Post should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var (objectIri, _) = await FindCreatedNoteAsync("CW check");
        Assert.NotNull(objectIri);

        var http = _server.CreateClient();
        var response = await http.GetAsync(objectIri!.ToString());

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"sensitive\":true", json);
        Assert.Contains("Careful", json);
    }

    // --- Helpers ------------------------------------------------------------------------

    private static readonly Iri Public = new("https://www.w3.org/ns/activitystreams#Public");

    private IPersistenceProvider GetPersistence()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPersistenceProvider>();
    }

    /// <summary>
    /// Scans the actor's outbox for the <c>Create</c> whose embedded object's content contains
    /// <paramref name="contentFragment"/>, and returns the stored object (looked up by its IRI) plus the
    /// object IRI.
    /// </summary>
    private async Task<(Iri? ObjectIri, IObject? Object)> FindCreatedNoteAsync(string contentFragment)
    {
        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);

        foreach (var item in outbox)
        {
            if (item is not KristofferStrube.ActivityStreams.Create create
                || create.Object is not { } objects)
            {
                continue;
            }

            var embedded = objects.FirstOrDefault() as IObject;
            if (embedded is null || embedded.Content is null)
            {
                continue;
            }

            var content = string.Join(" ", embedded.Content);
            if (!content.Contains(contentFragment, StringComparison.Ordinal))
            {
                continue;
            }

            Iri? objectIri = embedded.Id is { Length: > 0 } id ? new Iri(id) : null;
            IObject? stored = null;
            if (objectIri is { } resolvedIri)
            {
                if (await persistence.Objects.TryGetObjectAsync(resolvedIri, out var foundObj) && foundObj is not null)
                {
                    stored = foundObj;
                }
            }

            return (objectIri, stored ?? embedded);
        }

        throw new InvalidOperationException($"No created note containing '{contentFragment}' found in the outbox.");
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
