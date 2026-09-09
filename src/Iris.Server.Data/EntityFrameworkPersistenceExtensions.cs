using Iris.Server;
using Iris.Server.Data.Accounts;
using Iris.Server.Data.Stores;
using Iris.Server.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iris.Server.Data;

/// <summary>
/// Extension methods that register the EF Core (PostgreSQL) persistence provider for an Iris server.
/// </summary>
/// <remarks>
/// Call <see cref="AddEntityFrameworkPersistence(IServiceCollection, IConfiguration)"/> after
/// <see cref="Iris.Server.ActivityPubServerExtensions.AddActivityPubServer(IServiceCollection)"/> to
/// bind the <see cref="IPersistenceProvider"/> seam to the EF Core implementation. A host app that
/// wants the in-memory provider instead calls <c>AddInMemoryPersistence()</c>. The connection string
/// comes from the <c>Iris:ConnectionString</c> configuration key.
/// </remarks>
public static class EntityFrameworkPersistenceExtensions
{
    /// <summary>
    /// The configuration section that holds the connection string and the media blob directory.
    /// </summary>
    public const string SectionName = "Iris";

    /// <summary>
    /// Registers the EF Core (PostgreSQL) <see cref="IPersistenceProvider"/> and the individual stores.
    /// </summary>
    /// <param name="services">The service collection. Must not be null.</param>
    /// <param name="configuration">The configuration that supplies the connection string and blob dir. Must not be null.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
    public static IServiceCollection AddEntityFrameworkPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Iris")
            ?? configuration[SectionName + ":ConnectionString"]
            ?? throw new InvalidOperationException(
                $"A connection string is required. Set '{SectionName}:ConnectionString' or a connection string named 'Iris'.");

        var blobDir = configuration[SectionName + ":MediaBlobDir"] ?? Path.Combine(AppContext.BaseDirectory, "media-blobs");
        Directory.CreateDirectory(blobDir);

        // The shared context factory (one per app; the stores share it).
        services.TryAddSingleton<IDbContextFactory<IrisDbContext>>(sp => new IrisDbContextFactory(connectionString));

        // The shared edge store (every relationship store reads/writes the same Edges table through it).
        services.TryAddSingleton<EdgeStore>(sp => new EdgeStore(sp.GetRequiredService<IDbContextFactory<IrisDbContext>>()));

        // The individual stores.
        services.TryAddSingleton<EfActorStore>();
        services.TryAddSingleton<EfActivityStore>();
        services.TryAddSingleton<EfFollowStore>();
        services.TryAddSingleton<EfLikeStore>();
        services.TryAddSingleton<EfAnnounceStore>();
        services.TryAddSingleton<EfReplyStore>();
        services.TryAddSingleton<EfModerationStore>();
        services.TryAddSingleton<EfRelayStore>();
        services.TryAddSingleton<EfObjectStore>();
        services.TryAddSingleton<EfCreateIndex>();
        services.TryAddSingleton<EfKeyStore>();
        services.TryAddSingleton<EfCommunityStore>();
        services.TryAddSingleton<EfMediaStore>(sp => new EfMediaStore(
            sp.GetRequiredService<IDbContextFactory<IrisDbContext>>(),
            sp.GetRequiredService<EdgeStore>(),
            blobDir));

        // The local browser-session account store (EF Core).
        services.TryAddSingleton<IUserAccountStore, EfUserAccountStore>();

        // The instance metadata store (EF Core).
        services.TryAddSingleton<IInstanceMetadataStore, EfInstanceMetadataStore>();

        // Instance stats (55.1): real user count for NodeInfo's usage.users.total. Hard AddSingleton
        // (not TryAdd) to override the in-memory default registered by AddActivityPubServer.
        services.AddSingleton<IInstanceStatsProvider>(sp =>
            new EfInstanceStatsProvider(sp.GetRequiredService<IUserAccountStore>()));

        // The aggregate provider. This MUST be a hard AddSingleton (not TryAddSingleton):
        // AddActivityPubServer registers a recursive fallback factory for IPersistenceProvider
        // (sp => sp.GetRequiredService<IPersistenceProvider>()) via TryAddSingleton, which becomes the
        // first binding. A TryAddSingleton here would be a no-op (the service is already registered by
        // that fallback), leaving the recursive factory as the sole binding — resolving it then recurses
        // infinitely (StackGuard deadlock) the first time anything resolves IPersistenceProvider (e.g. the
        // startup migration / seed). A hard AddSingleton adds a second binding; for singletons the LAST
        // registration wins, so this factory overrides the fallback and resolves cleanly. (The in-memory
        // path is safe because WebAppFactory binds it with a hard AddSingleton too.)
        services.AddSingleton<IPersistenceProvider>(sp => new EntityFrameworkPersistenceProvider(
            sp.GetRequiredService<EfActorStore>(),
            sp.GetRequiredService<EfActivityStore>(),
            sp.GetRequiredService<EfFollowStore>(),
            sp.GetRequiredService<EfLikeStore>(),
            sp.GetRequiredService<EfReplyStore>(),
            sp.GetRequiredService<EfAnnounceStore>(),
            sp.GetRequiredService<EfModerationStore>(),
            sp.GetRequiredService<EfRelayStore>(),
            sp.GetRequiredService<EfObjectStore>(),
            sp.GetRequiredService<EfCreateIndex>(),
            sp.GetRequiredService<EfCommunityStore>(),
            sp.GetRequiredService<EfKeyStore>(),
            sp.GetRequiredService<EfMediaStore>(),
            sp.GetRequiredService<EdgeStore>()));

        return services;
    }

    /// <summary>
    /// Ensures the database schema is created (or migrated) before the server starts serving. Call
    /// once at startup, after building the host, to <c>Migrate()</c> the <see cref="IrisDbContext"/>.
    /// </summary>
    /// <param name="provider">The registered provider. Must not be null.</param>
    /// <param name="configuration">The configuration that supplies the connection string. Must not be null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the schema is ready.</returns>
    public static async Task EnsureCreatedAsync(this IPersistenceProvider provider, IConfiguration configuration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Iris") ?? configuration[SectionName + ":ConnectionString"];
        if (string.IsNullOrEmpty(connectionString))
        {
            return;
        }

        await using var db = new IrisDbContext(new DbContextOptionsBuilder<IrisDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
    }
}
