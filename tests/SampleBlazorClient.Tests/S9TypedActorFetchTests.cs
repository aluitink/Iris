using Iris.Client;
using Iris.Client.Collections;
using Iris.Core;
using Iris.Core.Collections;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleServer;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 8 (second round, S9) tests: the <c>ActorDetail</c> page now fetches the actor via the **typed**
/// <c>GetActorAsync</c> (closing the API-coverage gap where the dedicated typed-actor method had no UI —
/// the S2 page used the generic <c>GetObjectAsync</c>). These tests reproduce the page's exact load
/// strategy against a real in-process instance: the typed method returns the actor as an <c>Actor</c>
/// (not null, carrying its identity), and — because <c>GetActorAsync</c> is <c>GetObjectAsync</c> cast to
/// <c>Actor</c> — it returns <c>null</c> for an object that is not an actor (the typed path's null contract).
/// </summary>
public sealed class S9TypedActorFetchTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// Hosts a real <see cref="Iris.Server"/> ActivityPub pipeline (in-memory) with <c>alice</c> and
    /// <c>bob</c> seeded at the dial base, and posts a note to alice's outbox so there is a non-actor
    /// object to probe the typed method's null contract. Mirrors <see cref="S3FollowFeedTests.StartHost"/>.
    /// </summary>
    private static TestServer StartHost()
    {
        const string dialBase = "http://localhost";
        var persistence = new InMemoryPersistenceProvider();
        var aliceIri = new Iri($"{dialBase}/ap/v1/u/alice");
        var aliceKeyId = new Iri($"{aliceIri.Value}#key-1");
        var aliceKey = KeyPairGenerator.GenerateRsa(aliceKeyId);
        persistence.Keys.PutKey(aliceKey);
        var alice = new Person
        {
            Id = aliceIri.Value,
            PreferredUsername = "alice",
            Name = ["alice"],
        };
        alice.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["publicKey"] = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id = aliceKeyId.Value,
                owner = aliceIri.Value,
                publicKeyPem = aliceKey.ExportPublicKeyPem(),
            }),
        };
        persistence.ActorStore.PutActorAsync(alice).GetAwaiter().GetResult();

        persistence.ActorStore.PutActorAsync(new Person
        {
            Id = $"{dialBase}/ap/v1/u/bob",
            PreferredUsername = "bob",
            Name = ["bob"],
        }).GetAwaiter().GetResult();

        var builder = new WebHostBuilder()
            .ConfigureLogging(l => { l.ClearProviders(); l.SetMinimumLevel(LogLevel.None); })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri(dialBase);
                    opts.InstanceName = "iris-a";
                    opts.InstanceActorId = aliceIri;
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(persistence.Keys);
                s.AddSingleton<IActorDocumentFetcher>(new PersistenceActorFetcher(persistence));
                s.AddSingleton<IActorCredentialValidator>(new BasicAuthCredentialValidator(
                    (_, username, password) =>
                    {
                        var valid = username == "alice"
                            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                                System.Text.Encoding.UTF8.GetBytes(password),
                                System.Text.Encoding.UTF8.GetBytes(SampleServer.SampleServer.Password));
                        return new ValueTask<bool>(valid);
                    }));
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }

    private sealed class PersistenceActorFetcher(IPersistenceProvider persistence) : IActorDocumentFetcher
    {
        private readonly IPersistenceProvider _persistence = persistence;

        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => await _persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct) ? actor : null;
    }

    private static async Task<(TestServer Server, IActivityPubClient Client, Iri Alice, Iri Bob, Iri Note)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        var client = session.GetClient();
        var alice = new Iri("http://localhost/ap/v1/u/alice");
        var bob = new Iri("http://localhost/ap/v1/u/bob");

        // Post a note to alice's outbox (a non-actor object to probe the typed method's null contract).
        var noteResult = await client.PostNoteAsync(alice, "<p>a note, not an actor</p>");
        Assert.Equal(202, noteResult.StatusCode);
        Iri note = new($"{alice.Value}/notes/1");
        return (server, client, alice, bob, note);
    }

    /// <summary>
    /// The S9 gap-closure: the <c>ActorDetail</c> page's actor load now uses the **typed**
    /// <c>GetActorAsync</c>. For a real actor it returns the actor (not null) carrying its identity — the
    /// same object the generic <c>GetObjectAsync</c> returned, now surfaced through the dedicated typed
    /// method (the plan's §3.1 gap list named <c>GetActorAsync</c> as "no UI").
    /// </summary>
    [Fact]
    public async Task ActorDetail_TypedGetActor_ReturnsActorWithIdentity()
    {
        var (server, client, alice, _, _) = await LogOnAsync();
        using var _ = server;

        // The page's load strategy (S9): the typed method, cast to Actor.
        var actor = await client.GetActorAsync(alice);
        Assert.True(actor is not null, "GetActorAsync must return the actor (not null) for a real actor");
        Assert.Equal(alice.Value, actor!.Id);
        Assert.Equal("alice", actor.PreferredUsername);
    }

    /// <summary>
    /// The typed method's null contract: for an object that is not an actor (a note), <c>GetActorAsync</c>
    /// returns <c>null</c> (it is <c>GetObjectAsync</c> cast to <c>Actor</c>). This is the behavior the
    /// page's "not an actor" branch relies on, and the reason the page keeps a non-null guard.
    /// </summary>
    [Fact]
    public async Task ActorDetail_TypedGetActor_ReturnsNullForNonActor()
    {
        var (server, client, _, _, note) = await LogOnAsync();
        using var _ = server;

        // A note is a content object, not an actor: the typed method returns null.
        var actor = await client.GetActorAsync(note);
        Assert.True(actor is null, "GetActorAsync must return null for a non-actor object (a note)");
    }

    /// <summary>
    /// Confirms the typed method and the generic method agree on the actor document (the page renders the
    /// result through <c>ObjectView</c>, so the typed <c>Actor</c> must be the same object the generic
    /// <c>GetObjectAsync</c> returned — the switch is behavior-preserving).
    /// </summary>
    [Fact]
    public async Task ActorDetail_TypedGetActor_MatchesGenericGetObject()
    {
        var (server, client, alice, _, _) = await LogOnAsync();
        using var _ = server;

        var generic = await client.GetObjectAsync(alice);
        var typed = await client.GetActorAsync(alice);
        Assert.True(generic is not null, "GetObjectAsync must return the actor document");
        Assert.True(typed is not null, "GetActorAsync must return the actor");
        Assert.Equal(generic!.Id, typed!.Id);
        Assert.Equal("alice", typed.PreferredUsername);
    }
}
