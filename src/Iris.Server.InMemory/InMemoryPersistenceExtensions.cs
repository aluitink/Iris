using Iris.Core;
using Iris.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iris.Server.InMemory;

/// <summary>
/// Extension methods that register the in-memory persistence provider for an Iris server.
/// </summary>
/// <remarks>
/// Call <see cref="AddInMemoryPersistence(IServiceCollection)"/> after
/// <see cref="ActivityPubServerExtensions.AddActivityPubServer(IServiceCollection)"/> to bind the
/// <see cref="IPersistenceProvider"/> seam to the in-memory implementation. A host app that wants a
/// real database replaces this registration.
/// </remarks>
public static class InMemoryPersistenceExtensions
{
    /// <summary>
    /// Registers the in-memory <see cref="IPersistenceProvider"/> and the individual in-memory stores.
    /// </summary>
    /// <param name="services">The service collection. Must not be null.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddInMemoryPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<InMemoryActorStore>();
        services.TryAddSingleton<InMemoryActivityStore>();
        services.TryAddSingleton<InMemoryFollowStore>();
        services.TryAddSingleton<InMemoryLikeStore>();
        services.TryAddSingleton<InMemoryReplyStore>();
        services.TryAddSingleton<InMemoryModerationStore>();
        services.TryAddSingleton<InMemoryRelayStore>();
        services.TryAddSingleton<InMemoryObjectStore>();
        services.TryAddSingleton<InMemoryCreateIndex>();
        services.TryAddSingleton<InMemoryCommunityStore>();
        services.TryAddSingleton<InMemoryMediaStore>();

        // Ensure a key store is registered (the local actor's signing keys).
        services.TryAddSingleton<IKeyStore, InMemoryKeyStore>();

        // The aggregate provider, built from the individual stores.
        services.TryAddSingleton<IPersistenceProvider>(sp => new InMemoryPersistenceProvider(
            sp.GetRequiredService<InMemoryActorStore>(),
            sp.GetRequiredService<InMemoryActivityStore>(),
            sp.GetRequiredService<InMemoryFollowStore>(),
            sp.GetRequiredService<InMemoryLikeStore>(),
            sp.GetRequiredService<InMemoryReplyStore>(),
            sp.GetRequiredService<InMemoryModerationStore>(),
            sp.GetRequiredService<InMemoryRelayStore>(),
            sp.GetRequiredService<InMemoryObjectStore>(),
            sp.GetRequiredService<InMemoryCreateIndex>(),
            sp.GetRequiredService<InMemoryCommunityStore>(),
            sp.GetRequiredService<IKeyStore>(),
            sp.GetRequiredService<InMemoryMediaStore>()));

        return services;
    }
}
