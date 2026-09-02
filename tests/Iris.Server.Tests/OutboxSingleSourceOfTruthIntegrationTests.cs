 using System.Net;
 using System.Net.Http.Headers;
 using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Integration test for the Phase 19.6.2 architectural expectation: <em>all activities flow through the
/// outbox</em>. Every activity a local actor authors — including the follow <c>Accept</c>/<c>Reject</c>
/// the operator publishes to the followed actor's own outbox (Phase 19.0b, AP-native; the legacy
/// Basic-auth follow-decision endpoint is removed) — is recorded in the actor's outbox, exactly once, in a
/// stable (recorded) order. The outbox is therefore the single source of truth for the actor's authored
/// history: reading the outbox yields precisely the activities the actor did, nothing more and nothing
/// less, with no duplicates.
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts alice (the instance actor, the one who authors the
/// activities) plus bob (a second local actor, so alice's local <c>Follow</c>/<c>Block</c> have a local
/// recipient and need no cross-instance hop). A remote actor (remote.domain.local, never hosted) follows
/// alice twice so the operator can publish an <c>Accept</c> and a <c>Reject</c> (to alice's outbox,
/// AP-native Phase 19.0b) on alice's behalf. The test authors every supported activity type, then
/// enumerates alice's outbox and
/// asserts it contains exactly the authored set, each once, in the store's stable order (newest-first,
/// the order the outbox collection renders). The raw-inspector (UI) half of 19.6.2 is exercised live in
/// the two-instance Docker environment.
/// </remarks>
public sealed class OutboxSingleSourceOfTruthIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string RemoteHost = "remote.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string AlicePassword = "alice-password";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly IActivityPubClient _client;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _alice;
    private readonly Iri _bob;
    private readonly Iri _remote;
    private readonly KeyPair _aliceKey;

    public OutboxSingleSourceOfTruthIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var (aliceKey, aliceIri, _) = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        var (_, bobIri, _) = TestSeeder.SeedPersonWithKey(_persistence, AHost, Bob);
        _alice = aliceIri;
        _bob = bobIri;
        _aliceKey = aliceKey;
        _remote = new Iri($"https://{RemoteHost}/ap/v1/u/remote");

        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(iri == _alice && username == Alice && password == AlicePassword));

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _persistence,
            ExtraLocalActors = [bobIri],
            CredentialValidator = credentialValidator,
            // Self-loop fetcher: the only actor document this instance serves is alice's/bob's, so the
            // delivery worker's sharedInbox resolution is a no-op (alice has no followers; bob is local)
            // and the remote follow-decision deliveries are queued but never fetched in this test.
            Fetcher = BuildSelfFetcher(aliceKey, aliceIri, () => _server!.CreateHandler()),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);

        // A signed client that routes to the TestServer (the client's one-call operations — Announce,
        // Unannounce, etc. — exercise the full signed pipeline against the live outbox endpoint).
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_aliceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(_alice, new Iri($"{_alice.Value}#key-1"));
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        _client = factory.Create(
            new ActivityPubClientOptions { ActorId = _alice, EnableRetry = false },
            _server.CreateHandler());
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- Every authored activity appears in the outbox exactly once, in order ----------------
    //
    // The heart of 19.6.2: after exercising every supported write, the outbox is the single source of
    // truth for the actor's authored history.

    [Fact]
    public async Task EveryAuthoredActivity_AppearsInTheOutbox_OnceInStableOrder()
    {
        // Decision 055: the client authors each activity WITHOUT an id; the server mints the id (and, for
        // a Create, the embedded Note's id) and returns the created activity in the 202 body. The test
        // learns each minted id from the response and threads the learned id into any reference-carrying
        // follow-up (an Undo/Delete references the LEARNED id of the target, never a recomputed formula).
        // The "single source of truth" property — exactly the authored set, each once, in order — is
        // unchanged; only the ids are now server-minted rather than client-chosen.

        // 1. Follow bob (a local recipient — no cross-instance hop).
        var follow = BuildFollow(_alice, _bob);
        var (followStatus, followId) = await PostOutboxAsync(follow);
        Assert.Equal((int)HttpStatusCode.Accepted, followStatus);
        Assert.NotNull(followId);

        // 2. Create a note (alice has no followers, so no fan-out).
        var create = BuildCreate(_alice);
        var (createStatus, createId) = await PostOutboxAsync(create);
        Assert.Equal((int)HttpStatusCode.Accepted, createStatus);
        Assert.NotNull(createId);

        // 3. Like a note.
        var like = BuildLike(_alice);
        var (likeStatus, likeId) = await PostOutboxAsync(like);
        Assert.Equal((int)HttpStatusCode.Accepted, likeStatus);
        Assert.NotNull(likeId);

        // 4. Announce a note.
        var announce = BuildAnnounce(_alice);
        var (announceStatus, announceId) = await PostOutboxAsync(announce);
        Assert.Equal((int)HttpStatusCode.Accepted, announceStatus);
        Assert.NotNull(announceId);

        // 5. Block bob (a local recipient).
        var block = BuildBlock(_alice, _bob);
        var (blockStatus, blockId) = await PostOutboxAsync(block);
        Assert.Equal((int)HttpStatusCode.Accepted, blockStatus);
        Assert.NotNull(blockId);

        // 6. Undo the follow (un-follow bob). The Undo references the LEARNED follow id.
        var undo = BuildUndo(_alice, followId!.Value);
        var (undoStatus, undoId) = await PostOutboxAsync(undo);
        Assert.Equal((int)HttpStatusCode.Accepted, undoStatus);
        Assert.NotNull(undoId);

        // 7. Delete the created note. The Delete references the LEARNED note id (the embedded Note's
        //    server-minted id, read from the stored Create). The note's Create stays in the outbox: this
        //    Create's IRI is not the deterministic sibling of the note, so the Delete's inverse-removal
        //    is a no-op — the point here is that the Delete itself is recorded as an authored activity.
        var noteIri = await LearnEmbeddedNoteIriAsync(createId!.Value);
        var delete = BuildDelete(_alice, noteIri);
        var (deleteStatus, deleteId) = await PostOutboxAsync(delete);
        Assert.Equal((int)HttpStatusCode.Accepted, deleteStatus);
        Assert.NotNull(deleteId);

        // 8. Accept a remote follow of alice (published to alice's outbox; the instance applies the edge
        //    and server-delivers the Accept). The Accept's own id is server-minted.
        var remoteFollow1 = BuildRemoteFollow(_remote, _alice);
        await RecordProvisionalFollowAsync(remoteFollow1);
        var (acceptStatus, acceptId) = await DecisionAsync(remoteFollow1, accept: true);
        Assert.Equal((int)HttpStatusCode.Accepted, acceptStatus);
        Assert.NotNull(acceptId);

        // 9. Reject a second remote follow of alice (published to alice's outbox; the instance removes the
        //    provisional edge and server-delivers the Reject). The Reject's own id is server-minted.
        var remoteFollow2 = BuildRemoteFollow(_remote, _alice);
        await RecordProvisionalFollowAsync(remoteFollow2);
        var (rejectStatus, rejectId) = await DecisionAsync(remoteFollow2, accept: false);
        Assert.Equal((int)HttpStatusCode.Accepted, rejectStatus);
        Assert.NotNull(rejectId);

        // 10. Flag bob (a moderation report; the instance records the flag edge locally).
        var flag = BuildFlag(_alice, _bob);
        var (flagStatus, flagId) = await PostOutboxAsync(flag);
        Assert.Equal((int)HttpStatusCode.Accepted, flagStatus);
        Assert.NotNull(flagId);

        // 11. Undo the flag (un-flag bob; the instance removes the flag edge). References the learned flag id.
        var undoFlag = BuildUndo(_alice, flagId!.Value);
        var (undoFlagStatus, undoFlagId) = await PostOutboxAsync(undoFlag);
        Assert.Equal((int)HttpStatusCode.Accepted, undoFlagStatus);
        Assert.NotNull(undoFlagId);

        // 12. Undo the like (un-like the liked object; the instance removes the like edge). References
        //     the learned like id.
        var undoLike = BuildUndo(_alice, likeId!.Value);
        var (undoLikeStatus, undoLikeId) = await PostOutboxAsync(undoLike);
        Assert.Equal((int)HttpStatusCode.Accepted, undoLikeStatus);
        Assert.NotNull(undoLikeId);

        // 13. Undo the announce (un-boost the announced object; the instance removes the announce edge).
        //     References the learned announce id.
        var undoAnnounce = BuildUndo(_alice, announceId!.Value);
        var (undoAnnounceStatus, undoAnnounceId) = await PostOutboxAsync(undoAnnounce);
        Assert.Equal((int)HttpStatusCode.Accepted, undoAnnounceStatus);
        Assert.NotNull(undoAnnounceId);

        // 14. Undo the block (un-block bob; the instance removes the block edge). References the learned
        //     block id.
        var undoBlock = BuildUndo(_alice, blockId!.Value);
        var (undoBlockStatus, undoBlockId) = await PostOutboxAsync(undoBlock);
        Assert.Equal((int)HttpStatusCode.Accepted, undoBlockStatus);
        Assert.NotNull(undoBlockId);

        // --- The outbox is the single source of truth: exactly the authored set, each once, in order.

        var outbox = (await _persistence.Activities.GetOutboxAsync(_alice)).ToList();
        var ids = outbox.Select(a => a.Id).Cast<string>().ToList();

        // The authored set (each its server-minted id, as a string), in authoring order.
        var authored = new[]
        {
            followId!.Value.Value, createId!.Value.Value, likeId!.Value.Value, announceId!.Value.Value, blockId!.Value.Value,
            undoId!.Value.Value, deleteId!.Value.Value, acceptId!.Value.Value, rejectId!.Value.Value, flagId!.Value.Value,
            undoFlagId!.Value.Value, undoLikeId!.Value.Value, undoAnnounceId!.Value.Value, undoBlockId!.Value.Value,
        };

        // The outbox lists activities newest-first (the store inserts at the front), so the expected
        // collection order is the authored set reversed: the most recent (the Reject) first, the
        // earliest (the Follow) last.
        var expected = authored.Reverse().ToArray();

        // Exactly the authored set — nothing more, nothing less (no duplicates, no extras), in order.
        Assert.Equal(expected, ids);

        // Each appears exactly once.
        foreach (var id in authored)
        {
            Assert.Single(ids, x => x == id);
        }
    }

    /// <summary>
    /// Reads the server-minted id of the <see cref="Note"/> embedded in the stored <see cref="Create"/> at
    /// <paramref name="createIri"/> (decision 055: the Create's embedded object gets its own minted id).
    /// </summary>
    private async Task<Iri> LearnEmbeddedNoteIriAsync(Iri createIri)
    {
        Assert.True(
            await _persistence.Activities.TryGetActivityAsync(createIri, out var stored),
            "The stored Create should be present to read its embedded note id.");
        var create = Assert.IsType<Create>(stored!);
        var note = create.Object?.FirstOrDefault();
        var noteIri = note?.ResolveObjectIri();
        Assert.NotNull(noteIri);
        return noteIri!.Value;
    }

    // --- The outbox collection (HTTP) agrees with the persistence-level outbox --------------
    //
    // The client reads the outbox over HTTP (GET /ap/v1/u/{handle}/outbox); the source of truth is the
    // same set. This pins that the HTTP collection and the persistence outbox agree (the raw-inspector
    // half of 19.6.2 is exercised live in the two-instance environment).

    [Fact]
    public async Task OutboxHttpCollection_MatchesTheAuthoredSet()
    {
        var follow = BuildFollow(_alice, _bob);
        var (_, followId) = await PostOutboxAsync(follow);
        var create = BuildCreate(_alice);
        var (_, createId) = await PostOutboxAsync(create);
        Assert.NotNull(followId);
        Assert.NotNull(createId);

        // The HTTP outbox collection (first page) lists both authored activities (under their
        // server-minted ids — decision 055).
        var response = await _http.GetAsync($"{_alice.Value.TrimEnd('/')}/outbox");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var ids = JsonDoc.ItemIdsOf(body);

        Assert.Contains(followId!.Value.Value, ids);
        Assert.Contains(createId!.Value.Value, ids);

        // And the persistence outbox agrees.
        var persisted = (await _persistence.Activities.GetOutboxAsync(_alice)).Select(a => a.Id).Cast<string>().ToList();
        Assert.Contains(followId!.Value.Value, persisted);
        Assert.Contains(createId!.Value.Value, persisted);
    }

    // --- The client's Announce/Unannounce publish to the outbox (19.6.1 management) -------
    //
    // The client's one-call boost/unboost (AnnounceAsync/UnannounceAsync) builds the deterministic
    // Announce / Undo-of-Announce and publishes them to the acting actor's own outbox through the signed
    // pipeline. The server records each in the outbox + activity store. This pins that the client's
    // boost is a first-class outbox-authored activity (no side channel) and that the Undo references the
    // exact Announce by its deterministic IRI.

    [Fact]
    public async Task ClientAnnounce_PublishesAnnounceToOutbox()
    {
        var objectId = new Iri($"https://{AHost}/objects/boost-target-{Guid.NewGuid():N}");
        var result = await _client.AnnounceAsync(_alice, objectId);

        Assert.True(result.IsSuccess, "the announce must be accepted");
        Assert.Equal((int)HttpStatusCode.Accepted, result.StatusCode);
        // Decision 055: the server mints the Announce's id ({actor}/announces/{ulid}) and returns it in
        // the 202 body; the client learns it via DeliveryResult.MintedId.
        Assert.True(result.MintedId != null, "the server should have minted the Announce's id and returned it.");
        var mintedIri = new Iri(result.MintedId!);

        // The Announce is recorded in alice's outbox + the activity store under its server-minted id.
        var outbox = (await _persistence.Activities.GetOutboxAsync(_alice)).ToList();
        var announce = Assert.Single(outbox, a => a.Id == mintedIri.Value);
        Assert.IsType<Announce>(announce);

        var stored = await _persistence.Activities.TryGetActivityAsync(mintedIri, out var activity);
        Assert.True(stored, "the Announce must be in the activity store.");
        Assert.NotNull(activity);
    }

    [Fact]
    public async Task ClientUnannounce_PublishesUndoOfAnnounceToOutbox()
    {
        var objectId = new Iri($"https://{AHost}/objects/boost-target-{Guid.NewGuid():N}");

        // First boost (the server mints the Announce's id, returned in DeliveryResult.MintedId) ...
        var announce = await _client.AnnounceAsync(_alice, objectId);
        Assert.True(announce.IsSuccess);
        Assert.True(announce.MintedId != null, "the server should have minted the Announce's id.");
        var announceIri = announce.MintedId!;

        // ... then unboost. The client's UnannounceAsync builds the Undo of the LEARNED announce id
        // (decision 055 learned-id references: the client passes the id it learned, never a recomputed
        // formula).
        var unannounce = await _client.UnannounceAsync(_alice, new Iri(announceIri));
        Assert.True(unannounce.IsSuccess, "the unannounce must be accepted");
        Assert.Equal((int)HttpStatusCode.Accepted, unannounce.StatusCode);
        Assert.True(unannounce.MintedId != null, "the server should have minted the Undo's id.");
        var undoIri = unannounce.MintedId!;

        // Both the Announce and the Undo-of-Announce are in the outbox (each under its server-minted id).
        var outbox = (await _persistence.Activities.GetOutboxAsync(_alice)).ToList();
        Assert.Contains(outbox, a => a.Id == announceIri);
        var undo = Assert.Single(outbox, a => a.Id == undoIri);
        Assert.IsType<Undo>(undo);

        // The Undo references the exact Announce by its LEARNED (server-minted) id.
        var undoActivity = (Undo)undo;
        var referencedObjectIri = undoActivity.Object?.FirstOrDefault().ResolveObjectIri();
        Assert.Equal(announceIri, referencedObjectIri?.Value);
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// POSTs <paramref name="activity"/> to the outbox endpoint, signed as alice, and returns the status
    /// code plus the server-minted id of the created activity (decision 055: the server is the sole id
    /// authority and returns the created activity — with its minted id — in the 202 body; this mirrors
    /// the real client's <c>DeliveryResult.MintedId</c> flow). The minted id is <c>null</c> when the
    /// response is not 2xx or carries no body.
    /// </summary>
    private async Task<(int Status, Iri? MintedId)> PostOutboxAsync(Activity activity)
    {
        using var request = SignedRequest(activity, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _http.SendAsync(request);
        var status = (int)response.StatusCode;
        Iri? mintedId = null;
        if (response.IsSuccessStatusCode && response.Content is { } respContent)
        {
            var body = await respContent.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                var created = ActivityJson.Deserialize<Activity>(body);
                if (created?.Id is { Length: > 0 } id)
                {
                    mintedId = new Iri(id);
                }
            }
        }

        return (status, mintedId);
    }

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as alice POSTing <paramref name="activity"/> to
    /// <paramref name="path"/> on the author's outbox. Uses the client pipeline (via a capture handler)
    /// to produce a correctly signed request, then replays the signed headers onto a fresh request for
    /// delivery to the TestServer.
    /// </summary>
    private HttpRequestMessage SignedRequest(Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClient(capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{AHost}{path}")
                    {
                        Content = signedContent,
                    },
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            response.Dispose();
        }

        var captured = capture.Captured!;
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{AHost}{path}")
        {
            Content = content,
        };
        foreach (var (name, values) in captured.Headers)
        {
            if (string.Equals(name, "content-type", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "date", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in values)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (captured.Headers.TryGetValue("date", out var dateValues))
        {
            foreach (var value in dateValues)
            {
                request.Headers.TryAddWithoutValidation("date", value);
            }
        }

        return request;
    }

    /// <summary>
    /// Publishes the AP-native follow decision (the <see cref="Accept"/> or <see cref="Reject"/>) for
    /// <paramref name="follow"/> to alice's outbox, signed as alice, and returns the status code plus the
    /// server-minted id of the decision (decision 055: the decision's own id is minted by the server; the
    /// object references the inbound follow by its originator's id). <paramref name="accept"/> selects the
    /// accept half.
    /// </summary>
    private async Task<(int Status, Iri? MintedId)> DecisionAsync(Follow follow, bool accept)
    {
        Activity decision = accept
            ? FollowIris.BuildAccept(new IdMinter(), _alice, follow)
            : FollowIris.BuildReject(new IdMinter(), _alice, follow);
        return await PostOutboxAsync(decision);
    }

    /// <summary>
    /// Records the original Follow activity + the provisional follow edge (follower → followed) locally,
    /// as an inbound follow would.
    /// </summary>
    private async Task RecordProvisionalFollowAsync(Follow follow)
    {
        await _persistence.Activities.PutActivityAsync(follow);
        var follower = follow.Actor?.FirstOrDefault().ResolveObjectIri();
        var target = follow.Object?.FirstOrDefault().ResolveObjectIri();
        if (follower is { } f && target is { } t)
        {
            await _persistence.Follows.RecordFollowAsync(f, t);
        }
    }

    // Decision 055: every authoring build helper is id-less — the client sends the activity shape
    // WITHOUT an id (the activity's own id AND, for a Create, the embedded object's id); the server mints
    // both and returns the created activity in the 202 body. The test learns each minted id from the
    // response and threads the learned id into any reference-carrying follow-up (an Undo/Delete).

    private static Follow BuildFollow(Iri actor, Iri target) => new()
    {
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri(target.Value) }],
    };

    // An inbound (remote-authored) follow keeps its originator's id verbatim — this is NOT posted to the
    // outbox, it is stored directly (RecordProvisionalFollowAsync), so it keeps a deterministic id
    // (representing the remote actor's choice) so the Accept/Reject can reference it.
    private static Follow BuildRemoteFollow(Iri remote, Iri alice) => new()
    {
        Id = $"https://{RemoteHost}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(remote.Value) }],
        Object = [new Link { Href = new Uri(alice.Value) }],
    };

    private static Create BuildCreate(Iri actor)
    {
        return new Create
        {
            Actor = [new Link { Href = new Uri(actor.Value) }],
            Object =
            [
                new Note { Content = ["an outbox post"] },
            ],
        };
    }

    private static Like BuildLike(Iri actor) => new()
    {
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri($"https://{AHost}/objects/liked-{Guid.NewGuid():N}") }],
    };

    private static Announce BuildAnnounce(Iri actor) => new()
    {
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri($"https://{AHost}/objects/announced-{Guid.NewGuid():N}") }],
    };

    private static Block BuildBlock(Iri actor, Iri target) => new()
    {
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri(target.Value) }],
    };

    private static Flag BuildFlag(Iri actor, Iri target) => new()
    {
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri(target.Value) }],
    };

    private static Undo BuildUndo(Iri actor, Iri targetIri) => new()
    {
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    private static Delete BuildDelete(Iri actor, Iri objectIri) => new()
    {
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    private IActivityPubClient BuildClient(HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_aliceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(_alice, new Iri($"{_alice.Value}#key-1"));
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = _alice, EnableRetry = false },
            handler);
    }

    private static IActorDocumentFetcher BuildSelfFetcher(
        KeyPair key, Iri actorIri, Func<HttpMessageHandler> handlerFactory)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, new Iri($"{actorIri.Value}#key-1"));
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(handlerFactory));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public CapturedRequest? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = values.ToList();
            }

            if (request.Content is not null)
            {
                foreach (var (name, values) in request.Content.Headers)
                {
                    if (headers.TryGetValue(name, out var existing))
                    {
                        existing.AddRange(values);
                    }
                    else
                    {
                        headers[name] = values.ToList();
                    }
                }
            }

            Captured = new CapturedRequest(body, headers);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(byte[] Body, Dictionary<string, List<string>> Headers);
}
