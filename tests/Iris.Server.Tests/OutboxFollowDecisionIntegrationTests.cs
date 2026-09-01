using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.0b (AP-native rework) integration test: the operator's manual follow decision (an
/// <see cref="Accept"/> or <see cref="Reject"/> of an inbound follow) is authored by the client and
/// published to the <em>followed</em> actor's own outbox (<c>POST /ap/v1/u/{handle}/outbox</c>) — the
/// AP-native write surface — instead of the legacy Basic-auth follow-decision endpoint. The client signs
/// the activity as the followed actor (the one deciding); the server records it in the actor's outbox +
/// activity store, applies the local follow-edge effect (an accept ensures the follower→actor edge; a
/// reject removes the provisional edge), and — for a remote follower — server-delivers the
/// <c>Accept</c>/<c>Reject</c> to the follower's inbox (signed as the followed actor).
/// </summary>
/// <remarks>
/// Topology: a single instance (b.domain.local) hosts bob (manually-approving); a remote alice (an IRI on
/// a.domain.local, never hosted) follows bob, recording the provisional edge. The decision is a
/// <em>signed outbox publish</em> (no Basic auth, no dedicated route — the original <c>Follow</c> is
/// referenced by IRI in the activity's <c>object</c>). The cross-instance delivery of the decision back to
/// alice is covered by the federation-signature tests; here we assert the local-side effects (activity +
/// outbox + edge) and the status codes.
/// </remarks>
public sealed class OutboxFollowDecisionIntegrationTests : IDisposable
{
    private const string BHost = "b.domain.local";
    private const string AHost = "a.domain.local";
    private const string Bob = "bob";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _bobActorIri;
    private readonly KeyPair _bobKey;
    private readonly Iri _aliceActorIri;

    public OutboxFollowDecisionIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        // bob is the manually-approving instance actor (a real key so the outbox publish is signed as bob).
        var bob = TestSeeder.SeedManuallyApprovingPersonWithKey(_persistence, BHost, Bob);
        _bobKey = bob.Key;
        _bobActorIri = bob.ActorIri;

        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/alice");

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _persistence,
            IdentityKeys = BuildIdentity(_bobKey, _bobActorIri),
            // The decision's delivery target (remote alice) is never fetched in this test — the fetcher
            // is a self-loop (the only actor document this instance serves is bob's) so the
            // DeliveryWorker's sharedInbox resolution is a no-op and the decision is queued to alice's
            // inbox (the cross-instance hop is not exercised here).
            Fetcher = BuildSelfFetcher(_bobKey, _bobActorIri, () => _server!.CreateHandler()),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A signed outbox Accept of a remote follow is accepted, recorded, and ensures the edge ---

    [Fact]
    public async Task OutboxAccept_RemoteFollow_IsAcceptedAndRecordsEdgeAndAccept()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        await RecordProvisionalFollowAsync(follow);

        var accept = BuildAccept(_bobActorIri, follow);
        var statusCode = await PublishToOutboxAsync(accept);
        Assert.Equal((int)HttpStatusCode.Accepted, statusCode);

        // The Accept is recorded under its deterministic IRI in the activity store AND bob's outbox.
        var acceptIri = FollowIris.AcceptIri(_bobActorIri, follow);
        Assert.True(
            await _persistence.Activities.TryGetActivityAsync(acceptIri, out var stored),
            "The Accept should be recorded in the activity store under its deterministic IRI");
        Assert.IsType<Accept>(stored!);
        var outbox = await _persistence.Activities.GetOutboxAsync(_bobActorIri);
        Assert.Contains(outbox, a => a.Id == acceptIri.Value);

