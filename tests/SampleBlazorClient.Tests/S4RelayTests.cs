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
/// Phase 8 (second round) S4 tests: the relay feature (F-06). The <c>ActorDetail</c> page's new relays
/// card lists an actor's relays (<c>GetRelaysAsync</c>, the <c>{actor}/relays</c> collection — the ActivityPub
/// <c>star</c> set) and subscribes / unsubscribes via the local, Basic-authenticated
/// <c>SubscribeRelayAsync</c> / <c>UnsubscribeRelayAsync</c>. These tests exercise the same API the card uses:
/// subscribe an actor to a relay and assert it appears in the actor's relays collection; unsubscribe and
/// assert it is gone.
/// </summary>
public sealed class S4RelayTests
{
    private static Uri DialBase => new("http://localhost");

    private static readonly Iri RelayA = new("https://relay-a.example/");
    private static readonly Iri RelayB = new("https://relay-b.example/");

    /// <summary>
    /// Hosts a real <see cref="Iris.Server"/> ActivityPub pipeline (in-memory) for relay subscription, with
    /// <c>alice</c> (the acting actor) + <c>bob</c> seeded at the dial base. Mirrors
    /// <see cref="S3FollowFeedTests.StartHost"/>.
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

    private static async Task<(TestServer Server, IActivityPubClient Client, Iri AliceIri)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        return (server, session.GetClient(), new Iri("http://localhost/ap/v1/u/alice"));
    }

    private static async Task<IReadOnlyList<string>> RelayIrisAsync(
        IActivityPubClient client, Iri actor, bool bypassCache = false)
    {
        var iris = new List<string>();
        // After a subscribe/unsubscribe write the relays collection is served through the local
        // collection-page cache, so a follow-up read must bypass the cache to observe the update.
        var query = bypassCache ? new Iris.Client.Collections.CollectionQuery(BypassCache: true) : null;
        await foreach (var item in client.GetRelaysAsync(actor, query))
        {
            var iri = item is IObject { Id: { } id } ? id : (item is ILink { Href: { } href } ? href.ToString() : null);
            if (iri is not null)
            {
                iris.Add(iri);
            }
        }

        return iris;
    }

    [Fact]
    public async Task SubscribeRelay_RelayAppearsInRelaysCollection()
    {
        var (server, client, alice) = await LogOnAsync();
        using var _ = server;

        // Initially alice subscribes to no relays.
        Assert.Empty(await RelayIrisAsync(client, alice));

        // Subscribe to relay-a (a local, Basic-authenticated decision on alice's own instance).
        var subscribe = await client.SubscribeRelayAsync(alice, RelayA);
        Assert.True(subscribe.StatusCode == 204, $"subscribing to a relay must succeed (got {subscribe.StatusCode})");

        // relay-a is now in alice's relays collection (bypass the page cache to observe the write).
        var relays = await RelayIrisAsync(client, alice, bypassCache: true);
        Assert.True(relays.Contains(RelayA.Value, StringComparer.Ordinal),
            $"alice's relays must contain relay-a (got {string.Join(", ", relays)})");

        // Subscribing to a second relay adds it (both present).
        Assert.Equal(204, (await client.SubscribeRelayAsync(alice, RelayB)).StatusCode);
        var relays2 = await RelayIrisAsync(client, alice, bypassCache: true);
        Assert.True(relays2.Contains(RelayA.Value, StringComparer.Ordinal), $"relays must contain relay-a (got {string.Join(", ", relays2)})");
        Assert.True(relays2.Contains(RelayB.Value, StringComparer.Ordinal), $"relays must contain relay-b (got {string.Join(", ", relays2)})");
    }

    [Fact]
    public async Task UnsubscribeRelay_RelayRemovedFromRelaysCollection()
    {
        var (server, client, alice) = await LogOnAsync();
        using var _ = server;

        // Subscribe to both relays, then unsubscribe relay-a.
        Assert.Equal(204, (await client.SubscribeRelayAsync(alice, RelayA)).StatusCode);
        Assert.Equal(204, (await client.SubscribeRelayAsync(alice, RelayB)).StatusCode);
        var before = await RelayIrisAsync(client, alice, bypassCache: true);
        Assert.True(before.Contains(RelayA.Value, StringComparer.Ordinal), $"relays must contain relay-a before unsubscribe (got {string.Join(", ", before)})");

        Assert.Equal(204, (await client.UnsubscribeRelayAsync(alice, RelayA)).StatusCode);

        // relay-a is gone; relay-b remains (bypass the page cache to observe the removal).
        var after = await RelayIrisAsync(client, alice, bypassCache: true);
        Assert.False(after.Contains(RelayA.Value, StringComparer.Ordinal), $"relay-a must be gone after unsubscribe (got {string.Join(", ", after)})");
        Assert.True(after.Contains(RelayB.Value, StringComparer.Ordinal), $"relay-b must remain (got {string.Join(", ", after)})");
    }
}
