using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleServer;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 8 S7 tests: the explorer's write screens. Each write the screens perform is exercised in-process
/// against a live <see cref="SampleServer"/> (TestServer), exactly as the screens issue it through
/// <c>ExplorerSession.GetClient()</c>: compose (<c>PostNoteAsync</c> / <c>PostReplyAsync</c>), like
/// (<c>LikeAsync</c>), and follow / un-follow (<c>FollowAsync</c> / <c>UndoFollowAsync</c>). The last two
/// are also driven over a genuine two-instance federation (A signed as its own actor → B's inbox, with
/// cross-instance key resolution) to cover the slice's federated-write requirement.
/// </summary>
public sealed class S7ScreenTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// Hosts a real <see cref="Iris.Server"/> ActivityPub pipeline (in-memory) for the write screens.
    /// Alice and bob are seeded at the <em>dial base</em> (<c>http://localhost/ap/v1/u/…</c>), the host
    /// the <see cref="TestServer"/> transport dials in-process: the Basic-auth logon fetches the
    /// owner-only document at the dial-base IRI, loads the dial-base key, and the signed client signs
    /// every write as the dial-base actor — so the signature, the activity's body <c>actor</c>, and the
    /// key id all agree on the dial-base IRI. Object/follow targets are also dial-base IRIs. An inbound
    /// <see cref="IActorDocumentFetcher"/> serves actor documents straight from the in-process
    /// persistence, so the inbound key resolver verifies the signature by reading the actor's
    /// <c>publicKey</c>.
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

        // Built by hand (rather than ActivityPubHostFactory) so BaseUri is the dial base — the host the
        // actor-document handler resolves the requesting actor IRI from. That makes the Basic-auth logon
        // (dial-base actor IRI), the signed writes (signed as the dial-base actor), and the activity body
        // actor all agree on one IRI, so the inbound key resolver verifies the signature by reading the
        // actor document's publicKey.
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

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that serves an actor's document directly from the
    /// in-process persistence (no network), so the inbound key resolver verifies the signature by
    /// reading the actor's <c>publicKey</c>.
    /// </summary>
    private sealed class PersistenceActorFetcher(IPersistenceProvider persistence) : IActorDocumentFetcher
    {
        private readonly IPersistenceProvider _persistence = persistence;

        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => await _persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct)
                ? actor
                : null;
    }

    /// <summary>
    /// Logs on as the dial-base <c>alice</c> and returns the signed client plus alice's dial-base actor
    /// IRI (the IRI every write is addressed to and signed as — the logon, signature, and body actor
    /// all agree on it).
    /// </summary>
    private static async Task<(TestServer Server, IActivityPubClient Client, ILocalModerationClient Local, Iri ActorIri)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        return (server, session.GetClient(), session.GetLocalModerationClient(), new Iri("http://localhost/ap/v1/u/alice"));
    }

    private static async Task<IReadOnlyList<IObjectOrLink>> CollectAsync(IAsyncEnumerable<IObjectOrLink> items)
    {
        var list = new List<IObjectOrLink>();
        await foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static async Task<string> ContentOfAsync(IActivityPubClient client, Iri objectIri)
    {
        var obj = await client.GetObjectAsync(objectIri);
        return obj?.Content?.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// Resolves a collection item's IRI the same way the screens do (<c>ObjectView</c>): an
    /// <see cref="IObject"/> carries it in <c>Id</c>; an <see cref="ILink"/> in <c>Href</c>.
    /// </summary>
    private static Iri? IriOf(IObjectOrLink item)
    {
        if (item is IObject { Id: { } id })
        {
            return new Iri(id);
        }

        if (item is ILink { Href: { } href })
        {
            return new Iri(href);
        }

        return null;
    }

    // --- Compose: post a note ----------------------------------------------------

    [Fact]
    public async Task Compose_PostNote_SurfacesInActorOutboxAndObjectView()
    {
        var (server, client, local, actorIri) = await LogOnAsync();
        using var _ = server;

        var content = "<p>S7: a note from the compose screen.</p>";
        var result = await client.PostNoteAsync(actorIri, content);
        Assert.Equal(202, result.StatusCode);

        // The note is stored as a fetchable content object (the object view loads it by IRI).
        var objects = await server.Services.GetRequiredService<IPersistenceProvider>().Objects.ListObjectsAsync();
        var posted = objects.FirstOrDefault(o => o.Content?.FirstOrDefault() == content);
        Assert.NotNull(posted);
        var noteIri = new Iri(posted!.Id!);
        Assert.Equal(content, await ContentOfAsync(client, noteIri));

        // The note appears in the author's outbox (the actor detail screen's feed): the outbox lists the
        // post's Create, whose IRI derives from the note IRI (same content hash). The object view renders
        // each outbox item by its IRI; the posted note itself is fetchable by IRI (asserted above).
        var outbox = await CollectAsync(client.GetCollectionItemsAsync(actorIri.OutboxOf()));
        Assert.Contains(outbox, o => IriOf(o) is { } iri && iri.Value.StartsWith($"{actorIri.Value}/creates/"));
    }

    // --- Compose: post a reply ---------------------------------------------------

    [Fact]
    public async Task Compose_PostReply_SurfacesUnderParentReplies()
    {
        var (server, client, local, actorIri) = await LogOnAsync();
        using var _ = server;

        // Seed a parent note so the reply threads under it (the object view's thread).
        var parent = new Iri($"{actorIri.Value}/notes/1");
        await server.Services.GetRequiredService<IPersistenceProvider>().Objects.PutObjectAsync(new Note
        {
            Id = parent.Value,
            AttributedTo = [new Link { Href = actorIri.Uri }],
            Content = ["<p>parent</p>"],
        });
        var result = await client.PostReplyAsync(
            actorIri, parent, "<p>S7: a reply from the compose screen.</p>", to: [Iri.Public]);
        Assert.Equal(202, result.StatusCode);

        // The reply is stored and lists under the parent's replies collection (the object view's thread).
        // The replies surface items as links (the object view renders each by Href); the reply's content
        // is fetchable by its IRI.
        var replies = await CollectAsync(client.GetRepliesAsync(parent));
        var expected = "<p>S7: a reply from the compose screen.</p>";
        var replyIris = replies
            .Select(r => IriOf(r))
            .Where(i => i is not null)
            .Select(i => i!.Value)
            .ToList();
        var matching = await Task.WhenAll(replyIris.Select(async r =>
            (r, match: (await ContentOfAsync(client, r)) == expected)));
        Assert.True(matching.Any(m => m.match), $"a reply with the posted content must list under the parent (replies: {replyIris.Count})");
    }

    // --- Like --------------------------------------------------------------------

    [Fact]
    public async Task ObjectLike_Like_SurfacesInLikersLikedCollection()
    {
        var (server, client, local, actorIri) = await LogOnAsync();
        using var _ = server;

        // Seed a target note (the object alice likes).
        var bob = new Iri("http://localhost/ap/v1/u/bob");
        var target = new Iri($"{bob.Value}/notes/1");
        await server.Services.GetRequiredService<IPersistenceProvider>().Objects.PutObjectAsync(new Note
        {
            Id = target.Value,
            AttributedTo = [new Link { Href = bob.Uri }],
            Content = ["<p>a note to like</p>"],
        });
        var result = await client.LikeAsync(actorIri, target);
        Assert.Equal(202, result.StatusCode);

        // Decision 055: the server mints the Like's id (an unguessable ULID) and returns it in the 202
        // body; the caller reads DeliveryResult.MintedId. The Like is stored under that learned id (no
        // longer the pre-055 deterministic {actor}/likes/{object} formula), so resolve it by the learned
        // id, not by derivation.
        Assert.True(result.MintedId is { Length: > 0 }, "the like must carry a server-minted id");
        var activity = await server.Services.GetRequiredService<IPersistenceProvider>()
            .Activities.TryGetActivityAsync(new Iri(result.MintedId!), out var stored);
        Assert.True(activity, "the like activity must be stored on the receiving instance");
        Assert.NotNull(stored);

        // The liker's `liked` collection lists the liked object's IRI (as a link — the object view
        // renders it by Href).
        var liked = await CollectAsync(client.GetCollectionItemsAsync(actorIri.LikedOf()));
        Assert.Contains(liked, o => IriOf(o) is { } iri && iri == target);
    }

    [Fact]
    public async Task ObjectUnlike_Undo_LikeRemovesTheLikeEdge()
    {
        var (server, client, local, actorIri) = await LogOnAsync();
        using var _ = server;

        // Seed a target note and like it (records the like edge in the liker's `liked` collection).
        var bob = new Iri("http://localhost/ap/v1/u/bob");
        var target = new Iri($"{bob.Value}/notes/1");
        await server.Services.GetRequiredService<IPersistenceProvider>().Objects.PutObjectAsync(new Note
        {
            Id = target.Value,
            AttributedTo = [new Link { Href = bob.Uri }],
            Content = ["<p>a note to unlike</p>"],
        });
        // Decision 055: capture the id the server minted for the Like (learned from the 202 body) so the
        // unlike can reference it. The client never recomputes the server's ids.
        var likeResult = await client.LikeAsync(actorIri, target);
        Assert.Equal(202, likeResult.StatusCode);
        Assert.True(likeResult.MintedId is { Length: > 0 }, "the like must carry a server-minted id");
        var likedBefore = await CollectAsync(client.GetCollectionItemsAsync(actorIri.LikedOf()));
        Assert.Contains(likedBefore, o => IriOf(o) is { } iri && iri == target);

        // The unlike is the inverse: it removes the like edge from the liker's `liked` collection. It
        // references the original Like by its learned (server-minted) id, not by the liked object's IRI.
        Assert.Equal(202, (await client.UnlikeAsync(actorIri, new Iri(likeResult.MintedId!))).StatusCode);
        // Bypass the client's collection cache — the like wrote a `liked` page the client cached, and a
        // post-write re-read must observe the removal (the same refresh the S4 relay screen uses).
        var likedAfter = await CollectAsync(client.GetCollectionItemsAsync(
            actorIri.LikedOf(), new CollectionQuery { BypassCache = true }));
        Assert.DoesNotContain(likedAfter, o => IriOf(o) is { } iri && iri == target);
    }

    // --- Boost / Unboost (the ObjectPage's Boost/Unboost button) -------------------
    //
    // The ObjectPage's Boost button calls the client's AnnounceAsync (boost) / UnannounceAsync
    // (unboost), both published to the acting actor's own outbox through the signed pipeline. These
    // exercise the same calls the button makes, in-process, and verify each lands in alice's outbox
    // with the deterministic IRI the server mints.

    [Fact]
    public async Task ObjectBoost_Announce_SurfacesInAuthorOutbox()
    {
        var (server, client, local, actorIri) = await LogOnAsync();
        using var _ = server;

        // Seed a target note (the object alice boosts), authored by bob (local, dial-base).
        var bob = new Iri("http://localhost/ap/v1/u/bob");
        var target = new Iri($"{bob.Value}/notes/1");
        await server.Services.GetRequiredService<IPersistenceProvider>().Objects.PutObjectAsync(new Note
        {
            Id = target.Value,
            AttributedTo = [new Link { Href = bob.Uri }],
            Content = ["<p>a note to boost</p>"],
        });

        var result = await client.AnnounceAsync(actorIri, target);
        Assert.Equal(202, result.StatusCode);

        // Decision 055: the server mints the Announce's id (an unguessable ULID) and returns it in the
        // 202 body; resolve it by the learned id (the pre-055 {actor}/announces/{object} formula no
        // longer holds).
        Assert.True(result.MintedId is { Length: > 0 }, "the announce must carry a server-minted id");
        var announceIri = new Iri(result.MintedId!);
        var persistence = server.Services.GetRequiredService<IPersistenceProvider>();
        var activity = await persistence.Activities.TryGetActivityAsync(announceIri, out var stored);
        Assert.True(activity, "the announce activity must be stored on the receiving instance");
        Assert.IsType<Announce>(stored);

        // The announcer's outbox lists the Announce (the object view's boost indicator reads it).
        var outbox = await CollectAsync(client.GetCollectionItemsAsync(actorIri.OutboxOf()));
        Assert.Contains(outbox, o => IriOf(o) is { } iri && iri.Value == announceIri.Value);
    }

    [Fact]
    public async Task ObjectUnboost_Undo_RemovesTheBoostEdge()
    {
        var (server, client, local, actorIri) = await LogOnAsync();
        using var _ = server;

        var bob = new Iri("http://localhost/ap/v1/u/bob");
        var target = new Iri($"{bob.Value}/notes/1");
        await server.Services.GetRequiredService<IPersistenceProvider>().Objects.PutObjectAsync(new Note
        {
            Id = target.Value,
            AttributedTo = [new Link { Href = bob.Uri }],
            Content = ["<p>a note to boost and unboost</p>"],
        });

        // Boost, then unboost (the ObjectPage's Boost → Unboost toggle). Decision 055: capture the id the
        // server minted for the Announce (learned from the 202 body) so the unboost can reference it.
        var announceResult = await client.AnnounceAsync(actorIri, target);
        Assert.Equal(202, announceResult.StatusCode);
        Assert.True(announceResult.MintedId is { Length: > 0 }, "the announce must carry a server-minted id");
        var announceIri = new Iri(announceResult.MintedId!);
        var persistence = server.Services.GetRequiredService<IPersistenceProvider>();
        var announced = await persistence.Activities.TryGetActivityAsync(announceIri, out var _);
        Assert.True(announced, "the announce must be stored before the unboost");

        // The unboost is an Undo referencing the exact Announce by its learned (server-minted) IRI. The
        // server mints the Undo's own id (returned in the 202 body); resolve it by that learned id.
        var undoResult = await client.UnannounceAsync(actorIri, announceIri);
        Assert.Equal(202, undoResult.StatusCode);
        Assert.True(undoResult.MintedId is { Length: > 0 }, "the unannounce must carry a server-minted id");
        var undo = await persistence.Activities.TryGetActivityAsync(
            new Iri(undoResult.MintedId!), out var storedUndo);
        Assert.True(undo, "the undo-of-announce activity must be stored on the receiving instance");
        Assert.IsType<Undo>(storedUndo);
        var referenced = (storedUndo as Undo)!.Object?.FirstOrDefault()?.ResolveObjectIri();
        Assert.Equal(announceIri.Value, referenced?.Value);
    }

    [Fact]
    public async Task ObjectDelete_Delete_TombstonesTheObject()
    {
        var (server, client, local, actorIri) = await LogOnAsync();
        using var _ = server;
        var persistence = server.Services.GetRequiredService<IPersistenceProvider>();

        // Post a note (the object alice deletes) — it is stored and appears in the author's outbox.
        var content = "<p>S7: a note to delete.</p>";
        Assert.Equal(202, (await client.PostNoteAsync(actorIri, content)).StatusCode);
        var objects = await persistence.Objects.ListObjectsAsync();
        var posted = objects.First(o => o.Content?.FirstOrDefault() == content);
        var noteIri = new Iri(posted.Id!);
        var outboxBefore = await CollectAsync(client.GetCollectionItemsAsync(actorIri.OutboxOf()));
        Assert.Contains(outboxBefore, o => IriOf(o) is { } iri && iri.Value.StartsWith($"{actorIri.Value}/creates/"));

        // The delete publishes a Delete to the author's outbox; the server routes it to the
        // DeleteActivityHandler, which tombstones the object and removes its Create from the outbox.
        Assert.Equal(202, (await client.DeleteAsync(actorIri, noteIri)).StatusCode);

        // The object's IRI now resolves to a Tombstone (the "deleted" marker), not the original note.
        var after = await client.GetObjectAsync(noteIri);
        Assert.NotNull(after);
        Assert.IsType<Tombstone>(after);
        Assert.Equal(noteIri.Value, after!.Id);

        // The deleted note's Create is no longer listed in the author's outbox (the outbox no longer
        // surfaces the deleted content).
        var outboxAfter = await CollectAsync(client.GetCollectionItemsAsync(
            actorIri.OutboxOf(), new CollectionQuery { BypassCache = true }));
        Assert.DoesNotContain(outboxAfter, o => IriOf(o) is { } iri && iri.Value.StartsWith($"{actorIri.Value}/creates/"));
    }

    // --- Moderation (local single instance) --------------------------------------

    [Fact]
    public async Task Moderation_MuteUnmute_RecordsAndRemovesMuteEdge()
    {
        var (server, client, local, actorIri) = await LogOnAsync();
        using var _ = server;

        var target = new Iri("http://localhost/ap/v1/u/bob");
        var persistence = server.Services.GetRequiredService<IPersistenceProvider>();

        // A mute is a local, Basic-authenticated decision (204 No Content), recorded in the muter's
        // mutes collection — not a signed inbox delivery.
        Assert.Equal(204, (await local.MuteAsync(actorIri, target)).StatusCode);
        Assert.True(
            await persistence.Moderation.IsMutedAsync(actorIri, target),
            "after a mute, the muter's mutes collection must list the target");
        var mutes = await CollectAsync(client.GetMutesAsync(actorIri));
        Assert.Contains(mutes, o => IriOf(o) is { } iri && iri == target);

        // The inverse un-mute removes the edge.
        Assert.Equal(204, (await local.UnmuteAsync(actorIri, target)).StatusCode);
        Assert.False(
            await persistence.Moderation.IsMutedAsync(actorIri, target),
            "after an un-mute, the mute edge must be gone");
    }

    [Fact]
    public async Task Moderation_BlockUnblock_RecordsAndRemovesBlockEdge()
    {
        var (server, client, local, actorIri) = await LogOnAsync();
        using var _ = server;

        var target = new Iri("http://localhost/ap/v1/u/bob");
        var persistence = server.Services.GetRequiredService<IPersistenceProvider>();

        // A block is a signed write to the actor's own outbox (the delivery model); the instance records
        // the block edge in the blocker's blocks collection (202 Accepted). Decision 055: capture the id
        // the server minted for the Block so the un-block can reference it (the client never recomputes
        // the server's ids).
        var blockResult = await client.BlockAsync(actorIri, target);
        Assert.Equal(202, blockResult.StatusCode);
        Assert.True(blockResult.MintedId is { Length: > 0 }, "the block must carry a server-minted id");
        Assert.True(
            await persistence.Moderation.IsBlockedAsync(actorIri, target),
            "after a block, the blocker's blocks collection must list the target");
        var blocks = await CollectAsync(client.GetBlocksAsync(actorIri));
        Assert.Contains(blocks, o => IriOf(o) is { } iri && iri == target);

        // The inverse un-block (an Undo published to the actor's outbox) removes the edge, referencing
        // the original Block by its learned (server-minted) id.
        Assert.Equal(202, (await client.UnblockAsync(actorIri, new Iri(blockResult.MintedId!))).StatusCode);
        Assert.False(
            await persistence.Moderation.IsBlockedAsync(actorIri, target),
            "after an un-block, the block edge must be gone");
    }

    [Fact]
    public async Task Moderation_FlagUnflag_RecordsAndRemovesFlagEdge()
    {
        var (server, client, local, actorIri) = await LogOnAsync();
        using var _ = server;

        var target = new Iri("http://localhost/ap/v1/u/bob");
        var persistence = server.Services.GetRequiredService<IPersistenceProvider>();

        // A flag is a signed write to the actor's own outbox (a moderation report); the instance records
        // the flag edge in the flagger's flags collection (202 Accepted). Decision 055: capture the id
        // the server minted for the Flag so the un-flag can reference it.
        var flagResult = await client.FlagAsync(actorIri, target);
        Assert.Equal(202, flagResult.StatusCode);
        Assert.True(flagResult.MintedId is { Length: > 0 }, "the flag must carry a server-minted id");
        Assert.True(
            await persistence.Moderation.HasFlaggedAsync(actorIri, target),
            "after a flag, the flagger's flags collection must list the target");
        var flags = await CollectAsync(client.GetFlagsAsync(actorIri));
        Assert.Contains(flags, o => IriOf(o) is { } iri && iri == target);

        // The inverse un-flag (an Undo published to the actor's outbox) removes the edge, referencing
        // the original Flag by its learned (server-minted) id.
        Assert.Equal(202, (await client.UnflagAsync(actorIri, new Iri(flagResult.MintedId!))).StatusCode);
        Assert.False(
            await persistence.Moderation.HasFlaggedAsync(actorIri, target),
            "after an un-flag, the flag edge must be gone");
    }

    // --- Follow / un-follow (local single instance) ------------------------------

    [Fact]
    public async Task ActorFollow_Follow_SurfacesInFollowersCollection()
    {
        var (server, client, local, follower) = await LogOnAsync();
        using var _ = server;

        var target = new Iri("http://localhost/ap/v1/u/bob");
        var result = await client.FollowAsync(follower, target);
        Assert.Equal(202, result.StatusCode);

        // The follow edge is recorded: bob's followers collection lists alice (by IRI).
        var followers = await CollectAsync(client.GetCollectionItemsAsync(target.FollowersOf()));
        Assert.Contains(followers, o => IriOf(o) is { } iri && iri == follower);
    }

    [Fact]
    public async Task ActorUnfollow_AfterFollow_RemovesFollowEdge()
    {
        var (server, client, local, follower) = await LogOnAsync();
        using var _ = server;

        var target = new Iri("http://localhost/ap/v1/u/bob");
        // Decision 055: capture the id the server minted for the Follow so the un-follow can reference it
        // (the client never recomputes the server's ids).
        var followResult = await client.FollowAsync(follower, target);
        Assert.Equal(202, followResult.StatusCode);
        Assert.True(followResult.MintedId is { Length: > 0 }, "the follow must carry a server-minted id");

        // The un-follow is an Undo referencing the original Follow by its learned (server-minted) id; the
        // receiver resolves it and removes the recorded edge.
        Assert.Equal(202, (await client.UndoFollowAsync(follower, new Iri(followResult.MintedId!))).StatusCode);

        var followers = await CollectAsync(client.GetCollectionItemsAsync(target.FollowersOf()));
        Assert.DoesNotContain(followers, o => IriOf(o) is { } iri && iri == follower);
    }

    // --- Follow / un-follow (two-instance federation) ----------------------------

    [Fact]
    public async Task ActorFollow_FederatedAcrossInstances_RecordsEdgeOnTarget()
    {
        // A (host a.example, actor alice) follows B (host b.example, actor bob) over the wire. Per the
        // delivery model, the client publishes the authored Follow to alice's OWN outbox — which lives on
        // A (alice's home instance) — NOT to bob's inbox. A records the Follow in alice's outbox and then
        // (the server's job) delivers it to bob's inbox on B; B's inbound key resolver verifies the
        // signature by fetching alice's actor document from A. The un-follow is an Undo published to
        // alice's own outbox (A) likewise; A resolves the stored Follow and removes the edge, and the
        // server delivers the Undo to bob's inbox on B.
        const string AHost = "a.example";
        const string BHost = "b.example";
        var aPersistence = new Iris.Server.InMemory.InMemoryPersistenceProvider();
        var bPersistence = new Iris.Server.InMemory.InMemoryPersistenceProvider();
        var (aliceKey, aliceActorIri, _) = TestSeeder.SeedPersonWithKey(aPersistence, AHost, "alice");
        var (bobKey, bobActorIri, _) = TestSeeder.SeedPersonWithKey(bPersistence, BHost, "bob");

        // Each instance's inbound key resolver resolves a signing actor's public key by fetching the
        // actor's document, and each instance's outbound DeliveryWorker delivers to the other instance's
        // inbox. A's TestServer does not yet exist while A is being constructed, so both the fetcher and
        // the delivery transport are deferred: they capture a reference to A that is filled in once A is
        // built (the LazyHandler resolves it on first use, after construction completes).
        TestServer? aRef = null;
        TestServer? bRef = null;
        Func<HttpMessageHandler> aHandler = () => new LazyHandler(() => aRef!.CreateHandler());
        Func<HttpMessageHandler> bHandler = () => new LazyHandler(() => bRef!.CreateHandler());
        using var a = TestFederation.StartServer(AHost, "alice", aPersistence, aliceKey,
            new RemoteDocumentFetcher(aHandler, AHost), bHandler);
        aRef = a;
        using var b = TestFederation.StartServer(BHost, "bob", bPersistence, bobKey,
            new RemoteDocumentFetcher(aHandler, AHost), aHandler);
        bRef = b;

        // A client signed as alice (A's key), routed to A — alice's home instance — because her authored
        // activities are published to her OWN outbox (which lives on A), never to a recipient's inbox.
        var keyStore = new Iris.Core.Identity.InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        var keyProvider = new Iris.Client.Auth.InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(aliceActorIri, aliceKey.KeyId);
        var signer = new Iris.Core.Signing.HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var toA = factory.Create(new ActivityPubClientOptions { ActorId = aliceActorIri, EnableRetry = false }, a.CreateHandler());
        using var _ = toA;

        // alice follows bob: the Follow is published to alice's own outbox (A); A records it in its own
        // follow store (the actor's `following` collection lists even a remote target) and the server
        // delivers it to bob's inbox (B), where B records the follow edge. Decision 055: capture the id
        // the server minted for the Follow so the un-follow can reference it (the client never
        // recomputes the server's ids).
        var followResult = await toA.FollowAsync(aliceActorIri, bobActorIri);
        Assert.Equal(202, followResult.StatusCode);
        Assert.True(followResult.MintedId is { Length: > 0 }, "the follow must carry a server-minted id");
        Assert.True(
            await aPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            "after a federated Follow, alice must follow bob in A's follow store (her own outbox)");

        // The server→server delivery of the Follow to bob's inbox (B) is asynchronous (the DeliveryWorker
        // pumps the queue), so poll for B's recorded edge.
        await TestFederation.WaitForAsync(
            () => bPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            TimeSpan.FromSeconds(5));
        Assert.True(
            await bPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            "after a federated Follow, B must record the follow edge (server delivered it to bob's inbox)");

        // alice un-follows bob: the Undo is published to alice's own outbox (A); A resolves the stored
        // Follow (by its learned, server-minted id) and removes the edge.
        Assert.Equal(202, (await toA.UndoFollowAsync(aliceActorIri, new Iri(followResult.MintedId!))).StatusCode);
        var aliceFollowing = await aPersistence.Follows.GetFollowingAsync(aliceActorIri);
        Assert.DoesNotContain(bobActorIri, aliceFollowing);
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that resolves an actor document by fetching the actor from
    /// a specific in-process <see cref="TestServer"/> (the source instance that hosts the actor). Used to
    /// wire cross-instance key resolution in a two-instance federation test: B's resolver fetches alice's
    /// document from A. The handler is a deferred factory (a <see cref="LazyHandler"/>) so an instance's
    /// fetcher can reach its own (not-yet-constructed) TestServer.
    /// </summary>
    private sealed class RemoteDocumentFetcher(Func<HttpMessageHandler> handlerFactory, string host) : IActorDocumentFetcher
    {
        private readonly Func<HttpMessageHandler> _handlerFactory = handlerFactory;
        private readonly string _host = host;

        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            var uri = new Uri(actorIri.Value);
            var handler = _handlerFactory();
            using var http = new HttpClient(handler, disposeHandler: false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/activity+json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return ActivityJson.Deserialize<Actor>(body);
        }
    }
}
