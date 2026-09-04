using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Testing;

/// <summary>
/// Options for <see cref="ActivityPubHostFactory.Create"/>: the configuration surface shared by every
/// integration test's server bootstrap. The factory is the single place that wires the real ActivityPub
/// pipeline (the production gap the earlier scaffold harness left open), so the ~12 per-test
/// <c>StartServer</c> copies collapse into one tested implementation.
/// </summary>
public sealed class ActivityPubHostOptions
{
    /// <summary>The instance hostname (e.g. <c>a.domain.local</c>).</summary>
    public required string Host { get; init; }

    /// <summary>The local actor's handle (e.g. <c>alice</c>); the instance actor IRI is
    /// <c>https://{Host}/ap/v1/u/{Handle}</c>.</summary>
    public required string Handle { get; init; }

    /// <summary>The persistence provider whose keys/persistence the host binds (its <c>Keys</c> becomes
    /// the host's <see cref="IKeyStore"/> seam).</summary>
    public required InMemoryPersistenceProvider Persistence { get; init; }

    /// <summary>Override the inbound actor-document fetcher (federation wiring: route to the other
    /// instance's <c>TestServer</c>). Defaults to the production <c>HttpClientHandler</c> fetcher.</summary>
    public IActorDocumentFetcher? Fetcher { get; init; }

    /// <summary>Override the outbound delivery transport (federation wiring: route the <c>DeliveryWorker</c>
    /// to the other instance's <see cref="TestServer"/>). Defaults to a real <see cref="HttpClientHandler"/>.</summary>
    public Func<HttpMessageHandler>? DeliveryTransport { get; init; }

    /// <summary>Override the outbound object fetcher (the <see cref="IActivityPubClient"/> the outbox
    /// publish handler uses to resolve a remote object's owner for a Like / Announce delivery, 24.1).
    /// When set, it is registered after the host's default (so it wins for <c>GetService</c>); when null,
    /// the host's default (a real <see cref="HttpClientHandler"/> client, or none) is used.</summary>
    public IActivityPubClient? Client { get; init; }

    /// <summary>Override the Basic-auth credential validator (the owner-only actor-doc + proxy gate).</summary>
    public IActorCredentialValidator? CredentialValidator { get; init; }

    /// <summary>Proxy settings (allowlist + rate limit) for the gated proxy endpoint.</summary>
    public ProxySettings? ProxySettings { get; init; }

    /// <summary>
    /// The instance's shared inbox IRI (F-01) to advertise on local actor/community documents and (when
    /// set) route local deliveries to. When null, the instance advertises no <c>endpoints.sharedInbox</c>.
    /// </summary>
    public Iri? SharedInboxIri { get; init; }

    /// <summary>
    /// Additional local-actor keys to register with the host's <see cref="IKeyProvider"/> (so the outbound
    /// <c>DeliveryWorker</c> can sign as a second local identity). Each IRI's key is resolved by the
    /// <c>#key-1</c> convention (matching <see cref="TestSeeder"/>).
    /// </summary>
    public IEnumerable<Iri>? ExtraLocalActors { get; init; }

    /// <summary>A community key (a group actor as a follower) to register with the host's
    /// <see cref="IKeyProvider"/> so the outbound <c>DeliveryWorker</c> can sign as the community.</summary>
    public KeyPair? CommunityKey { get; init; }

    /// <summary>Optional additional service registrations (escape hatch for test-specific seams).</summary>
    public Action<IServiceCollection>? ExtraServices { get; init; }

    /// <summary>
    /// Whether to register the local actor's key (and any <see cref="ExtraLocalActors"/> /
    /// <see cref="CommunityKey"/>) with the host's <see cref="IKeyProvider"/> so the outbound
    /// <c>DeliveryWorker</c> can sign. Defaults to <c>true</c>. A test whose server performs no outbound
    /// delivery may set this to <c>false</c> (its earlier private <c>StartServer</c> skipped the
    /// registration).
    /// </summary>
    public bool RegisterLocalKey { get; init; } = true;

    /// <summary>
    /// A test-built identity (key store + provider + signer) to register with the host in place of the
    /// default <see cref="IKeyStore"/>/signing seam. Used by the few tests that need a custom signer or a
    /// specific key store (e.g. signing outbound deliveries as a community). When set,
    /// <see cref="CommunityKey"/> is also honored (its <see cref="IKeyProvider.RegisterKey(Iri, Iri)"/>
    /// registration is still applied).
    /// </summary>
    public IdentityKeys? IdentityKeys { get; init; }
}

