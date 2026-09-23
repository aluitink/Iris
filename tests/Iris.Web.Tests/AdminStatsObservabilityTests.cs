using System.Collections.ObjectModel;
using System.Net;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.Data.Accounts;
using Iris.Server.Delivery;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Web.Tests;

/// <summary>
/// S58: the <c>GET /local/v1/admin/stats</c> endpoint (the admin dashboard payload) surfaces the
/// federation-observability signal — the dead-letter backlog and the
/// stored-actors-without-a-resolvable-signing-identity gap — that <c>/ap/v1/health</c> already reports
/// as "degraded". Verifies the operator-facing read path (the Blazor dashboard renders these fields).
/// </summary>
public sealed class AdminStatsObservabilityTests
{
    [Fact]
    public async Task AdminStats_ReportsDeadLettersAndSignableActorGap()
    {
        // Three stored actors; the key provider resolves only one (alice). Two are un-signable.
        var keyProvider = new StubKeyProvider(new[] { "https://me.test/ap/v1/u/alice" });

        var persistence = new StubPersistence(
            actors:
            [
                Actor("https://me.test/ap/v1/u/alice"),
                Actor("https://me.test/ap/v1/c/community"),
                Actor("https://me.test/ap/v1/u/bob"),
            ],
            objects: [new Note { Id = "https://me.test/ap/v1/notes/1" }]);

        using var host = BuildHost(
            new StubUserAccountStore([]),
            persistence,
            keyProvider,
            new StubDeadLetterStore(count: 2));
        await host.StartAsync();

        var response = await host.GetTestClient().GetAsync("/local/v1/admin/stats");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"deadLetterCount\":2", body);
        Assert.Contains("\"storedActors\":3", body);
        Assert.Contains("\"resolvableActors\":1", body);
        Assert.Contains("\"postCount\":1", body);
    }

    [Fact]
    public async Task AdminStats_HealthyInstance_ReportsZeroDeadLettersAllSignable()
    {
        var keyProvider = new StubKeyProvider(new[] { "https://me.test/ap/v1/u/alice" });
        var persistence = new StubPersistence(
            actors: [Actor("https://me.test/ap/v1/u/alice")],
            objects: []);

        using var host = BuildHost(
            new StubUserAccountStore([]),
            persistence,
            keyProvider,
            new StubDeadLetterStore(count: 0));
        await host.StartAsync();

        var response = await host.GetTestClient().GetAsync("/local/v1/admin/stats");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"deadLetterCount\":0", body);
        Assert.Contains("\"storedActors\":1", body);
        Assert.Contains("\"resolvableActors\":1", body);
    }

    [Fact]
    public async Task AdminStats_ExcludesRemoteActorsFromSignableCount()
    {
        // S59: ListActorsAsync returns cached remote actors too, but only LOCAL actors (on the instance
        // base) are ever signed for. A remote actor must not inflate the denominator — the old bug
        // reported e.g. 40/4542 because ~4500 remote actors had no key to resolve.
        var keyProvider = new StubKeyProvider(new[] { "https://me.test/ap/v1/u/alice" });
        var persistence = new StubPersistence(
            actors:
            [
                Actor("https://me.test/ap/v1/u/alice"),
                Actor("https://me.test/ap/v1/c/community"),
                // Remote actors cached for the directory — never signed for, must be excluded.
                Actor("https://mastodon.social/users/remote1"),
                Actor("https://lemmy.world/u/remote2"),
                Actor("https://lemmy.world/u/remote3"),
            ],
            objects: []);

        using var host = BuildHost(
            new StubUserAccountStore([]),
            persistence,
            keyProvider,
            new StubDeadLetterStore(count: 0));
        await host.StartAsync();

        var response = await host.GetTestClient().GetAsync("/local/v1/admin/stats");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        // Only the two local actors are counted (the three remote ones are excluded).
        Assert.Contains("\"storedActors\":2", body);
        Assert.Contains("\"resolvableActors\":1", body);
        Assert.DoesNotContain("\"storedActors\":5", body);
    }

    private static IHost BuildHost(
        IUserAccountStore accounts,
        IPersistenceProvider persistence,
        IKeyProvider keyProvider,
        IDeliveryDeadLetterStore deadLetters) =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer();
                builder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddControllers();
                    services.AddSingleton(accounts);
                    services.AddSingleton(persistence);
                    services.AddSingleton(keyProvider);
                    services.AddSingleton(deadLetters);
                    // The instance base (S59): actors on this host are local; the rest are cached
                    // remote actors and are excluded from the signable count.
                    services.Configure<ActivityPubServerOptions>(o => o.BaseUri = new Iri("https://me.test"));
                });
                builder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        WebAppFactory.MapAdminStatsEndpoint(endpoints);
                    });
                });
            })
            .Build();

    private static Actor Actor(string id) => new() { Id = id, Type = ["Person"], PreferredUsername = "x" };

    // --- test doubles ----------------------------------------------------------

    private sealed class StubKeyProvider(IReadOnlyCollection<string> resolvable) : IKeyProvider
    {
        private readonly HashSet<string> _resolvable = new(resolvable);

        public bool TryGetIdentity(Iri actorId, out IIdentity? identity)
        {
            if (_resolvable.Contains(actorId.Value))
            {
                identity = new SystemIdentity(actorId, actorId);
                return true;
            }

            identity = null;
            return false;
        }

        public void RegisterKey(Iri actorId, Iri keyId)
        {
        }
    }

    private sealed class StubUserAccountStore(IReadOnlyCollection<UserAccount> accounts) : IUserAccountStore
    {
        public Task<IReadOnlyCollection<UserAccount>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<UserAccount>>(accounts);

        public Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default) =>
            Task.FromResult<UserAccount?>(null);

        public Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<UserAccount?>(null);

        public Task CreateAsync(UserAccount account, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdatePasswordHashAsync(Guid id, string newHash, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpdateNotificationsReadAtAsync(Guid id, DateTimeOffset readAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> AnyAdminExistsAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);

        public Task UpdateNotificationPrefsAsync(Guid id, NotificationPreferences? prefs, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpdateRoleAsync(Guid id, UserRole role, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubPersistence(IReadOnlyList<Actor> actors, IReadOnlyList<IObject> objects) :
        IPersistenceProvider
    {
        public IActorStore Actors { get; } = new StubActorStore(actors);
        public IObjectStore Objects { get; } = new StubObjectStore(objects);

        public IActivityStore Activities => throw new NotSupportedException();
        public IFollowStore Follows => throw new NotSupportedException();
        public ILikeStore Likes => throw new NotSupportedException();
        public IDislikeStore Dislikes => throw new NotSupportedException();
        public IReplyStore Replies => throw new NotSupportedException();
        public IAnnounceStore Announces => throw new NotSupportedException();
        public IModerationStore Moderation => throw new NotSupportedException();
        public IRelayStore Relays => throw new NotSupportedException();
        public ICreateIndex Creates => throw new NotSupportedException();
        public ICommunityStore Communities => throw new NotSupportedException();
        public IKeyStore Keys => throw new NotSupportedException();
        public IMediaStore Media => throw new NotSupportedException();
    }

    private sealed class StubActorStore(IReadOnlyList<Actor> actors) : IActorStore
    {
        public Task<bool> TryGetActorAsync(Iri actorIri, out Actor? actor, CancellationToken ct = default)
        {
            actor = null;
            return Task.FromResult(false);
        }

        public Task PutActorAsync(Actor actor, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> RemoveActorAsync(Iri actorIri, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Actor>> ListActorsAsync(CancellationToken ct = default) =>
            Task.FromResult(actors);

        public Task<IReadOnlyList<Actor>> SearchActorsAsync(
            string? query, int limit, int offset, CancellationToken ct = default, bool localOnly = false) =>
            Task.FromResult(actors);

        public Task<int> CountSearchMatchesAsync(string? query, CancellationToken ct = default, bool localOnly = false) =>
            Task.FromResult(actors.Count);
    }

    private sealed class StubObjectStore(IReadOnlyList<IObject> objects) : IObjectStore
    {
        public Task<bool> TryGetObjectAsync(Iri objectIri, out IObject? obj, CancellationToken ct = default)
        {
            obj = null;
            return Task.FromResult(false);
        }

        public Task PutObjectAsync(IObject obj, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> TryDeleteObjectAsync(Iri objectIri, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<IObject>> ListObjectsAsync(CancellationToken ct = default) =>
            Task.FromResult(objects);

        public Task<IReadOnlyList<IObject>> ListByActorAsync(Iri actorIri, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IObject>>([]);

        public Task<IReadOnlyList<IObject>> SearchObjectsAsync(
            string? query, int limit, int offset, CancellationToken ct = default) => Task.FromResult(objects);

        public Task<int> CountSearchMatchesAsync(string? query, CancellationToken ct = default) =>
            Task.FromResult(objects.Count);
    }

    private sealed class StubDeadLetterStore(int count) : IDeliveryDeadLetterStore
    {
        public Task AddAsync(DeadLetterEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public int Count => count;

        public Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterEntry>>(new List<DeadLetterEntry>());
    }
}
