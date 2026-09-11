using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Iris.Server.Stores;
using Iris.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Identity;

/// <summary>
/// Auto-convergence tests for <see cref="KeyProviderRefreshService"/> (Phase 84.6, shared-state scale-out —
/// the "when" of the refresh). These prove the hosted service converges a running instance to a rotation
/// performed on <em>another</em> instance over the same persistence <strong>without any explicit
/// <c>RefreshFromActorsAsync</c> call</strong> — the production behavior, where no operator (and no test)
/// drives the refresh by hand.
/// </summary>
/// <remarks>
/// The topology is two instances over one origin: instance A (the rotator, its own
/// <see cref="DocumentDerivedKeyProvider"/> + a <see cref="KeyRotationService"/> over the shared store) and
/// instance B (the converger, a <see cref="DocumentDerivedKeyProvider"/> registered as the
/// <c>IKeyProvider</c> a <see cref="KeyProviderRefreshService"/> resolves). A rotates; B's in-process map is
/// untouched by the rotation (the divergence). The hosted service's periodic pass re-derives B's map from
/// the persisted document B shares with A, so B's signer resolves the new key on its own — no restart, no
/// explicit refresh. The test polls B until it converges (bounded by a deadline) rather than sleeping a
/// fixed time, so it is fast and not flaky.
/// </remarks>
public sealed class KeyProviderRefreshServiceAutoConvergenceTests
{
    [Fact]
    public async Task RotateOnInstanceA_InstanceB_ConvergesAutomatically_NoExplicitRefresh()
    {
        // One shared persistence = two instances over one origin.
        var persistence = new InMemoryPersistenceProvider();
        var (_, actorIri, originalKeyId) = TestSeeder.SeedPersonWithKey(persistence, "a.domain.local", "alice");

        // Instance A: its own provider + the rotation service (over the shared store). A's rotation updates
        // the shared key store + the persisted document + A's OWN map.
        var instanceA = new DocumentDerivedKeyProvider(persistence.Keys);
        var rotation = new KeyRotationService(
            persistence,
            persistence.Keys,
            instanceA,
            NullLogger<KeyRotationService>.Instance);

        // Instance B: the provider the hosted service will converge. Registered as the IKeyProvider so the
        // service resolves it. B's map is seeded (refreshed once) so it starts converged at #key-1.
        var instanceB = new DocumentDerivedKeyProvider(persistence.Keys);
        Assert.Equal(1, await instanceB.RefreshFromActorsAsync(persistence.Actors, persistence.Keys));

        // A short refresh interval so the test converges quickly (the service's startup pass + first tick).
        var options = Options.Create(new ActivityPubServerOptions
        {
            KeyProviderRefreshInterval = TimeSpan.FromMilliseconds(50),
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPersistenceProvider>(persistence);
        services.AddSingleton<IKeyProvider>(instanceB);
        services.AddSingleton(options);
        services.AddSingleton<KeyProviderRefreshService>();
        var provider = services.BuildServiceProvider();
        var refreshService = provider.GetRequiredService<KeyProviderRefreshService>();

        // Baseline: B resolves the seeded key (converged at #key-1) before the rotation.
        Assert.True(instanceB.TryGetIdentity(actorIri, out var bBefore));
        Assert.Equal(originalKeyId, bBefore!.KeyId);

        // Start the hosted service (its startup pass runs, then it ticks every 50 ms).
        await refreshService.StartAsync(CancellationToken.None);

        try
        {
            // Instance A rotates: mints #key-2, stores it in the SHARED key store, re-stamps the document,
            // re-binds A's OWN map. B's map is untouched (per-instance) — the divergence.
            var newKeyId = await rotation.RotateAsync(actorIri);
            Assert.NotEqual(originalKeyId, newKeyId);
            Assert.True(instanceA.TryGetIdentity(actorIri, out var aAfter));
            Assert.Equal(newKeyId, aAfter!.KeyId);

            // Auto-convergence: poll B until its signer resolves the new key. The hosted service's periodic
            // pass (NOT an explicit RefreshFromActorsAsync call here) is what re-derives B's map from the
            // persisted document. A bounded deadline keeps the test fast + not flaky.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            IIdentity? bIdentity = null;
            while (DateTime.UtcNow < deadline)
            {
                if (instanceB.TryGetIdentity(actorIri, out bIdentity)
                    && bIdentity!.KeyId == newKeyId)
                {
                    break;
                }

                await Task.Delay(25);
            }

            // B converged to the new key on its own — no restart, no explicit refresh.
            Assert.True(instanceB.TryGetIdentity(actorIri, out var bConverged),
                "instance B did not converge to the rotated key within the deadline");
            Assert.Equal(newKeyId, bConverged!.KeyId);
        }
        finally
        {
            await refreshService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Inert_WhenProviderIsNotDocumentDerived_NoRefreshRuns()
    {
        // The service is a no-op when the instance's IKeyProvider is not a DocumentDerivedKeyProvider (the
        // single-instance default). It does not throw, does not touch the provider, and a rotation on the
        // shared store is NOT pulled into the (in-memory) provider — the common single-instance deployment
        // is unaffected by registering the service.
        var persistence = new InMemoryPersistenceProvider();
        var (_, actorIri, originalKeyId) = TestSeeder.SeedPersonWithKey(persistence, "a.domain.local", "alice");

        // The default in-memory provider: it does NOT auto-derive from the documents.
        var inMemory = new InMemoryKeyProvider(persistence.Keys);
        inMemory.RegisterKey(actorIri, originalKeyId);

        var options = Options.Create(new ActivityPubServerOptions
        {
            KeyProviderRefreshInterval = TimeSpan.FromMilliseconds(20),
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPersistenceProvider>(persistence);
        services.AddSingleton<IKeyProvider>(inMemory);
        services.AddSingleton(options);
        services.AddSingleton<KeyProviderRefreshService>();
        var provider = services.BuildServiceProvider();
        var refreshService = provider.GetRequiredService<KeyProviderRefreshService>();

        await refreshService.StartAsync(CancellationToken.None);
        try
        {
            // Give the service a couple of ticks (its refresh is a no-op, but confirm it doesn't throw).
            await Task.Delay(100);

            // Rotate the shared store (via a separate document-derived provider for A). The in-memory
            // provider is NOT auto-converged — it still resolves the original key (its map was set once and
            // the service never touched it).
            var instanceA = new DocumentDerivedKeyProvider(persistence.Keys);
            var rotation = new KeyRotationService(
                persistence,
                persistence.Keys,
                instanceA,
                NullLogger<KeyRotationService>.Instance);
            var newKeyId = await rotation.RotateAsync(actorIri);
            Assert.NotEqual(originalKeyId, newKeyId);

            // The in-memory provider still resolves the ORIGINAL key (the service is inert for it).
            Assert.True(inMemory.TryGetIdentity(actorIri, out var identity));
            Assert.Equal(originalKeyId, identity!.KeyId);
        }
        finally
        {
            await refreshService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NonPositiveInterval_DisablesPeriodicRefresh_ButStartupPassStillConverges()
    {
        // A non-positive interval disables the periodic tick, but the startup convergence pass still runs —
        // a host that registers a fresh (empty) DocumentDerivedKeyProvider with the periodic refresh off
        // still converges to the seeded actors before it signs.
        var persistence = new InMemoryPersistenceProvider();
        var (_, actorIri, keyId) = TestSeeder.SeedPersonWithKey(persistence, "a.domain.local", "alice");

        // A FRESH (empty) provider — not pre-seeded. Only the service's startup pass can populate it.
        var provider = new DocumentDerivedKeyProvider(persistence.Keys);
        Assert.False(provider.TryGetIdentity(actorIri, out _));

        var options = Options.Create(new ActivityPubServerOptions
        {
            KeyProviderRefreshInterval = TimeSpan.Zero,
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPersistenceProvider>(persistence);
        services.AddSingleton<IKeyProvider>(provider);
        services.AddSingleton(options);
        services.AddSingleton<KeyProviderRefreshService>();
        var sp = services.BuildServiceProvider();
        var refreshService = sp.GetRequiredService<KeyProviderRefreshService>();

        await refreshService.StartAsync(CancellationToken.None);
        try
        {
            // The startup convergence pass is async (BackgroundService.StartAsync kicks off ExecuteAsync
            // without awaiting it), so poll for the fresh provider to converge — bounded, like the other
            // tests. The periodic loop did NOT run (the interval is zero), so the only thing that can
            // populate the empty provider is the startup pass.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (provider.TryGetIdentity(actorIri, out var candidate) && candidate!.KeyId == keyId)
                {
                    break;
                }

                await Task.Delay(25);
            }

            Assert.True(provider.TryGetIdentity(actorIri, out var identity),
                "the fresh provider did not converge on the startup pass within the deadline");
            Assert.Equal(keyId, identity!.KeyId);
        }
        finally
        {
            await refreshService.StopAsync(CancellationToken.None);
        }
    }
}
