 using System.Net;
 using System.Net.Http.Headers;
 using Iris.Client;
using Iris.Core;
using Iris.Server;
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
        // 1. Follow bob (a local recipient — no cross-instance hop).
        var follow = BuildFollow(_alice, _bob);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(follow));

        // 2. Create a note (alice has no followers, so no fan-out).
        var create = BuildCreate(_alice);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(create));

        // 3. Like a note.
        var like = BuildLike(_alice);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(like));

        // 4. Announce a note.
        var announce = BuildAnnounce(_alice);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(announce));

        // 5. Block bob (a local recipient).
        var block = BuildBlock(_alice, _bob);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(block));

        // 6. Undo the follow (un-follow bob).
        var undo = BuildUndo(_alice, follow);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(undo));

        // 7. Delete the created note. The note's Create stays in the outbox: this Create's IRI is not the
        //    deterministic sibling of the note, so the Delete's inverse-removal is a no-op — the point
        //    here is that the Delete itself is recorded as an authored activity.
        var note = create.Object!.First();
        var noteIri = note.ResolveObjectIri()
            ?? throw new InvalidOperationException("The created note must carry an IRI.");
        var delete = BuildDelete(_alice, noteIri);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(delete));

        // 8. Accept a remote follow of alice (published to alice's outbox; the instance applies the edge
        //    and server-delivers the Accept).
        var remoteFollow1 = BuildRemoteFollow(_remote, _alice);
        await RecordProvisionalFollowAsync(remoteFollow1);
        Assert.Equal((int)HttpStatusCode.Accepted, await DecisionAsync(remoteFollow1, accept: true));
        var accept = await _persistence.Activities
            .TryGetActivityAsync(FollowIris.AcceptIri(_alice, remoteFollow1), out var storedAccept)
            ? storedAccept!
            : throw new InvalidOperationException("The Accept should be recorded.");

        // 9. Reject a second remote follow of alice (published to alice's outbox; the instance removes the
        //    provisional edge and server-delivers the Reject).
        var remoteFollow2 = BuildRemoteFollow(_remote, _alice);
        await RecordProvisionalFollowAsync(remoteFollow2);
        Assert.Equal((int)HttpStatusCode.Accepted, await DecisionAsync(remoteFollow2, accept: false));
        var reject = await _persistence.Activities
            .TryGetActivityAsync(FollowIris.RejectIri(_alice, remoteFollow2), out var storedReject)
            ? storedReject!
            : throw new InvalidOperationException("The Reject should be recorded.");

        // 10. Flag bob (a moderation report; the instance records the flag edge locally).
        var flag = BuildFlag(_alice, _bob);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(flag));

        // 11. Undo the flag (un-flag bob; the instance removes the flag edge).
        var undoFlag = BuildUndo(_alice, flag);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(undoFlag));

        // 12. Undo the like (un-like the liked object; the instance removes the like edge).
        var undoLike = BuildUndo(_alice, like);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(undoLike));

        // 13. Undo the announce (un-boost the announced object; the instance removes the announce edge).
        var undoAnnounce = BuildUndo(_alice, announce);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(undoAnnounce));

        // 14. Undo the block (un-block bob; the instance removes the block edge).
        var undoBlock = BuildUndo(_alice, block);
        Assert.Equal((int)HttpStatusCode.Accepted, await PostOutboxAsync(undoBlock));

        // --- The outbox is the single source of truth: exactly the authored set, each once, in order.

        var outbox = (await _persistence.Activities.GetOutboxAsync(_alice)).ToList();
        var ids = outbox.Select(a => a.Id).ToList();

        // The authored set, in authoring order.
        var authored = new[]
        {
            follow.Id, create.Id, like.Id, announce.Id, block.Id, undo.Id, delete.Id,
            accept!.Id, reject!.Id, flag.Id, undoFlag.Id, undoLike.Id, undoAnnounce.Id, undoBlock.Id,
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

    // --- The outbox collection (HTTP) agrees with the persistence-level outbox --------------
    //
    // The client reads the outbox over HTTP (GET /ap/v1/u/{handle}/outbox); the source of truth is the
    // same set. This pins that the HTTP collection and the persistence outbox agree (the raw-inspector
    // half of 19.6.2 is exercised live in the two-instance environment).

    [Fact]
    public async Task OutboxHttpCollection_MatchesTheAuthoredSet()
    {
        var follow = BuildFollow(_alice, _bob);
        await PostOutboxAsync(follow);
        var create = BuildCreate(_alice);
        await PostOutboxAsync(create);

        // The HTTP outbox collection (first page) lists both authored activities.
        var response = await _http.GetAsync($"{_alice.Value.TrimEnd('/')}/outbox");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var ids = JsonDoc.ItemIdsOf(body);

        Assert.Contains(follow.Id, ids);
        Assert.Contains(create.Id, ids);

        // And the persistence outbox agrees.
        var persisted = (await _persistence.Activities.GetOutboxAsync(_alice)).Select(a => a.Id).ToList();
        Assert.Contains(follow.Id, persisted);
        Assert.Contains(create.Id, persisted);
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// POSTs <paramref name="activity"/> to the outbox endpoint, signed as alice, and returns the status
    /// code.
    /// </summary>
    private async Task<int> PostOutboxAsync(Activity activity)
    {
        using var request = SignedRequest(activity, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _http.SendAsync(request);
        return (int)response.StatusCode;
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
    /// Publishes the AP-native follow decision (the deterministic <see cref="Accept"/> or
    /// <see cref="Reject"/>) for <paramref name="follow"/> to alice's outbox, signed as alice, and returns
    /// the status code. <paramref name="accept"/> selects the accept half.
    /// </summary>
    private async Task<int> DecisionAsync(Follow follow, bool accept)
    {
        Activity decision = accept
            ? FollowIris.BuildAccept(_alice, follow)
            : FollowIris.BuildReject(_alice, follow);
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

    private static Follow BuildFollow(Iri actor, Iri target) => new()
    {
        Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri(target.Value) }],
    };

    private static Follow BuildRemoteFollow(Iri remote, Iri alice) => new()
    {
        Id = $"https://{RemoteHost}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(remote.Value) }],
        Object = [new Link { Href = new Uri(alice.Value) }],
    };

    private static Create BuildCreate(Iri actor)
    {
        var noteIri = $"https://{AHost}/objects/note-{Guid.NewGuid():N}";
        return new Create
        {
            Id = $"https://{AHost}/activities/create-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actor.Value) }],
            Object =
            [
                new Note { Id = noteIri, Content = ["an outbox post"] },
            ],
        };
    }

    private static Like BuildLike(Iri actor) => new()
    {
        Id = $"https://{AHost}/activities/like-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri($"https://{AHost}/objects/liked-{Guid.NewGuid():N}") }],
    };

    private static Announce BuildAnnounce(Iri actor) => new()
    {
        Id = $"https://{AHost}/activities/announce-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri($"https://{AHost}/objects/announced-{Guid.NewGuid():N}") }],
    };

    private static Block BuildBlock(Iri actor, Iri target) => new()
    {
        Id = $"https://{AHost}/activities/block-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri(target.Value) }],
    };

    private static Flag BuildFlag(Iri actor, Iri target) => new()
    {
        Id = $"https://{AHost}/activities/flag-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri(target.Value) }],
    };

    private static Undo BuildUndo(Iri actor, Activity target) => new()
    {
        Id = $"https://{AHost}/activities/undo-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actor.Value) }],
        Object = [new Link { Href = new Uri(target.Id!) }],
    };

    private static Delete BuildDelete(Iri actor, Iri objectIri) => new()
    {
        Id = $"https://{AHost}/activities/delete-{Guid.NewGuid():N}",
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