/// <summary>
/// A test-built signing identity: the <see cref="IKeyStore"/> to bind as the host's key seam, the
/// <see cref="IKeyProvider"/> (already populated with the actor key registrations), and the
/// <see cref="ISignatureSigner"/> to use for outbound deliveries.
/// </summary>
public sealed record IdentityKeys(IKeyStore Store, IKeyProvider Provider, ISignatureSigner Signer);

/// <summary>
/// The single shared bootstrap for the real ActivityPub server pipeline used by integration tests.
/// Replaces the ~12 near-identical per-test <c>StartServer</c> helpers (the "harness bridge" the Phase 10
/// test audit flagged): each now calls <see cref="Create"/> with an <see cref="ActivityPubHostOptions"/>
/// instead of re-wiring <c>AddActivityPubServer</c>/<c>AddInMemoryPersistence</c>/
/// <c>UseSignatureValidation</c>/<c>MapActivityPubEndpoints</c> by hand.
/// </summary>
public static class ActivityPubHostFactory
{
    /// <summary>
    /// Builds and starts a real in-process <see cref="TestServer"/> hosting the full ActivityPub pipeline.
    /// The host's <see cref="IKeyStore"/> is bound to <see cref="ActivityPubHostOptions.Persistence"/>.Keys,
    /// and the local actor's key is registered with the host's <see cref="IKeyProvider"/> (plus any
    /// <see cref="ActivityPubHostOptions.ExtraLocalActors"/> / <see cref="ActivityPubHostOptions.CommunityKey"/>).
    /// </summary>
    public static TestServer Create(ActivityPubHostOptions options)
    {
        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri($"https://{options.Host}");
                    opts.InstanceName = $"iris-{options.Host}";
                    opts.InstanceActorId = new Iri($"https://{options.Host}/ap/v1/u/{options.Handle}");
                    if (options.ProxySettings is not null)
                    {
                        opts.ProxySettings = options.ProxySettings;
                    }

                    if (options.SharedInboxIri is not null)
                    {
                        opts.SharedInboxIri = options.SharedInboxIri;
                    }
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(options.Persistence);

                // Bind the IKeyStore seam (and, when provided, a test-built provider/signer) so the
                // outbound DeliveryWorker can sign. AddInMemoryPersistence otherwise registers a fresh,
                // empty InMemoryKeyStore; a custom IdentityKeys overrides the whole identity.
                if (options.IdentityKeys is { } identity)
                {
                    s.AddSingleton<IKeyStore>(identity.Store);
                    s.AddSingleton<IKeyProvider>(identity.Provider);
                    s.AddSingleton<ISignatureSigner>(identity.Signer);
                }
                else
                {
                    s.AddSingleton<IKeyStore>(options.Persistence.Keys);
                }

                if (options.Fetcher is not null)
                {
                    s.AddSingleton<IActorDocumentFetcher>(options.Fetcher);
                }

                // 24.1: override the outbound object fetcher (the outbox handler's IActivityPubClient? param)
                // so a test can route A's remote-object fetch to B's TestServer. Added after AddActivityPubServer
                // (which TryAddSingletons the default), so this registration wins for GetService<IActivityPubClient>.
                if (options.Client is not null)
                {
                    s.AddSingleton<IActivityPubClient>(options.Client);
                }

                if (options.DeliveryTransport is { } transport)
                {
                    s.AddSingleton<Func<HttpMessageHandler>>(() => transport());
                }

                if (options.CredentialValidator is not null)
                {
                    s.AddSingleton<IActorCredentialValidator>(options.CredentialValidator);
                }

                options.ExtraServices?.Invoke(s);
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        var server = new TestServer(builder);

        if (options.RegisterLocalKey && options.IdentityKeys is null)
        {
            // Register the local actor's key (and any extra local actors / the community) with the host's
            // IKeyProvider so the outbound DeliveryWorker can sign as them. The key IRI is the actor's
            // publicKey.id (the #key-1 convention used by TestSeeder).
            var keyProvider = server.Services.GetRequiredService<IKeyProvider>();
            var actorIri = new Iri($"https://{options.Host}/ap/v1/u/{options.Handle}");
            keyProvider.RegisterKey(actorIri, new Iri($"{actorIri}#key-1"));

            if (options.ExtraLocalActors is not null)
            {
                foreach (var extraActor in options.ExtraLocalActors)
                {
                    keyProvider.RegisterKey(extraActor, new Iri($"{extraActor}#key-1"));
                }
            }

            if (options.CommunityKey is not null)
            {
                keyProvider.RegisterKey(new Iri($"https://{options.Host}/ap/v1/c/{options.Handle}"), options.CommunityKey.KeyId);
            }
        }

        return server;
    }
}
