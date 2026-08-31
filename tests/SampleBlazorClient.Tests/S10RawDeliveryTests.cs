using Iris.Client;
using Iris.Core;
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
/// Phase 8 (second round, S10) tests: the <c>Deliver</c> page drives <c>DeliverAsync</c> — the raw
/// signed-activity escape hatch — directly. Every high-level write (follow, like, moderation, compose)
/// routes through <c>DeliverAsync</c> internally; this screen exercises it standalone by building a
/// <c>Follow</c> activity and POSTing it to the target's inbox. These tests reproduce the page's exact
/// strategy against a real in-process instance: <c>DeliverAsync</c> to a local actor's inbox is
/// signature-validated, recorded by the inbox processor (the follow edge is recorded — the target's
/// followers gain the sender), and returns <c>202 Accepted</c>; a delivery to a non-actor inbox returns
/// <c>404</c>.
/// </summary>
public sealed class S10RawDeliveryTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// Hosts a real <see cref="Iris.Server"/> ActivityPub pipeline (in-memory) with <c>alice</c> and
    /// <c>bob</c> seeded at the dial base. Mirrors <see cref="S9TypedActorFetchTests.StartHost"/>.
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

    private static async Task<(TestServer Server, IActivityPubClient Client, Iri Alice, Iri Bob)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        return (server, session.GetClient(),
            new Iri("http://localhost/ap/v1/u/alice"),
            new Iri("http://localhost/ap/v1/u/bob"));
    }

    /// <summary>
    /// The F-1911-3 regression: the Raw delivery screen's "act as" override signs a delivery as the
    /// instance's seeded community via the <c>X-Iris-Actor</c> header override. The client session
    /// registers the community's signing identity at logon (the community reuses the actor's key), so
    /// the override resolves — before the fix the session only registered the logged-on actor's
    /// identity and the override dead-lettered with "No signing identity registered for actor". The
    /// community signs the Follow with the actor's key (its publicKey extension points at it); the
    /// server's signature validation accepts it (the key is the registered key) and records the edge
    /// from the community (the <c>X-Iris-Actor</c> override is the activity's actor).
    /// </summary>
    [Fact]
    public async Task Deliver_ActAsCommunity_SignsAndIsAccepted()
    {
        var (server, client, alice, bob) = await LogOnAsync();
        using var _ = server;

        // The Raw delivery screen's "act as" override: the community IRI (derived from the actor's
        // host) is passed as the X-Iris-Actor header override so the pipeline signs as the community.
        var community = new Iri("http://localhost/ap/v1/c/iris");
        var follow = BuildFollow(community, bob);
        var inbox = bob.InboxOf();
        var request = new HttpRequestMessage(HttpMethod.Post, inbox.Uri)
        {
            Content = new StringContent(
                Iris.Core.ActivityJson.Serialize(follow),
                System.Text.Encoding.UTF8,
                "application/activity+json"),
        };
        request.Headers.Add("X-Iris-Actor", community.Value);

        // Before the fix this threw KeyNotFoundException ("No signing identity registered for actor").
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode,
            $"act-as-community delivery must be accepted (got {response.StatusCode})");
        Assert.Equal(202, (int)response.StatusCode);

        // The follow edge is recorded from the community (the X-Iris-Actor override is the actor):
        // bob's followers now include the community IRI.
        var followers = await FollowerIrisAsync(client, bob);
        Assert.Contains(community.Value, followers);
    }

    /// <summary>
    /// Builds the <c>Follow</c> activity the page builds (actor = the sender, object = the target, a
    /// deterministic unique-per-(actor,target) IRI) — the exact payload <c>DeliverAsync</c> signs + POSTs.
    /// </summary>
    private static Follow BuildFollow(Iri actor, Iri target) => new()
    {
        Id = $"{actor.Value}/follows/{target.Value}",
        Actor = [new Link { Href = actor.Uri }],
        Object = [new Link { Href = target.Uri }],
    };

    /// <summary>
    /// Reads an actor's followers (the <c>followers</c> collection) so a test can assert the follow edge a
    /// delivered <c>Follow</c> recorded on the target.
    /// </summary>
    private static async Task<IReadOnlyList<string>> FollowerIrisAsync(IActivityPubClient client, Iri actor)
    {
        var iris = new List<string>();
        var followersIri = new Iri($"{actor.Value}/followers");
        var obj = await client.GetObjectAsync(followersIri);
        if (obj is OrderedCollection { Items: { } items })
        {
            foreach (var item in items)
            {
                if (item is ILink { Href: { } href })
                {
                    iris.Add(href.ToString());
                }
                else if (item is IObject { Id: { } id })
                {
                    iris.Add(id);
                }
            }
        }

        return iris;
    }

    /// <summary>
    /// The S10 core: <c>DeliverAsync</c> — the raw escape hatch — POSTs a signed <c>Follow</c> to the
    /// target's inbox. The server signature-validates it, records the follow edge (the target's followers
    /// gain the sender), and returns <c>202 Accepted</c>. This is the method every high-level write routes
    /// through, exercised standalone (the plan's §3.1 gap list named it "the escape hatch is unused").
    /// </summary>
    [Fact]
    public async Task Deliver_RawFollowToInbox_IsAcceptedAndRecordsEdge()
    {
        var (server, client, alice, bob) = await LogOnAsync();
        using var _ = server;

        // The page's strategy: build the Follow + deliver it raw to bob's inbox (bob.InboxOf()).
        var follow = BuildFollow(alice, bob);
        var inbox = bob.InboxOf();
        var result = await client.DeliverAsync(inbox, follow);

        // The raw inbox delivery is accepted (signature valid + recipient exists + activity recorded).
        Assert.True(result.IsSuccess, $"DeliverAsync to the inbox must succeed (got {result.StatusCode})");
        Assert.Equal(202, result.StatusCode);

        // The follow edge is recorded on the target: bob's followers now include alice (the sender).
        var followers = await FollowerIrisAsync(client, bob);
        Assert.Contains(alice.Value, followers);
    }

    /// <summary>
    /// The raw delivery is idempotent-ish by IRI: delivering the same <c>Follow</c> (same deterministic
    /// IRI) a second time is still accepted (202) and does not duplicate the edge (the target's followers
    /// still list the sender exactly once). This mirrors how the high-level helpers dedupe by activity IRI.
    /// </summary>
    [Fact]
    public async Task Deliver_RawFollowTwice_SameIri_DoesNotDuplicateEdge()
    {
        var (server, client, alice, bob) = await LogOnAsync();
        using var _ = server;

        var follow = BuildFollow(alice, bob);
        var inbox = bob.InboxOf();

        Assert.Equal(202, (await client.DeliverAsync(inbox, follow)).StatusCode);
        Assert.Equal(202, (await client.DeliverAsync(inbox, follow)).StatusCode);

        var followers = await FollowerIrisAsync(client, bob);
        Assert.Equal(1, followers.Count(i => i == alice.Value));
    }

    /// <summary>
    /// The inbox endpoint's 404 contract: delivering to an inbox whose actor does not exist returns
    /// <c>404 Not Found</c> (the endpoint checks the recipient exists before processing). The page's
    /// target validation + this server check together keep a mistyped IRI from silently succeeding.
    /// </summary>
    [Fact]
    public async Task Deliver_RawFollowToUnknownInbox_IsNotFound()
    {
        var (server, client, alice, _) = await LogOnAsync();
        using var _ = server;

        var ghost = new Iri("http://localhost/ap/v1/u/ghost");
        var follow = BuildFollow(alice, ghost);
        var result = await client.DeliverAsync(ghost.InboxOf(), follow);

        Assert.True(!result.IsSuccess, "delivery to a non-existent actor's inbox must not succeed");
        Assert.Equal(404, result.StatusCode);
    }
}
