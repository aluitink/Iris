using Iris.Client;
using Iris.Client.Collections;
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
/// Phase 8 (second round) S6 tests: the actor-detail page shows the <em>logged-on</em> actor's own
/// moderation (their <c>mutes</c>/<c>blocks</c>/<c>flags</c> collections — what *they* have
/// muted/blocked/flagged), in addition to the target actor's own moderation counts. The page's
/// Mute / Block / Flag buttons act as the logged-on actor, so surfacing their own state makes the
/// buttons' effect visible. These tests exercise the same reads the page performs (the logged-on
/// actor's own mutes/blocks/flags collections, read through the client with a post-write
/// bypass-cache) and the same writes the buttons issue (<c>MuteAsync</c> / <c>BlockAsync</c> /
/// <c>FlagAsync</c> as the logged-on actor).
/// </summary>
public sealed class S6MyModerationTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// Hosts a real in-process ActivityPub server for the write screens, with <c>alice</c> (the acting
    /// actor) + <c>bob</c> (the moderation target) at the dial base. Mirrors <see cref="S7ScreenTests.StartHost"/>.
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

    private static async Task<(TestServer Server, IActivityPubClient Client, ILocalModerationClient Local, Iri AliceIri, Iri BobIri)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        return (server, session.GetClient(), session.GetLocalModerationClient(),
            new Iri("http://localhost/ap/v1/u/alice"),
            new Iri("http://localhost/ap/v1/u/bob"));
    }

    private static async Task<int> CountAsync(IAsyncEnumerable<IObjectOrLink> items, CollectionQuery? query = null)
    {
        var count = 0;
        await foreach (var _ in items)
        {
            count++;
        }

        return count;
    }

    private static string? IriOf(IObjectOrLink item)
    {
        if (item is IObject { Id: { } id })
        {
            return id;
        }

        if (item is ILink { Href: { } href })
        {
            return href.ToString();
        }

        return null;
    }

    /// <summary>
    /// The logged-on actor's own moderation counts, the way the actor-detail page's "My moderation" card
    /// reads them (the actor's own <c>mutes</c>/<c>blocks</c>/<c>flags</c> collections), optionally
    /// bypassing the page cache (post-write, to observe the update).
    /// </summary>
    private static async Task<(int Mutes, int Blocks, int Flags)> MyModerationAsync(
        IActivityPubClient client, Iri me, bool bypassCache = false)
    {
        var query = bypassCache ? new CollectionQuery(BypassCache: true) : null;
        return (
            await CountAsync(client.GetMutesAsync(me, query)),
            await CountAsync(client.GetBlocksAsync(me, query)),
            await CountAsync(client.GetFlagsAsync(me, query)));
    }

    [Fact]
    public async Task MyModeration_Mute_Block_Flag_AppearInOwnCollections()
    {
        var (server, client, local, alice, bob) = await LogOnAsync();
        using var _ = server;

        // Initially alice's own moderation collections are empty (the card starts at 0/0/0).
        var before = await MyModerationAsync(client, alice);
        Assert.True(before == (0, 0, 0), $"alice's own moderation must start empty (got {before})");

        // Alice mutes bob (local, Basic-authenticated — 204). It appears in alice's OWN mutes collection.
        Assert.True((await local.MuteAsync(alice, bob)).StatusCode == 204, "muting must succeed (204)");
        var afterMute = await MyModerationAsync(client, alice, bypassCache: true);
        Assert.True(afterMute.Mutes == 1, $"after muting bob, alice's own mutes count must be 1 (got {afterMute.Mutes})");

        // Alice blocks bob (signed write to her outbox — 202). It appears in alice's OWN blocks collection.
        Assert.True((await client.BlockAsync(alice, bob)).StatusCode == 202, "blocking must succeed (202)");
        var afterBlock = await MyModerationAsync(client, alice, bypassCache: true);
        Assert.True(afterBlock.Blocks == 1, $"after blocking bob, alice's own blocks count must be 1 (got {afterBlock.Blocks})");
        Assert.True(afterBlock.Mutes == 1, "muting earlier must still be reflected");

        // Alice flags bob (signed write — 202). It appears in alice's OWN flags collection.
        Assert.True((await client.FlagAsync(alice, bob)).StatusCode == 202, "flagging must succeed (202)");
        var afterFlag = await MyModerationAsync(client, alice, bypassCache: true);
        Assert.True(afterFlag.Mutes == 1 && afterFlag.Blocks == 1 && afterFlag.Flags == 1,
            $"after mute/block/flag, alice's own counts must all be 1 (got {afterFlag})");
    }

    [Fact]
    public async Task MyModeration_Unmute_Unblock_Unflag_RemoveFromOwnCollections()
    {
        var (server, client, local, alice, bob) = await LogOnAsync();
        using var _ = server;

        // Seed all three edges as alice, then remove each and assert the counts return to 0.
        Assert.True((await local.MuteAsync(alice, bob)).StatusCode == 204, "muting must succeed (204)");
        Assert.True((await client.BlockAsync(alice, bob)).StatusCode == 202, "blocking must succeed (202)");
        Assert.True((await client.FlagAsync(alice, bob)).StatusCode == 202, "flagging must succeed (202)");
        var seeded = await MyModerationAsync(client, alice, bypassCache: true);
        Assert.True(seeded == (1, 1, 1), $"after seeding all three edges, alice's own counts must all be 1 (got {seeded})");

        Assert.True((await local.UnmuteAsync(alice, bob)).StatusCode == 204, "unmuting must succeed (204)");
        Assert.True((await client.UnblockAsync(alice, bob)).StatusCode == 202, "unblocking must succeed (202)");
        Assert.True((await client.UnflagAsync(alice, bob)).StatusCode == 202, "unflagging must succeed (202)");

        var cleared = await MyModerationAsync(client, alice, bypassCache: true);
        Assert.True(cleared == (0, 0, 0), $"after unmute/unblock/unflag, alice's own moderation must be empty (got {cleared})");
    }
}
