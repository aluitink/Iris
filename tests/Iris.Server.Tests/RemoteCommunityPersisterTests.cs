using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 135.1 tests: <see cref="RemoteCommunityPersister"/> persists a remote community (Group)
/// document to the durable community store on first encounter, so the communities the instance has
/// interacted with during federation (e.g. a Lemmy community it follows) are served as known content
/// by the cached-actor-by-IRI endpoint (<c>GET /ap/v1/actor?iri=…</c>).
/// </summary>
/// <remarks>
/// A remote community is a <see cref="Group"/> whose IRI is on a different instance than the server's.
/// The persister stores it in the <c>ICommunityStore</c> (a Group cannot go in the <c>IActorStore</c> —
/// it is not an <c>Actor</c>). Local communities (IRI prefix = the instance base) are skipped (they are
/// provisioned by the community creation flow). Persistence is idempotent (a community is stored once).
/// The endpoint test confirms a cached remote community is served as-is (its original remote IRI).
/// </remarks>
public sealed class RemoteCommunityPersisterTests
{
    private const string LocalHost = "a.domain.local";
    private const string RemoteHost = "lemmy.example";
    private const string InstanceBase = $"https://{LocalHost}/ap/v1";
    private const string RemoteCommunityIri = $"https://{RemoteHost}/c/lemmyverse";

    // --- PersistIfNewAsync: a remote community is persisted ---------------------------

    [Fact]
    public async Task PersistIfNew_RemoteCommunity_StoresIt()
    {
        var persistence = new InMemoryPersistenceProvider();
        var persister = new RemoteCommunityPersister(persistence.Communities, new Iri(InstanceBase));
        var community = NewRemoteCommunity("lmm");

        var persisted = await persister.PersistIfNewAsync(community);

        Assert.True(persisted);
        Assert.True(await persistence.Communities.TryGetCommunityAsync(NewRemoteCommunityIri("lmm"), out var stored));
        Assert.Equal(community.Id, stored!.Id);
    }

    // --- PersistIfNewAsync: a local community is skipped (provisioned by creation) ----

    [Fact]
    public async Task PersistIfNew_LocalCommunity_IsSkipped()
    {
        var persistence = new InMemoryPersistenceProvider();
        var persister = new RemoteCommunityPersister(persistence.Communities, new Iri(InstanceBase));
        var localIri = $"{InstanceBase}/c/local-comm";
        var community = new Group { Id = localIri, PreferredUsername = "local-comm", Name = ["local-comm"] };

        var persisted = await persister.PersistIfNewAsync(community);

        Assert.False(persisted);
        Assert.False(await persistence.Communities.TryGetCommunityAsync(new Iri(localIri), out _));
    }

    // --- PersistIfNewAsync: idempotent (a community is stored once) -------------------

    [Fact]
    public async Task PersistIfNew_AlreadyStored_ReturnsFalse()
    {
        var persistence = new InMemoryPersistenceProvider();
        var persister = new RemoteCommunityPersister(persistence.Communities, new Iri(InstanceBase));
        var community = new Group
        {
            Id = $"https://{RemoteHost}/c/dup",
            PreferredUsername = "dup",
            Name = ["dup"],
        };

        Assert.True(await persister.PersistIfNewAsync(community));
        Assert.False(await persister.PersistIfNewAsync(community));
    }

    // --- PersistIfNewAsync: null / no-IRI returns false --------------------------------

    [Fact]
    public async Task PersistIfNew_NullOrNoIri_ReturnsFalse()
    {
        var persistence = new InMemoryPersistenceProvider();
        var persister = new RemoteCommunityPersister(persistence.Communities, new Iri(InstanceBase));

        Assert.False(await persister.PersistIfNewAsync(null));
        Assert.False(await persister.PersistIfNewAsync(new Group { Name = ["no id"] }));
    }

    // --- Endpoint: a cached remote community is served as-is (its original IRI) --------

    [Fact]
    public async Task CachedActorEndpoint_ServesCachedRemoteCommunity()
    {
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPerson(persistence, LocalHost, "alice");
        await persistence.Communities.PutCommunityAsync(new Group
        {
            Id = RemoteCommunityIri,
            PreferredUsername = "lemmyverse",
            Name = ["Lemmyverse"],
            Summary = ["A community on the Lemmyverse."],
        });

        using var server = BuildServer(persistence);
        var http = new HttpClient(server.CreateHandler(), disposeHandler: false);
        var response = await http.GetAsync($"{InstanceBase}/actor?iri={Uri.EscapeDataString(RemoteCommunityIri)}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(RemoteCommunityIri, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Group", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("lemmyverse", doc.RootElement.GetProperty("preferredUsername").GetString());
        Assert.Equal("Lemmyverse", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("A community on the Lemmyverse.", doc.RootElement.GetProperty("summary").GetString());
    }

    // --- Endpoint: a community the instance has never cached 404s ----------------------

    [Fact]
    public async Task CachedActorEndpoint_UnknownCommunity_ReturnsNotFound()
    {
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPerson(persistence, LocalHost, "alice");

        using var server = BuildServer(persistence);
        var http = new HttpClient(server.CreateHandler(), disposeHandler: false);
        var response = await http.GetAsync(
            $"{InstanceBase}/actor?iri={Uri.EscapeDataString($"https://{RemoteHost}/c/never-seen")}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Helpers ----------------------------------------------------------------------

    private static Iri NewRemoteCommunityIri(string name) => new($"https://{RemoteHost}/c/{name}");

    private static Group NewRemoteCommunity(string name) => new()
    {
        Id = NewRemoteCommunityIri(name).Value,
        PreferredUsername = name,
        Name = [name],
    };

    private static TestServer BuildServer(InMemoryPersistenceProvider persistence)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = LocalHost,
            Handle = "alice",
            Persistence = persistence,
        });
}
