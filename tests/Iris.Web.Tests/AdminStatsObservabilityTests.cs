using System.Collections.ObjectModel;
using System.Net;
using System.Threading.Channels;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.Data.Accounts;
using Iris.Server.Delivery;
using Iris.Server.Stores;
using Iris.Web.Accounts;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public async Task DeadLetters_ListReturnsFailedDeliveries()
    {
        // S60: the operator-facing list shows each dead-lettered delivery (recipient inbox, failure
        // kind/detail, timestamp) so a downed peer can be identified.
        var deadLetters = new StubDeadLetterStore(entries:
        [
            new DeadLetterEntry(
                new Iri("https://down-peer.test/inbox"),
                new Create { Object = [new Note { Id = "https://me.test/ap/v1/notes/1" }] },
                new Iri("https://me.test/ap/v1/u/alice"),
                3,
                DeadLetterFailureKind.TransportError,
                "Connection refused",
                new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero)),
        ]);

        using var host = BuildDeadLetterHost(deadLetters, new StubDeliveryQueue());
        await host.StartAsync();

        var response = await host.GetTestClient().GetAsync("/local/v1/admin/dead-letters");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"count\":1", body);
        Assert.Contains("https://down-peer.test/inbox", body);
        Assert.Contains("TransportError", body);
        Assert.Contains("Connection refused", body);
    }

    [Fact]
    public async Task DeadLetters_ReplayReEnqueuesAndRemoves()
    {
        // S60: replaying a dead letter re-enqueues the original job (attempts reset) and clears the
        // entry, so a recovered peer receives the delivery and the list reflects the drop.
        var entry = new DeadLetterEntry(
            new Iri("https://down-peer.test/inbox"),
            new Create { Object = [new Note { Id = "https://me.test/ap/v1/notes/1" }] },
            new Iri("https://me.test/ap/v1/u/alice"),
            3,
            DeadLetterFailureKind.TransportError,
            "Connection refused",
            new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
        var deadLetters = new StubDeadLetterStore(entries: [entry]);
        var queue = new StubDeliveryQueue();

        using var host = BuildDeadLetterHost(deadLetters, queue);
        await host.StartAsync();

        var response = await host.GetTestClient().PostAsync("/local/v1/admin/dead-letters/0/replay", null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        // The replayed job is back on the queue (attempts reset to 0) and the entry was cleared.
        var replayed = await queue.WaitForJobAsync();
        Assert.NotNull(replayed);
        Assert.Equal("https://down-peer.test/inbox", replayed!.InboxIri.Value);
        Assert.Equal(0, replayed.Attempts);
        Assert.Equal(0, deadLetters.Count);
        Assert.Contains("\"success\":true", body);
        Assert.Contains("\"remaining\":0", body);
    }

    [Fact]
    public async Task DeadLetters_ReplayOutOfRange_ReturnsNotFound()
    {
        var deadLetters = new StubDeadLetterStore(entries: []);
        using var host = BuildDeadLetterHost(deadLetters, new StubDeliveryQueue());
        await host.StartAsync();

        var response = await host.GetTestClient().PostAsync("/local/v1/admin/dead-letters/5/replay", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminBootstrap_ReadsCredentialsFromAppAdminEnvVars()
    {
        // S61: the bootstrap admin's credentials are supplied as APP_ADMIN__USERNAME / APP_ADMIN__PASSWORD
        // (the standard .NET double-underscore form of App:Admin:Username / :Password). The host's
        // env->config mapping does not reliably surface the double-underscore form as the App:Admin:Username
        // path key, so the bootstrapper must read the process environment directly (with the IConfiguration
        // keys as a fallback). This test sets ONLY the env vars (no App:Admin:* config keys) and asserts the
        // real AdminBootstrapper still provisions an admin.
        var set = new HashSet<string>();
        set.Add("APP_ADMIN__USERNAME");
        set.Add("APP_ADMIN__PASSWORD");
        var previousUsername = Environment.GetEnvironmentVariable("APP_ADMIN__USERNAME");
        var previousPassword = Environment.GetEnvironmentVariable("APP_ADMIN__PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("APP_ADMIN__USERNAME", "envadmin");
            Environment.SetEnvironmentVariable("APP_ADMIN__PASSWORD", "envpass-12345");

            var store = new RecordingUserAccountStore();
            var bootstrapper = new AdminBootstrapper(
                new ServiceCollection()
                    .AddSingleton<IUserAccountStore>(store)
                    .AddSingleton<IKeyStore>(new InMemoryKeyStore())
                    .AddSingleton<IKeyProvider>(new StubKeyProvider([]))
                    .AddSingleton<ActorProvisioner>(new ActorProvisioner(
                        new StubPersistence([], []),
                        new InMemoryKeyStore(),
                        new StubKeyProvider([]),
                        new Iri("https://me.test")))
                    .AddSingleton<PasswordHasher>(new PasswordHasher())
                    .BuildServiceProvider(),
                new ConfigurationBuilder().Build(), // no App:Admin:* keys -> env vars must drive bootstrap
                new Iri("https://me.test"),
                NullLogger<AdminBootstrapper>.Instance);

            await bootstrapper.StartAsync(CancellationToken.None);

            Assert.Single(store.Created);
            Assert.Equal("envadmin", store.Created[0].Username);
            Assert.Equal(UserRole.Admin, store.Created[0].Role);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_ADMIN__USERNAME", previousUsername);
            Environment.SetEnvironmentVariable("APP_ADMIN__PASSWORD", previousPassword);
        }
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

    private static IHost BuildDeadLetterHost(
        IDeliveryDeadLetterStore deadLetters,
        IDeliveryQueue queue) =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer();
                builder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddControllers();
                    services.AddSingleton(deadLetters);
                    services.AddSingleton(queue);
                    // The dead-letter endpoints carry RequireRole("Admin"). The test host has no iris.auth
                    // cookie, so a permissive result handler always passes authorization — the test
                    // exercises the endpoint logic (list + replay), while role enforcement itself is the
                    // real app's concern (covered by the RequireRole metadata, verified in the live stack).
                    services.AddAuthorization();
                    services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler,
                        PermissiveResultHandler>();
                });
                builder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        WebAppFactory.MapDeadLetterEndpoints(endpoints);
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

        public Task UpdateMessagesReadAtAsync(Guid id, DateTimeOffset readAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> AnyAdminExistsAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);

        public Task UpdateNotificationPrefsAsync(Guid id, NotificationPreferences? prefs, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpdateRoleAsync(Guid id, UserRole role, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingUserAccountStore : IUserAccountStore
    {
        public List<UserAccount> Created { get; } = [];

        public Task<IReadOnlyCollection<UserAccount>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<UserAccount>>([]);

        public Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default) =>
            Task.FromResult<UserAccount?>(null);

        public Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<UserAccount?>(null);

        public Task CreateAsync(UserAccount account, CancellationToken ct = default)
        {
            Created.Add(account);
            return Task.CompletedTask;
        }

        public Task UpdatePasswordHashAsync(Guid id, string newHash, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpdateNotificationsReadAtAsync(Guid id, DateTimeOffset readAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpdateMessagesReadAtAsync(Guid id, DateTimeOffset readAt, CancellationToken ct = default) =>
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
        public IBookmarkStore Bookmarks => throw new NotSupportedException();
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

    private sealed class StubDeadLetterStore(
        IReadOnlyList<DeadLetterEntry>? entries = null,
        int count = 0) : IDeliveryDeadLetterStore
    {
        private List<DeadLetterEntry> _entries = new(entries ?? []);

        public int Count => _entries.Count + count;

        public Task AddAsync(DeadLetterEntry entry, CancellationToken ct = default)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterEntry>>(_entries);

        public Task RemoveAsync(DeadLetterEntry entry, CancellationToken ct = default)
        {
            _entries.RemoveAll(e => e == entry);
            return Task.CompletedTask;
        }
    }

    private sealed class PermissiveResultHandler :
        Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(
            Microsoft.AspNetCore.Http.RequestDelegate next,
            Microsoft.AspNetCore.Http.HttpContext context,
            Microsoft.AspNetCore.Authorization.AuthorizationPolicy policy,
            Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult result)
        {
            // Always allow: the test host has no iris.auth cookie, and the S60 test target is the
            // list/replay endpoint logic (role enforcement is the real app's RequireRole concern,
            // verified on the live stack). Invoke the endpoint pipeline so the handler runs.
            return next(context);
        }
    }

    private sealed class StubDeliveryQueue : IDeliveryQueue
    {
        private readonly Channel<DeliveryJob> _channel = Channel.CreateUnbounded<DeliveryJob>();
        private int _count;

        public int Count => _count;

        public Task EnqueueAsync(DeliveryJob job, CancellationToken ct = default)
        {
            _count++;
            return _channel.Writer.WriteAsync(job, ct).AsTask();
        }

        public async Task<DeliveryJob?> TryDequeueAsync(CancellationToken ct = default)
        {
            _count--;
            DeliveryJob? job = await _channel.Reader.ReadAsync(ct);
            return job;
        }

        public Task CompleteAsync(CancellationToken ct = default)
        {
            _channel.Writer.Complete();
            return Task.CompletedTask;
        }

        public async Task<DeliveryJob?> WaitForJobAsync()
        {
            DeliveryJob? job = await _channel.Reader.ReadAsync(CancellationToken.None);
            return job;
        }
    }
}
