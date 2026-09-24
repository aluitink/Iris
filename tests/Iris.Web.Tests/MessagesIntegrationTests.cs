using System.Net;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Compose;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.Data.Accounts;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using Iris.Web;
using Iris.Web.Accounts;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for S116 — <em>DM inbox</em>. They boot the real app (in-memory persistence)
/// in-process via <see cref="WebAppFactory"/> in a <see cref="TestServer"/> and exercise the
/// <c>GET /local/v1/messages</c> and <c>POST /local/v1/messages/read</c> endpoints. The endpoints are
/// cookie-<c>RequireAuthorization</c>-gated (the Blazor UI's Messages page authenticates via the
/// browser session cookie), so the test seeds a local user account for alice, signs in through
/// <c>POST /login</c> to obtain the session cookie, and issues the requests with that cookie. The
/// test seeds a second local actor (bob), posts a public note (excluded) and a direct message from
/// bob to alice (included), and asserts the merged DM list surfaces the DM and the mark-all-read
/// endpoint advances the cursor.
/// </summary>
public sealed class MessagesIntegrationTests : IDisposable
{
    private const string Base = "https://s116-msgs.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _aliceIri;
    private readonly KeyPair _aliceKey;

    public MessagesIntegrationTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Disable antiforgery validation (Phase 94 dev toggle) so the test's POST /login (which has no
        // Data Protection key ring / browser cookie round-trip) is not rejected with a 400. The
        // PermissiveAntiforgery no-op IAntiforgery is registered and the middleware's validation passes.
        builder.Configuration["Iris:Security:EnableAntiforgery"] = "false";
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
                    WebAppFactory.MapMessageEndpoints(endpoints);
                    endpoints.MapActivityPubEndpoints();
                });
            });

        _server = new TestServer(webHostBuilder);
        WebAppFactory.InitializePersistence(_server.Services, builder.Configuration, Base);
        _services = _server.Services;

        var diKeyStore = _services.GetRequiredService<IKeyStore>();
        _aliceIri = new Iri($"{Base}/ap/v1/u/alice");
        var keyId = new Iri($"{_aliceIri.Value}#key-1");
        if (!diKeyStore.TryGetKey(keyId, out var existing) || existing is null)
        {
            throw new InvalidOperationException("Seeded actor key not found.");
        }
        _aliceKey = (KeyPair)existing;
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Messages_ListsDirectMessages_ExcludesPublicPosts()
    {
        var ap = BuildSignedClient(_aliceIri, _aliceKey);
        var http = await BuildAuthenticatedHttpAsync();
        var persistence = GetPersistence();

        // alice posts a public note (excluded from the DM list).
        var publicNote = ComposeNote.Build(_aliceIri, "public note", to: [Public]);
        var publicPosted = await ap.PostNoteAsync(_aliceIri, publicNote);
        Assert.True(publicPosted.IsSuccess, $"Public post should succeed, got HTTP {(int)publicPosted.StatusCode}");

        // bob (a fresh local actor) DMs alice. The cc carries bob's followers collection — the same
        // audience the UI emits (Compose.BuildAudience sets cc=author's followers for Direct visibility,
        // and RewriteOutboundAudienceAsync merges it into the stored note's cc). S120: IsDirectMessage
        // must treat this as a DM despite the non-empty cc.
        var bob = await SeedActorAsync("bob");
        var bobClient = BuildSignedClient(bob.Iri, bob.Key);
        var dmNote = ComposeNote.Build(
            bob.Iri,
            "hello alice, this is a DM",
            to: [_aliceIri],
            cc: [new Iri($"{bob.Iri.Value}/followers")]);
        var dmPosted = await bobClient.PostNoteAsync(bob.Iri, dmNote);
        Assert.True(dmPosted.IsSuccess, $"DM post should succeed, got HTTP {(int)dmPosted.StatusCode}: {dmPosted.Body}");

        // Deliver the DM to alice's inbox (simulating the delivery path; the local-delivery path
        // records the Create in the author's outbox, not the recipient's inbox).
        var create = ExtractCreateFromOutbox(persistence, bob.Iri, "hello alice");
        await persistence.Activities.AddToInboxAsync(_aliceIri, create);

        // GET /local/v1/messages as alice (session cookie).
        var response = await http.GetAsync("/local/v1/messages");
        Assert.True(response.IsSuccessStatusCode, $"GET messages should succeed, got HTTP {(int)response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        var items = doc.RootElement.GetProperty("items");
        var totalItems = doc.RootElement.GetProperty("totalItems").GetInt32();

        // Only the DM should appear (the public note is excluded).
        Assert.Equal(1, totalItems);
        Assert.Equal(1, items.GetArrayLength());

        var firstItem = items[0];
        var contentElement = firstItem.GetProperty("object").GetProperty("content");
        var content = contentElement.ValueKind == JsonValueKind.Array
            ? string.Join(" ", contentElement.EnumerateArray().Select(e => e.GetString()))
            : contentElement.GetString() ?? string.Empty;
        Assert.Contains("hello alice", content);

        // The DM's actor is bob.
        var actorElement = firstItem.GetProperty("actor");
        var actorHref = actorElement.ValueKind == JsonValueKind.Object
            ? (actorElement.GetProperty("href").GetString() ?? string.Empty)
            : (actorElement.ValueKind == JsonValueKind.Array
                ? actorElement[0].GetProperty("href").GetString()
                : actorElement.GetString()) ?? string.Empty;
        Assert.Equal(bob.Iri.Value, actorHref);
    }

    [Fact]
    public async Task Messages_Unauthenticated_IsRejected()
    {
        // A client without the session cookie is rejected: the cookie handler challenges with a
        // redirect to /login for a browser (no Accept: application/json), so the endpoint is never
        // reached. Assert the redirect target is the login page.
        var handler = _server.CreateHandler();
        using var bare = new HttpClient(handler)
        {
            BaseAddress = new Uri(Base),
        };
        var response = await bare.GetAsync("/local/v1/messages");
        Assert.Equal(302, (int)response.StatusCode);
        Assert.Contains("/login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Messages_ReadAdvancesCursor()
    {
        var http = await BuildAuthenticatedHttpAsync();
        var persistence = GetPersistence();

        var bob = await SeedActorAsync("bob");
        var bobClient = BuildSignedClient(bob.Iri, bob.Key);
        var dmNote = ComposeNote.Build(bob.Iri, "dm for read test", to: [_aliceIri]);
        var dmPosted = await bobClient.PostNoteAsync(bob.Iri, dmNote);
        Assert.True(dmPosted.IsSuccess);

        var create = ExtractCreateFromOutbox(persistence, bob.Iri, "dm for read test");
        await persistence.Activities.AddToInboxAsync(_aliceIri, create);

        // Mark all as read.
        var readResponse = await http.PostAsync("/local/v1/messages/read", null);
        Assert.True(readResponse.IsSuccessStatusCode, $"POST messages/read should succeed, got HTTP {(int)readResponse.StatusCode}");
        var readBody = await readResponse.Content.ReadAsStringAsync();
        var readDoc = JsonDocument.Parse(readBody);
        // The unread count is 0 (the cursor was just advanced to now).
        Assert.Equal(0, readDoc.RootElement.GetProperty("unread").GetInt32());

        // The cursor is now set on the account.
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserAccountStore>();
        var account = await store.FindByUsernameAsync("alice");
        Assert.NotNull(account?.MessagesReadAt);
    }

    // --- Helpers ------------------------------------------------------------------------

    private static readonly Iri Public = new("https://www.w3.org/ns/activitystreams#Public");

    /// <summary>
    /// Seeds a local user account for alice (password "alice", hashed with the app's
    /// <see cref="PasswordHasher"/>) and signs in through <c>POST /login</c>, returning an
    /// <see cref="HttpClient"/> that carries the resulting session cookie.
    /// </summary>
    private async Task<HttpClient> BuildAuthenticatedHttpAsync()
    {
        await SeedUserAccountAsync("alice", "alice");

        var handler = _server.CreateHandler();
        var clientHandler = new CookieCarryingHandler
        {
            InnerHandler = handler,
        };
        var client = new HttpClient(clientHandler)
        {
            BaseAddress = new Uri(Base),
        };

        var login = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["handle"] = "alice",
            ["password"] = "alice",
        });
        var loginResponse = await client.PostAsync("/login", login);
        Assert.True(
            loginResponse.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK,
            $"Login should succeed, got HTTP {(int)loginResponse.StatusCode}");

        return client;
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

    private async Task SeedUserAccountAsync(string username, string password)
    {
        var store = _services.GetRequiredService<IUserAccountStore>();
        var actorIri = new Iri($"{Base}/ap/v1/u/{username}");
        var hasher = new PasswordHasher();
        var account = new UserAccount
        {
            Username = username,
            PasswordHash = hasher.Hash(password),
            ActorId = actorIri,
        };
        await store.CreateAsync(account);
    }

    private async Task<(Iri Iri, KeyPair Key)> SeedActorAsync(string name)
    {
        var persistence = GetPersistence();
        var diKeyStore = _services.GetRequiredService<IKeyStore>();
        var actorIri = new Iri($"{Base}/ap/v1/u/{name}");
        var keyId = new Iri($"{actorIri.Value}#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyId);
        persistence.Keys.PutKey(key);
        diKeyStore.PutKey(key);

        var actor = new Person
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

    private static Create ExtractCreateFromOutbox(IPersistenceProvider persistence, Iri actorIri, string contentFragment)
    {
        var outbox = persistence.Activities.GetOutboxAsync(actorIri).GetAwaiter().GetResult();
        foreach (var item in outbox)
        {
            if (item is not Create create || create.Object is not { } objects)
            {
                continue;
            }

            var embedded = objects.FirstOrDefault() as IObject;
            if (embedded?.Content is null)
            {
                continue;
            }

            var content = string.Join(" ", embedded.Content);
            if (!content.Contains(contentFragment, StringComparison.Ordinal))
            {
                continue;
            }

            return create;
        }

        throw new InvalidOperationException($"No Create containing '{contentFragment}' found in outbox.");
    }

    private sealed class LocalActorDocumentFetcher(IPersistenceProvider persistence)
        : IActorDocumentFetcher
    {
        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            if (await persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct) && actor is not null)
            {
                return actor;
            }

            return null;
        }
    }
}
