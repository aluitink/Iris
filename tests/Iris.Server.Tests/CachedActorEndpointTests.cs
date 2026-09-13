using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 134.1 integration test: the cached-actor-by-IRI endpoint (<c>GET /ap/v1/actor?iri=…</c>)
/// serves the actor document the instance has stored in its database — a local actor or a remote
/// actor the instance cached during federation — so a known actor's profile renders even when its
/// home instance is unreachable or the account has been deactivated (a live fetch would 410).
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts one local person (alice) and one remote actor
/// (remote, on b.domain.local) that the instance cached (its document was persisted to the actor
/// store, as the <c>RemoteActorPersister</c> does on first encounter). The test asserts: a cached
/// local actor is served (200, the stored document); a cached remote actor is served as-is (200, the
/// stored document with its original remote IRI); an actor the instance has never cached 404s; and a
/// missing/malformed <c>?iri</c> 404s. The endpoint is registered before the <c>/{**path}</c> object
/// catch-all, so it wins by routing specificity.
/// </remarks>
[Collection(CollectionName)]
public sealed class CachedActorEndpointTests : IDisposable
{
    public const string CollectionName = "CachedActorEndpointTests";

    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string RemoteHandle = "remote";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly string _base = $"https://{AHost}";

    public CachedActorEndpointTests(CachedActorEndpointSharedHost fixture)
    {
        _server = fixture.Server;
        _http = new HttpClient(fixture.Server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    // --- A cached local actor is served (the stored document) -------------------------

    [Fact]
    public async Task CachedActor_LocalActor_ReturnsStoredDocument()
    {
        var actorIri = $"https://{AHost}/ap/v1/u/{Alice}";
        var response = await _http.GetAsync($"{_base}/ap/v1/actor?iri={Uri.EscapeDataString(actorIri)}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(actorIri, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Person", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(Alice, doc.RootElement.GetProperty("preferredUsername").GetString());
    }

    // --- A cached remote actor is served as-is (its original remote IRI) --------------

    [Fact]
    public async Task CachedActor_RemoteActor_ReturnsStoredDocumentAsIs()
    {
        // The remote actor (on b.domain.local) was cached in the actor store. The endpoint serves the
        // stored document as-is — its id is the remote IRI (not rewritten to the local instance), and
        // it carries the name/summary the remote instance published.
        var remoteIri = $"https://{BHost}/users/{RemoteHandle}";
        var response = await _http.GetAsync($"{_base}/ap/v1/actor?iri={Uri.EscapeDataString(remoteIri)}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(remoteIri, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Person", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(RemoteHandle, doc.RootElement.GetProperty("preferredUsername").GetString());
        Assert.Equal("Remote Person", doc.RootElement.GetProperty("name").GetString());
        // The cached copy carries the remote actor's own summary (served as-is, not sanitized — the
        // client sanitizes on render).
        Assert.Equal("Hello from the remote instance.", doc.RootElement.GetProperty("summary").GetString());
    }

    // --- An actor the instance has never cached 404s ----------------------------------

    [Fact]
    public async Task CachedActor_UnknownActor_ReturnsNotFound()
    {
        var unknownIri = $"https://{BHost}/users/never-seen";
        var response = await _http.GetAsync($"{_base}/ap/v1/actor?iri={Uri.EscapeDataString(unknownIri)}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- A missing / malformed ?iri 404s ----------------------------------------------

    [Fact]
    public async Task CachedActor_MissingIri_ReturnsNotFound()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/actor");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CachedActor_MalformedIri_ReturnsNotFound()
    {
        // "not a uri" fails Iri.TryParse → 404.
        var response = await _http.GetAsync($"{_base}/ap/v1/actor?iri=not%20a%20uri");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Seeds: one local person (alice, on a.domain.local — the instance's own actor) and one remote
    /// person (remote, on b.domain.local) that the instance cached during federation (its document was
    /// persisted to the actor store, as the <see cref="Iris.Server.Security.RemoteActorPersister"/> does
    /// on first encounter). The remote actor's document carries a name + summary (the fields the
    /// profile view renders), so the test can assert they are served as-is.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider persistence)
    {
        TestSeeder.SeedPerson(persistence, AHost, Alice);

        // A remote actor the instance cached: a Person on b.domain.local (a different host than the
        // instance's a.domain.local), stored in the actor store under its own remote IRI.
        persistence.ActorStore.PutActorAsync(new Person
        {
            Id = $"https://{BHost}/users/{RemoteHandle}",
            PreferredUsername = RemoteHandle,
            Name = ["Remote Person"],
            Summary = ["Hello from the remote instance."],
        }).GetAwaiter().GetResult();
    }
}

/// <summary>
/// The collection's shared host: seeds the persistence (a local actor + a cached remote actor) once
/// and starts a single-instance <c>TestServer</c> hosting the real <c>GET /ap/v1/actor</c> endpoint.
/// Registered as the collection's <see cref="SharedHostFixture"/> so it is constructed once per
/// collection (not once per method).
/// </summary>
public sealed class CachedActorEndpointSharedHost : SharedHostFixture
{
    public CachedActorEndpointSharedHost()
        : base(new ActivityPubHostOptions
        {
            Host = "a.domain.local",
            Handle = "alice",
            Persistence = BuildPersistence(),
        })
    {
    }

    private static InMemoryPersistenceProvider BuildPersistence()
    {
        var persistence = new InMemoryPersistenceProvider();
        CachedActorEndpointTests.SeedForFixture(persistence);
        return persistence;
    }
}

/// <summary>
/// Collection definition for <see cref="CachedActorEndpointTests"/>: a single shared host built once
/// per collection (29.3) so the class's read-only methods do not each rebuild the pipeline.
/// </summary>
[CollectionDefinition(CachedActorEndpointTests.CollectionName)]
public sealed class CachedActorEndpointCollection : ICollectionFixture<CachedActorEndpointSharedHost>
{
}
