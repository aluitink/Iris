using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 4 integration test for the <strong>outbound account-resolution</strong> slice: the
/// <see cref="IAccountResolver"/> (default <see cref="WebFingerAccountResolver"/>) resolves a remote
/// account (<c>bob@host</c>) to its actor IRI via WebFinger <em>over the wire</em>, reading through the
/// Phase 3 <see cref="WebFingerCache"/> so the account is resolved once and reused.
/// </summary>
/// <remarks>
/// Topology: instance B (b.domain.local, bob) hosts a real WebFinger endpoint. The resolver's
/// <see cref="WebFingerClient"/> is backed by an <c>HttpClient</c> whose transport is B's
/// <c>TestServer</c> handler (in-process, mirroring the federation test's <c>BuildFetcherFor</c>), so
/// the WebFinger request goes out over the real HTTP stack to B. A single resolver (sharing one
/// <see cref="WebFingerCache"/>) is reused across the lookup assertions, so the cache is observable.
/// </remarks>
public sealed class AccountResolverIntegrationTests : IDisposable
{
    private const string BHost = "b.domain.local";
    private const string Bob = "bob";

    private readonly TestServer _b;
    private readonly IAccountResolver _resolver;
    private readonly Iri _bobActorIri;

    public AccountResolverIntegrationTests()
    {
        var persistence = new InMemoryPersistenceProvider();
        Seed(persistence, BHost, Bob);
        _b = StartServer(BHost, Bob, persistence);
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");

        // A WebFingerClient whose transport routes in-process to B (no signing, no content negotiation —
        // WebFinger is not ActivityPub). The resolver reads through a shared WebFingerCache.
        var webFinger = new WebFingerClient(new HttpClient(_b.CreateHandler(), disposeHandler: false));
        _resolver = new WebFingerAccountResolver(webFinger, new WebFingerCache());
    }

    public void Dispose()
    {
        _b.Dispose();
    }

    // --- The resolver resolves a remote account to its actor IRI over the wire ----------

    [Fact]
    public async Task Resolve_RemoteAccount_ResolvesOverWire_AndCaches()
    {
        // First lookup: a miss — the resolver hits B's WebFinger endpoint over the wire and caches the
        // resolution in the shared WebFingerCache.
        var first = await _resolver.ResolveAsync($"{Bob}@{BHost}");
        Assert.Equal(_bobActorIri, first);

        // Second lookup: served from the cache (same resolver instance → shared cache).
        var second = await _resolver.ResolveAsync($"{Bob}@{BHost}");
        Assert.Equal(first, second);
    }

    // --- A bypassCache bypasses the cache and re-fetches over the wire -----------------

    [Fact]
    public async Task Resolve_ForceRefresh_ReFetchesOverWire()
    {
        var first = await _resolver.ResolveAsync($"{Bob}@{BHost}");
        var refreshed = await _resolver.ResolveAsync($"{Bob}@{BHost}", bypassCache: true);

        // Both resolve to the same actor IRI (the endpoint is stable), but the forced refresh hit the
        // network again (the cache was bypassed for the read).
        Assert.Equal(_bobActorIri, first);
        Assert.Equal(first, refreshed);
    }

    // --- An unknown account resolves to null (and is not cached) ------------------------

    [Fact]
    public async Task Resolve_UnknownAccount_ReturnsNull()
    {
        // B has no actor "nobody", so its WebFinger endpoint returns 404 → the resolution is absent.
        var result = await _resolver.ResolveAsync($"nobody@{BHost}");
        Assert.Null(result);
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// Seeds a persistence provider with a single actor (Person) + a real EC key, carrying the real
    /// JWK in the <c>publicKey</c> extension (mirrors the delivery/federation tests).
    /// </summary>
    private static void Seed(InMemoryPersistenceProvider persistence, string host, string handle)
    {
        var actorIriString = $"https://{host}/ap/v1/u/{handle}";
        var actorIri = new Iri(actorIriString);
        var keyId = new Iri($"{actorIriString}#key-1");

        var key = KeyPairGenerator.GenerateEcP256(keyId);
        persistence.Keys.PutKey(key);

        var actor = new Person
        {
            Id = actorIriString,
            PreferredUsername = handle,
            Name = [handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = actorIriString,
            kty = "EC",
            crv = "P-256",
            x = ExtractJwkComponent(key, "x"),
            y = ExtractJwkComponent(key, "y"),
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
    }

    private static string ExtractJwkComponent(KeyPair key, string name)
    {
        using var doc = JsonDocument.Parse(key.GetPublicJwk());
        return doc.RootElement.GetProperty(name).GetString()!;
    }

    /// <summary>
    /// Starts a single-instance <c>TestServer</c> (host/handle/persistence) that hosts the real
    /// WebFinger endpoint the resolver queries over the wire.
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence)
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
                    opts.BaseUri = new Iri($"https://{host}");
                    opts.InstanceName = $"iris-{host}";
                    opts.InstanceActorId = new Iri($"https://{host}/ap/v1/u/{handle}");
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }
}
