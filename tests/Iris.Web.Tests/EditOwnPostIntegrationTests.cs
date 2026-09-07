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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for slice 44.2 — <em>edit own post (F-02)</em>. They boot the real app
/// (in-memory persistence) in-process via <see cref="WebAppFactory"/> in a <see cref="TestServer"/> and
/// verify that the client's <c>UpdateNoteAsync</c> (an <c>Update</c> activity through the signed outbox
/// pipeline) refreshes the stored note in place: a later read of the note's IRI serves the new content,
/// and the update is accepted only when the editing actor is the note's author.
/// </summary>
public sealed class EditOwnPostIntegrationTests : IDisposable
{
    private const string Base = "https://edit.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;

    public EditOwnPostIntegrationTests()
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
    public async Task UpdateNote_RefreshesStoredContent()
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "Original text", to: [Public]);

        var posted = await client.PostNoteAsync(_actorIri, note);
        Assert.True(posted.IsSuccess, $"Post should succeed, got HTTP {(int)posted.StatusCode}: {posted.Body}");

        var (objectIri, _) = await FindCreatedNoteAsync("Original text");
        Assert.NotNull(objectIri);

        var updated = new KristofferStrube.ActivityStreams.Note
        {
            Id = objectIri!.Value.Value,
            Content = ["Edited text"],
            AttributedTo = [new KristofferStrube.ActivityStreams.Link { Href = new Uri(_actorIri.Value) }],
        };

        var result = await client.UpdateNoteAsync(_actorIri, updated);
        Assert.True(result.IsSuccess, $"Update should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var (_, stored) = await FindCreatedNoteAsync("Edited text");
        Assert.NotNull(stored);
        Assert.Equal("Edited text", JoinContent(stored));
        Assert.DoesNotContain("Original text", JoinContent(stored));
    }

    [Fact]
    public async Task UpdateNote_PreservesSensitiveAndSummary()
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "Original CW", sensitive: true, summary: "Careful", to: [Public]);

        var posted = await client.PostNoteAsync(_actorIri, note);
        Assert.True(posted.IsSuccess, $"Post should succeed, got HTTP {(int)posted.StatusCode}: {posted.Body}");

        var (objectIri, _) = await FindCreatedNoteAsync("Original CW");
        Assert.NotNull(objectIri);

        // Edit the content but carry the sensitive term + summary so they are preserved on the object.
        var updated = ComposeNote.Build(_actorIri, "Edited CW", sensitive: true, summary: "Careful", to: [Public]);
        updated.Id = objectIri!.Value.Value;

        var result = await client.UpdateNoteAsync(_actorIri, updated);
        Assert.True(result.IsSuccess, $"Update should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var (_, stored) = await FindCreatedNoteAsync("Edited CW");
        Assert.NotNull(stored);
        Assert.True(stored!.IsSensitive(), "the sensitive term must survive the edit");
        Assert.Equal("Careful", stored.GetSummary());
        Assert.Equal("Edited CW", JoinContent(stored));
    }

    [Fact]
    public async Task UpdateNote_NotAuthor_IsNoOp()
    {
        var ownerClient = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "Alice's note", to: [Public]);

        var posted = await ownerClient.PostNoteAsync(_actorIri, note);
        Assert.True(posted.IsSuccess, $"Post should succeed, got HTTP {(int)posted.StatusCode}: {posted.Body}");

        var (objectIri, _) = await FindCreatedNoteAsync("Alice's note");
        Assert.NotNull(objectIri);

        // A second, distinct actor attempts to edit the note. It is not the stored note's author, so the
        // server's owner guard rejects it (a no-op): the stored content is unchanged.
        var intruder = await SeedRemoteActorAsync("mallory");
        var intruderClient = BuildSignedClient(intruder.Iri, intruder.Key);

        var updated = new KristofferStrube.ActivityStreams.Note
        {
            Id = objectIri!.Value.Value,
            Content = ["Hijacked text"],
            AttributedTo = [new KristofferStrube.ActivityStreams.Link { Href = new Uri(intruder.Iri.Value) }],
        };

        var result = await intruderClient.UpdateNoteAsync(intruder.Iri, updated);

        var (_, stored) = await FindCreatedNoteAsync("Alice's note");
        Assert.NotNull(stored);
        Assert.Equal("Alice's note", JoinContent(stored));
    }

    [Fact]
    public async Task UpdateNote_ServesUpdatedContentOnGet()
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "Before edit", to: [Public]);

        var posted = await client.PostNoteAsync(_actorIri, note);
        Assert.True(posted.IsSuccess, $"Post should succeed, got HTTP {(int)posted.StatusCode}: {posted.Body}");

        var (objectIri, _) = await FindCreatedNoteAsync("Before edit");
        Assert.NotNull(objectIri);

        var updated = new KristofferStrube.ActivityStreams.Note
        {
            Id = objectIri!.Value.Value,
            Content = ["After edit"],
            AttributedTo = [new KristofferStrube.ActivityStreams.Link { Href = new Uri(_actorIri.Value) }],
        };

        var result = await client.UpdateNoteAsync(_actorIri, updated);
        Assert.True(result.IsSuccess, $"Update should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        // A fresh GET of the note's IRI (via the client, as the UI's object-detail page does) must serve
        // the updated content, not the original.
        var noteIri = objectIri!.Value;
        var fetched = await client.GetObjectAsync(noteIri);
        Assert.NotNull(fetched);
        Assert.Equal("After edit", JoinContent(fetched));
    }

    [Fact]
    public async Task UpdateNote_UpdatesPublishedNoteTwice()
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "v1", to: [Public]);

        var posted = await client.PostNoteAsync(_actorIri, note);
        Assert.True(posted.IsSuccess, $"Post should succeed, got HTTP {(int)posted.StatusCode}: {posted.Body}");

        var (objectIri, _) = await FindCreatedNoteAsync("v1");
        Assert.NotNull(objectIri);

        var first = new KristofferStrube.ActivityStreams.Note
        {
            Id = objectIri!.Value.Value,
            Content = ["v2"],
            AttributedTo = [new KristofferStrube.ActivityStreams.Link { Href = new Uri(_actorIri.Value) }],
        };

        Assert.True((await client.UpdateNoteAsync(_actorIri, first)).IsSuccess);

        var second = new KristofferStrube.ActivityStreams.Note
        {
            Id = objectIri!.Value.Value,
            Content = ["v3"],
            AttributedTo = [new KristofferStrube.ActivityStreams.Link { Href = new Uri(_actorIri.Value) }],
        };

        Assert.True((await client.UpdateNoteAsync(_actorIri, second)).IsSuccess);

        var (_, stored) = await FindCreatedNoteAsync("v3");
        Assert.NotNull(stored);
        Assert.Equal("v3", JoinContent(stored));
    }

    // --- Helpers ------------------------------------------------------------------------

    private static readonly Iri Public = new("https://www.w3.org/ns/activitystreams#Public");

    private static string JoinContent(KristofferStrube.ActivityStreams.IObject? obj)
        => obj?.Content is { } content ? string.Join(" ", content) : string.Empty;

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
    private async Task<(Iri? ObjectIri, KristofferStrube.ActivityStreams.IObject? Object)> FindCreatedNoteAsync(string contentFragment)
    {
        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);

        foreach (var item in outbox)
        {
            // Match either the original Create or a later Update of the same object: both carry the
            // embedded note, and after an edit the outbox holds the Update (with the new content).
            var objects = (item as KristofferStrube.ActivityStreams.Activity)?.Object;
            if (objects is not { })
            {
                continue;
            }

            var embedded = objects.FirstOrDefault() as KristofferStrube.ActivityStreams.IObject;
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
            KristofferStrube.ActivityStreams.IObject? stored = null;
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

    /// <summary>
    /// Seeds a fresh local actor (a <c>Person</c> with its own key pair) into the persistence key store,
    /// the DI key store, and the DI key provider, so a signed client can act as that actor and the server
    /// can verify its signatures. Returns the actor's IRI and key pair.
    /// </summary>
    private async Task<(Iri Iri, KeyPair Key)> SeedRemoteActorAsync(string name)
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
        actor.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
        actor.ExtensionData[ActivityPubExtensionNames.PublicKey] =
            System.Text.Json.JsonSerializer.SerializeToElement(new
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