        // The follow edge (alice → bob) is recorded (ensured).
        Assert.True(
            await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "After the accept, the follow edge (alice → bob) should be recorded");
    }

    // --- A re-accept of an already-accepted follow is idempotent (202, same Accept IRI) ---------

    [Fact]
    public async Task OutboxAccept_AlreadyAccepted_IsIdempotent()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        await RecordProvisionalFollowAsync(follow);

        var accept = BuildAccept(_bobActorIri, follow);
        Assert.Equal((int)HttpStatusCode.Accepted, await PublishToOutboxAsync(accept));

        // The edge is already present, so a re-accept is a no-op on the edge (the activity is stored
        // under the same deterministic IRI). The outbox publish still reports success.
        Assert.Equal((int)HttpStatusCode.Accepted, await PublishToOutboxAsync(accept));
        Assert.True(await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri));
    }

    // --- An unsigned (or wrong-actor) outbox decision is rejected (401) ------------------------

    [Fact]
    public async Task OutboxDecision_Unsigned_Is401()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        await RecordProvisionalFollowAsync(follow);

        var accept = BuildAccept(_bobActorIri, follow);
        var json = ActivityJson.Serialize(accept);
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{BHost}/ap/v1/u/{Bob}/outbox")
        {
            Content = content,
        };
        using var response = await _http.SendAsync(request);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)response.StatusCode);

        // No Accept recorded, and the provisional edge survives.
        Assert.False(await _persistence.Activities.TryGetActivityAsync(FollowIris.AcceptIri(_bobActorIri, follow), out _));
        Assert.True(await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri));
    }

    // --- A signed outbox Reject of a remote follow is accepted, recorded, and removes the edge --

    [Fact]
    public async Task OutboxReject_RemoteFollow_IsAcceptedAndRemovesEdge()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        await RecordProvisionalFollowAsync(follow);

        var reject = BuildReject(_bobActorIri, follow);
        var statusCode = await PublishToOutboxAsync(reject);
        Assert.Equal((int)HttpStatusCode.Accepted, statusCode);

        // The Reject is recorded under its deterministic IRI in the activity store AND bob's outbox.
        var rejectIri = FollowIris.RejectIri(_bobActorIri, follow);
        Assert.True(
            await _persistence.Activities.TryGetActivityAsync(rejectIri, out var stored),
            "The Reject should be recorded in the activity store under its deterministic IRI");
        Assert.IsType<Reject>(stored!);
        var outbox = await _persistence.Activities.GetOutboxAsync(_bobActorIri);
        Assert.Contains(outbox, a => a.Id == rejectIri.Value);

        // The provisional follow edge (alice → bob) is removed.
        Assert.False(
            await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "After the reject, the provisional follow edge (alice → bob) should be removed");
    }

    // --- A re-reject of an already-rejected follow is idempotent (202, no new activity) --------

    [Fact]
    public async Task OutboxReject_AlreadyRejected_IsIdempotent()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        await RecordProvisionalFollowAsync(follow);

        var reject = BuildReject(_bobActorIri, follow);
        Assert.Equal((int)HttpStatusCode.Accepted, await PublishToOutboxAsync(reject));

        // The edge is already gone, so a re-reject of the same follow is a no-op on the edge (the
        // activity is stored under the same deterministic IRI). The outbox publish still reports success.
        Assert.Equal((int)HttpStatusCode.Accepted, await PublishToOutboxAsync(reject));
        Assert.False(await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri));
    }

    // --- A decision whose referenced follow is unknown to this instance records the activity but
    //     applies no local edge effect (the outbox always records what is authored) ---------------

    [Fact]
    public async Task OutboxDecision_UnknownFollow_RecordsActivityButAppliesNoEdge()
    {
        // The follow was never recorded locally (no activity in the store, no provisional edge) → the
        // outbox still records the authored Reject (202 — the outbox's contract is to record what is
        // published) but cannot resolve a follow to decide on, so it applies no local edge effect and
        // has no recipient to deliver to (a no-op beyond the record).
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        // (Intentionally no RecordProvisionalFollowAsync — the activity + edge are absent.)

        var reject = BuildReject(_bobActorIri, follow);
        var statusCode = await PublishToOutboxAsync(reject);
        Assert.Equal((int)HttpStatusCode.Accepted, statusCode);

        // The authored Reject is recorded (the outbox records what it is published).
        Assert.True(
            await _persistence.Activities.TryGetActivityAsync(FollowIris.RejectIri(_bobActorIri, follow), out _),
            "The authored Reject should be recorded in the outbox even when the referenced follow is unknown");

        // No edge effect: there was no provisional edge to remove, and none is created.
        Assert.False(await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri));
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Records the original Follow activity + the provisional follow edge (follower → followed) locally,
    /// as an inbound follow would (the activity is stored so it is inspectable; the edge is the
    /// provisional relationship the operator is deciding).
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

    /// <summary>
    /// Publishes <paramref name="activity"/> to bob's outbox (<c>POST /ap/v1/u/{handle}/outbox</c>) signed
    /// as bob (the followed actor) via the client pipeline, and returns the status code. The capture
    /// handler produces a correctly-signed request, which is then replayed onto the TestServer.
    /// </summary>
    private async Task<int> PublishToOutboxAsync(Activity activity)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClient(_bobActorIri, _bobKey, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{BHost}/ap/v1/u/{Bob}/outbox")
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{BHost}/ap/v1/u/{Bob}/outbox")
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

        using var sent = await _http.SendAsync(request);
        return (int)sent.StatusCode;
    }

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    private static IActivityPubClient BuildClient(Iri actorIri, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);
    }

    private static IActorDocumentFetcher BuildSelfFetcher(
        KeyPair authorKey, Iri actorIri, Func<HttpMessageHandler> handlerFactory)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(handlerFactory));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    private static Accept BuildAccept(Iri actorIri, Follow follow) => new()
    {
        Id = FollowIris.AcceptIri(actorIri, follow).Value,
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(follow.Id!) }],
    };

    private static Reject BuildReject(Iri actorIri, Follow follow) => new()
    {
        Id = FollowIris.RejectIri(actorIri, follow).Value,
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(follow.Id!) }],
    };

    /// <summary>
    /// Captures a signed request (its body + headers) instead of forwarding it, so the signed body can be
    /// replayed through a plain <see cref="HttpClient"/>.
    /// </summary>
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
