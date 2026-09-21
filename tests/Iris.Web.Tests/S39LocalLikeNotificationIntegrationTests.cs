using System.Text.Json;
using Iris.Client;
using Iris.Client.Auth;
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
/// S39 regression test: a local Like (same-instance) must produce a notification for the note's
/// author. The QA finding (Pass 157/159/172) reported that on the live QA cluster, a local Like
/// (ii-a2 → ii-a1) updated the note's likedCount but produced NO notification for ii-a1, while the
/// B-side author received equivalent A-side interactions. This test exercises the full outbox-publish
/// path (signed Like → OutboxPublishHandler → RecordLikeLocalAsync → AddToInboxAsync) and asserts
/// the Like appears in the recipient's inbox (the source of the /local/v1/notifications query).
/// </summary>
public sealed class S39LocalLikeNotificationIntegrationTests : IDisposable
{
    private const string Base = "https://s39-notif.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _aliceIri;
    private readonly KeyPair _aliceKey;

    public S39LocalLikeNotificationIntegrationTests()
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
        _aliceIri = new Iri($"{Base}/ap/v1/u/alice");
        var keyId = new Iri($"{_aliceIri.Value}#key-1");
        if (!diKeyStore.TryGetKey(keyId, out var existing) || existing is null)
        {
            throw new InvalidOperationException("Seeded actor key not found.");
        }
        _aliceKey = (KeyPair)existing;
    }

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// S39 core assertion: a local Like on a note must land in the note author's inbox so the
    /// /local/v1/notifications endpoint surfaces it.
    /// </summary>
    [Fact]
    public async Task LocalLike_LandsInAuthorInbox_ProducesNotification()
    {
        // Arrange: alice posts a note.
        var aliceClient = BuildSignedClient(_aliceIri, _aliceKey);
        var noteIri = await PostNoteAsync(aliceClient, "like this note");

        // Act: bob (a fresh local actor) Likes the note via the signed outbox-publish path.
        var bob = await SeedActorAsync("bob");
        var bobClient = BuildSignedClient(bob.Iri, bob.Key);
        var likeResult = await bobClient.LikeAsync(bob.Iri, noteIri);
        Assert.True(likeResult.IsSuccess,
            $"Like should succeed, got HTTP {(int)likeResult.StatusCode}: {likeResult.Body}");

        // Assert: the Like is in alice's inbox (the source of /local/v1/notifications).
        var persistence = GetPersistence();
        var inbox = await persistence.Activities.GetInboxAsync(_aliceIri);
        var likes = inbox.OfType<Activity>()
            .Where(a => a.Type is { } t && t.FirstOrDefault() == "Like")
            .ToList();

        if (likes.Count == 0)
        {
            throw new InvalidOperationException(
                $"S39: the local Like did not land in the author's inbox — no notification will be produced. " +
                $"Inbox contents: {string.Join(", ", inbox.Select(i => i.GetType().Name))}");
        }

        var like = likes[0];
        var likerIri = like.Actor?.FirstOrDefault()?.ResolveObjectIri()?.Value;
        Assert.Equal(bob.Iri.Value, likerIri);
    }

    /// <summary>
    /// S39 facet: a local reply (Create with inReplyTo) must land in the parent author's inbox.
    /// </summary>
    [Fact]
    public async Task LocalReply_LandsInParentAuthorInbox_ProducesNotification()
    {
        // Arrange: alice posts a note.
        var aliceClient = BuildSignedClient(_aliceIri, _aliceKey);
        var noteIri = await PostNoteAsync(aliceClient, "reply to this");

        // Act: bob replies to the note (a Create with inReplyTo).
        var bob = await SeedActorAsync("bob");
        var bobClient = BuildSignedClient(bob.Iri, bob.Key);
        var replyNote = ComposeNote.Build(bob.Iri, "a reply", to: [Public]);
        replyNote.InReplyTo = [new Link { Href = new Uri(noteIri.Value) }];
        var replyResult = await bobClient.PostNoteAsync(bob.Iri, replyNote);
        if (!replyResult.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Reply should succeed, got HTTP {(int)replyResult.StatusCode}: {replyResult.Body}");
        }

        // Assert: the Create (reply) is in alice's inbox.
        var persistence = GetPersistence();
        var inbox = await persistence.Activities.GetInboxAsync(_aliceIri);
        var creates = inbox.OfType<Activity>()
            .Where(a => a.Type is { } t && t.FirstOrDefault() == "Create")
            .ToList();

        // The reply's Create should be in alice's inbox (delivered to the parent author).
        var replyCreate = creates.FirstOrDefault(c =>
        {
            if (c is not Create cr || cr.Object is not { } objs)
            {
                return false;
            }

            var obj = objs.FirstOrDefault() as IObject;
            var parentIri = obj.GetParentIri();
            return parentIri?.Value == noteIri.Value;
        });

        if (replyCreate is null)
        {
            throw new InvalidOperationException(
                $"S39: the local reply's Create did not land in the parent author's inbox. " +
                $"Inbox Create count: {creates.Count}");
        }
    }

    /// <summary>
    /// S39 facet (Pass 172): a local follow-request (gated follow) must land in the target's inbox
    /// so the owner sees a "sent you a follow request" notification.
    /// </summary>
    [Fact]
    public async Task LocalFollowRequest_LandsInTargetInbox_ProducesNotification()
    {
        // Arrange: alice has manuallyApprovesFollowers = true (gated).
        var persistence = GetPersistence();
        await SetManuallyApprovesFollowersAsync(persistence, _aliceIri, true);

        // Act: bob follows alice (a Follow activity via the signed outbox-publish path).
        var bob = await SeedActorAsync("bob");
        var bobClient = BuildSignedClient(bob.Iri, bob.Key);
        var followResult = await bobClient.FollowAsync(bob.Iri, _aliceIri);
        Assert.True(followResult.IsSuccess,
            $"Follow should succeed, got HTTP {(int)followResult.StatusCode}: {followResult.Body}");

        // Assert: the Follow is in alice's inbox.
        var inbox = await persistence.Activities.GetInboxAsync(_aliceIri);
        var follows = inbox.OfType<Activity>()
            .Where(a => a.Type is { } t && t.FirstOrDefault() == "Follow")
            .ToList();

        if (follows.Count == 0)
        {
            throw new InvalidOperationException(
                $"S39: the local follow-request did not land in the target's inbox — no notification. " +
                $"Inbox contents: {string.Join(", ", inbox.Select(i => i.GetType().Name))}");
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<Iri> PostNoteAsync(IActivityPubClient client, string content)
    {
        var note = ComposeNote.Build(_aliceIri, content, to: [Public]);
        var result = await client.PostNoteAsync(_aliceIri, note);
        Assert.True(result.IsSuccess, $"Post should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_aliceIri);
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

    private static async Task SetManuallyApprovesFollowersAsync(
        IPersistenceProvider persistence,
        Iri actorIri,
        bool value)
    {
        if (await persistence.Actors.TryGetActorAsync(actorIri, out var actor, default)
            && actor is { } localActor)
        {
            var ext = localActor.ExtensionData is { } existingExt
                ? new Dictionary<string, JsonElement>(existingExt)
                : new Dictionary<string, JsonElement>();
            ext["manuallyApprovesFollowers"] = JsonDocument.Parse(value ? "true" : "false").RootElement.Clone();
            localActor.ExtensionData = ext;
            await persistence.Actors.PutActorAsync(localActor);
        }
    }

    private static readonly Iri Public = new("https://www.w3.org/ns/activitystreams#Public");

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
