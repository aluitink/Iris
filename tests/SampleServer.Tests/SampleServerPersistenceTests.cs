using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Samples.SampleServer;
using Iris.Server.InMemory;
using Iris.Server.Persistance;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Samples.SampleServer.Tests;

/// <summary>
/// Phase 19.0.1/19.0.2: the sample server's file-backed persistence opt-in and its idempotent seeding.
/// </summary>
/// <remarks>
/// Covers: (a) <c>Iris__PersistenceDirectory</c> unset (the default) keeps the in-memory provider — the
/// pre-existing behavior every other test relies on; (b) with the directory set, the server binds the
/// Phase 16.4 file-backed provider (actors, community, outbox content, and the signing keys all survive
/// a host rebuild over the same directory — a container recreation); (c) the seed is idempotent by IRI —
/// re-running it against a non-empty (file-backed) store reuses the persisted keys, never duplicates the
/// seeded outbox items, and leaves state created after seeding untouched (a post-seed follow survives).
/// </remarks>
public sealed class SampleServerPersistenceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A file lock (an undisposed store handle on another platform) can defer the delete; the
                // temp dir is garbage-collected by the OS.
            }
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "iris-sample-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public async Task SeedSampleData_FileBacked_SurvivesRecreation_WithoutDuplicatesOrRekey()
    {
        var dir = NewTempDir();
        const string baseString = "http://localhost:5000";
        var actorIri = new Iri($"{baseString}/ap/v1/u/alice");
        var aliceKeyIri = new Iri($"{actorIri}#key-1");
        var bobIri = new Iri($"{baseString}/ap/v1/u/bob");
        var bobKeyIri = new Iri($"{bobIri}#key-1");

        // First "boot": seed a fresh file-backed store. The remote stand-in (carla) is opted in via the
        // Iris:Seed:RemoteStandIn switch so the carla-like durability assertion below is exercised.
        var first = new FileBackedPersistenceProvider(dir);
        SampleServer.SeedSampleData(first, baseString, "alice", actorIri, remoteStandIn: true);
        var aliceNoteIri = new Iri($"{actorIri.Value}/notes/1");
        var bobNoteIri = new Iri($"{bobIri.Value}/notes/1");
        var bobReplyIri = new Iri($"{bobIri.Value}/notes/2");
        var carlaLikeIri = new Iri($"http://{SampleServer.RemoteHostName}/ap/v1/u/{SampleServer.CarlaHandle}/likes/1");
        Assert.Equal(2, (await first.Activities.GetOutboxAsync(bobIri)).Count);
        Assert.True(first.Keys.TryGetKey(aliceKeyIri, out _));
        Assert.True(await first.Actors.TryGetActorAsync(actorIri, out var firstAliceDoc));
        var firstAlicePem = firstAliceDoc!.ExtensionData!["publicKey"].GetProperty("publicKeyPem").GetString();
        first.Dispose();

        // Post-seed state made during a prior "turn": a user follow + a user post.
        var between = new FileBackedPersistenceProvider(dir);
        var userFollowTarget = new Iri($"{baseString}/ap/v1/u/user-follow-target");
        await between.Follows.RecordFollowAsync(actorIri, userFollowTarget);
        var userPost = new Note
        {
            Id = $"{actorIri.Value}/notes/user-1",
            Content = ["<p>a post made after seeding</p>"],
        };
        await between.Activities.AddToOutboxAsync(actorIri, userPost);
        between.Dispose();

        // Recreation: rebuild the provider over the same directory and re-run the seed (exactly what a
        // container `down` (no -v) + `up` does). The remote stand-in stays opted in.
        var second = new FileBackedPersistenceProvider(dir);
        SampleServer.SeedSampleData(second, baseString, "alice", actorIri, remoteStandIn: true);

        // Keys are recovered, not re-minted (a signature made before the recreation still verifies).
        Assert.True(second.Keys.TryGetKey(aliceKeyIri, out var recoveredAlice));
        Assert.Equal(firstAlicePem, recoveredAlice!.ExportPublicKeyPem());
        Assert.True(second.Keys.TryGetKey(bobKeyIri, out var recoveredBob));
        Assert.NotNull(recoveredBob);

        // The seeded outbox items are not duplicated by the re-seed…
        var bobOutbox = await second.Activities.GetOutboxAsync(bobIri);
        Assert.Equal(2, bobOutbox.Count);
        // (Set equality: the file-backed store round-trips through a dictionary, so persistence
        // order is not contractually insertion order — but the IRI set is exact.)
        var expected = new HashSet<string> { bobNoteIri.Value, bobReplyIri.Value };
        var actual = bobOutbox.Select(OutboxItemIri).ToHashSet();
        Assert.Equal(expected, actual);

        // …and the state created after seeding survived the recreation untouched.
        var aliceOutbox = await second.Activities.GetOutboxAsync(actorIri);
        Assert.Equal(2, aliceOutbox.Count);
        Assert.Contains(aliceOutbox, item => OutboxItemIri(item) == aliceNoteIri.Value);
        Assert.Contains(aliceOutbox, item => OutboxItemIri(item) == $"{actorIri.Value}/notes/user-1");
        Assert.True(await second.Follows.IsFollowingAsync(actorIri, userFollowTarget));

        // The community and its seeded edges are intact (keyed by IRI — no-op overwrites).
        var communityIri = new Iri($"{baseString}/ap/v1/c/iris");
        Assert.True(await second.Communities.TryGetCommunityAsync(communityIri, out var community));
        Assert.NotNull(community);
        var members = await second.Communities.GetMembersAsync(communityIri);
        Assert.Equal(2, members.Count);

        // The object store serves the seeded notes by IRI (the object view / search read path).
        // (Only the Note objects are seeded into the object store — the Like rides only in carla's
        // outbox; the outbox set assertion above already covers its durability.)
        Assert.True(await second.Objects.TryGetObjectAsync(aliceNoteIri, out _));
        Assert.True(await second.Objects.TryGetObjectAsync(bobNoteIri, out _));

        second.Dispose();
    }

    [Fact]
    public async Task CreateWebHostBuilder_WithPersistenceDirectory_ServesSeededGraphFromVolume()
    {
        var dir = NewTempDir();
        var server = BuildServer(persistenceDirectory: dir);
        var client = server.CreateClient();

        // The seeded actor document is served from the file-backed store over the same directory.
        var actorResponse = await client.GetAsync("/ap/v1/u/alice");
        Assert.Equal(HttpStatusCode.OK, actorResponse.StatusCode);
        var actorDoc = ActivityJson.Deserialize<Actor>(await actorResponse.Content.ReadAsStringAsync());
        Assert.NotNull(actorDoc);
        Assert.Equal("http://localhost:5000/ap/v1/u/alice", actorDoc!.Id);

        // The seeded community document is served too.
        var communityResponse = await client.GetAsync("/ap/v1/c/iris");
        Assert.Equal(HttpStatusCode.OK, communityResponse.StatusCode);

        // The seeded note is fetchable by IRI (the object view reads the object store).
        var noteResponse = await client.GetAsync("/ap/v1/u/alice/notes/1");
        Assert.Equal(HttpStatusCode.OK, noteResponse.StatusCode);
        server.Dispose();
    }

    [Fact]
    public async Task CreateWebHostBuilder_WithoutPersistenceDirectory_StaysInMemory()
    {
        var server = BuildServer(persistenceDirectory: null);
        var client = server.CreateClient();

        // The default (no Iris__PersistenceDirectory) keeps the in-memory provider — the pre-existing
        // behavior the rest of the suite relies on — and still serves the seeded graph.
        var provider = server.Services.GetRequiredService<IPersistenceProvider>();
        Assert.IsType<InMemoryPersistenceProvider>(provider);

        var actorResponse = await client.GetAsync("/ap/v1/u/alice");
        Assert.Equal(HttpStatusCode.OK, actorResponse.StatusCode);
        server.Dispose();
    }

    private static TestServer BuildServer(string? persistenceDirectory)
    {
        var builder = SampleServer.CreateWebHostBuilder();
        if (persistenceDirectory is not null)
        {
            // Per-host configuration (not process environment): an in-process TestServer host must
            // not leak the setting into other tests via environment state.
            builder.UseConfiguration(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Iris:PersistenceDirectory"] = persistenceDirectory,
                })
                .Build());
        }

        return new TestServer(builder);
    }

    /// <summary>
    /// Resolves the IRI of an outbox item (its <c>Id</c> when it is an object, the link's <c>href</c>
    /// otherwise) — the same convention the outbox stores use for removal matching.
    /// </summary>
    private static string OutboxItemIri(IObjectOrLink item)
        => item is IObject obj ? obj.Id! : ((Link)item).Href!.AbsoluteUri;
}
