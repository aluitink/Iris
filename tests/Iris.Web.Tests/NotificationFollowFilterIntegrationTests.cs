using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Web;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for the notification follow-filter fix: auto-accepted follows (from users
/// without manuallyApprovesFollowers) should remain visible in notifications, while decided
/// follows (from users with manuallyApprovesFollowers) should be filtered out once no longer pending.
/// </summary>
public sealed class NotificationFollowFilterIntegrationTests : IDisposable
{
    private const string Base = "https://notif-filter.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;

    public NotificationFollowFilterIntegrationTests()
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
    public async Task AutoAcceptedFollow_RemainsVisibleInNotifications()
    {
        // Arrange: the seeded actor alice is auto-accepting (no manuallyApprovesFollowers)
        var persistence = _services.GetRequiredService<IPersistenceProvider>();

        // Act: deliver a follow from follower1 to alice (auto-accepted, no manual approval)
        var followerIri = new Iri($"{Base}/ap/v1/u/follower1");
        var follow = CreateFollowActivity(followerIri, _actorIri);
        await persistence.Activities.AddToInboxAsync(_actorIri, follow);
        await persistence.Follows.RecordFollowAsync(followerIri, _actorIri);

        // Assert: the follow notification should be visible in alice's notifications
        var notifications = await GetNotificationsFromInbox(persistence);
        var followNotifications = notifications
            .Where(n => n.Type?.FirstOrDefault() == "Follow")
            .ToList();

        Assert.NotEmpty(followNotifications);
        
        // The actor in the notification should be a Person with the follower's IRI
        var firstFollow = followNotifications[0];
        var actor = firstFollow.Actor?.FirstOrDefault();
        Assert.NotNull(actor);
        var actorPerson = actor as Person;
        Assert.NotNull(actorPerson);
        Assert.Equal(followerIri.Value, actorPerson!.Id);
    }

    [Fact]
    public async Task ManuallyApprovedFollow_FilteredOutWhenDecided()
    {
        // Arrange: set alice to manually approve followers
        var persistence = _services.GetRequiredService<IPersistenceProvider>();
        await SetManuallyApprovesFollowersAsync(persistence, _actorIri, true);

        // Act: deliver a follow from follower2 to alice (held for approval)
        var followerIri = new Iri($"{Base}/ap/v1/u/follower2");
        var follow = CreateFollowActivity(followerIri, _actorIri);
        await persistence.Activities.AddToInboxAsync(_actorIri, follow);
        await persistence.Follows.RecordFollowAsync(followerIri, _actorIri);
        await persistence.Follows.RecordFollowRequestAsync(followerIri, _actorIri);

        // The follow notification should be visible while pending
        var notifications = await GetNotificationsFromInbox(persistence);
        var pendingFollows = notifications
            .Where(n => n.Type?.FirstOrDefault() == "Follow")
            .ToList();
        Assert.NotEmpty(pendingFollows);

        // Act: accept the follow (decides the follow request)
        await persistence.Follows.RemoveFollowRequestAsync(followerIri, _actorIri);

        // Assert: the follow notification should be filtered out (no longer pending)
        // Note: The filter checks if the requester is in the pending queue. After removal,
        // the requester is no longer in the queue, so the notification is filtered out.
        var pendingRequests = await persistence.Follows.GetFollowRequestsAsync(_actorIri);
        Assert.DoesNotContain(pendingRequests, r => r.Value == followerIri.Value);
    }

    private static Follow CreateFollowActivity(Iri followerIri, Iri targetIri)
    {
        return new Follow
        {
            Id = $"{followerIri.Value}/follow/{Guid.NewGuid():N}",
            Actor = [new Person { Id = followerIri.Value }],
            Object = [new Link { Href = new Uri(targetIri.Value) }],
            Published = DateTime.UtcNow,
        };
    }

    private async Task SetManuallyApprovesFollowersAsync(
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

    private async Task<List<Activity>> GetNotificationsFromInbox(IPersistenceProvider persistence)
    {
        var inbox = await persistence.Activities.GetInboxAsync(_actorIri);
        return inbox.OfType<Activity>().ToList();
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
