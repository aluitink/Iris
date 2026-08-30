using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Integration test for the operator follow-rejection endpoint
/// (POST <c>/ap/v1/u/{handle}/follows/{followId}</c>, the live outbound half of the
/// manually-approves-followers gate — J-10 / gap G-2's Reject half). A local actor's operator (or the
/// actor's own client) rejects a follow that a remote actor made of a <em>manually-approving</em> local
/// actor. The endpoint is Basic-authenticated (the acting actor's credentials, the same seam as the
/// mute/relay endpoints), takes the original <c>Follow</c> as its body, builds the deterministic
/// <see cref="Reject"/> (<see cref="FollowIris.BuildReject"/>), records it in the local activity store +
/// the local actor's outbox, removes the provisional follow edge (follower → local actor), and schedules
/// delivery of the Reject to the follower's inbox (signed as the local actor).
/// </summary>
/// <remarks>
/// Topology: a single instance (b.domain.local) hosts bob (the manually-approving instance actor) plus
/// carol (a second local actor, to exercise the local-follower guard). A remote alice (a IRI on
/// a.domain.local, never hosted) follows bob; the provisional edge is recorded in the follow store. The
/// tests exercise the endpoint's status codes and its local-side effects (activity + outbox + edge
/// removal) — the cross-instance delivery of the Reject back to alice is covered by
/// <see cref="Security.FederationSignatureIntegrationTests"/>.
/// </remarks>
public sealed class OperatorRejectEndpointIntegrationTests : IDisposable
{
    private const string BHost = "b.domain.local";
    private const string AHost = "a.domain.local";
    private const string Bob = "bob";
    private const string Carol = "carol";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _bobActorIri;
    private readonly Iri _carolActorIri;
    private readonly Iri _aliceActorIri;

    public OperatorRejectEndpointIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        // bob is the manually-approving instance actor; carol is a second local actor.
        var bob = TestSeeder.SeedManuallyApprovingPerson(_persistence, BHost, Bob);
        var carol = TestSeeder.SeedPersonWithKey(_persistence, BHost, Carol);
        _bobActorIri = bob;
        _carolActorIri = carol.ActorIri;
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/alice");

        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(iri == _bobActorIri && username == Bob && password == "bob-password"));

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _persistence,
            ExtraLocalActors = [carol.ActorIri],
            CredentialValidator = credentialValidator,
            // The Reject's delivery target (remote alice) is never fetched in this test — the fetcher is
            // a self-loop (the only actor document this instance serves is bob's/carol's) so the
            // DeliveryWorker's sharedInbox resolution is a no-op and the reject is queued to alice's inbox.
            Fetcher = BuildSelfFetcher(carol.Key, carol.ActorIri, () => _server!.CreateHandler()),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A Basic-authenticated operator reject is accepted, recorded, and removes the edge ---

    [Fact]
    public async Task Reject_Authenticated_RemoteFollow_IsAcceptedAndRemovesEdge()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        await RecordProvisionalFollowAsync(follow);

        var statusCode = await RejectAsync(follow, auth: $"{Bob}:bob-password");
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

    // --- An unauthenticated reject is rejected (401) and nothing is recorded -------------

    [Fact]
    public async Task Reject_Unauthenticated_Is401AndRecordsNothing()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        await RecordProvisionalFollowAsync(follow);

        var statusCode = await RejectAsync(follow, auth: null);
        Assert.Equal((int)HttpStatusCode.Unauthorized, statusCode);

        // No Reject recorded, and the provisional edge survives.
        Assert.False(await _persistence.Activities.TryGetActivityAsync(FollowIris.RejectIri(_bobActorIri, follow), out _));
        Assert.True(await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri));
    }

    // --- Rejecting a follow whose target is not this actor is a conflict (409) -----------

    [Fact]
    public async Task Reject_FollowTargetIsNotThisActor_Is409()
    {
        // alice follows carol (a different local actor). Rejecting it on bob's endpoint is a conflict:
        // a reject is always the followed side's decision about a follow made OF that actor.
        var follow = BuildFollow(_aliceActorIri, _carolActorIri);
        await _persistence.Follows.RecordFollowAsync(_aliceActorIri, _carolActorIri);

        var statusCode = await RejectAsync(follow, auth: $"{Bob}:bob-password");
        Assert.Equal((int)HttpStatusCode.Conflict, statusCode);
        Assert.False(await _persistence.Activities.TryGetActivityAsync(FollowIris.RejectIri(_bobActorIri, follow), out _));
    }

    // --- Rejecting a local follow (not a pending remote follow) is forbidden (403) --------

    [Fact]
    public async Task Reject_LocalFollower_Is403()
    {
        // carol (a local actor) following bob is a local relationship, not a pending remote follow: a
        // local un-follow is an Undo, not a Reject — the endpoint forbids it.
        var follow = BuildFollow(_carolActorIri, _bobActorIri);
        await _persistence.Follows.RecordFollowAsync(_carolActorIri, _bobActorIri);

        var statusCode = await RejectAsync(follow, auth: $"{Bob}:bob-password");
        Assert.Equal((int)HttpStatusCode.Forbidden, statusCode);
        // The local edge is left untouched.
        Assert.True(await _persistence.Follows.IsFollowingAsync(_carolActorIri, _bobActorIri));
    }

    // --- Rejecting a follow that is not recorded locally is gone (410) --------------------

    [Fact]
    public async Task Reject_NotRecordedLocally_Is410()
    {
        // The follow was never recorded locally (no provisional edge) → 410 Gone, nothing recorded.
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        // (Intentionally no RecordFollowAsync — the edge is absent.)

        var statusCode = await RejectAsync(follow, auth: $"{Bob}:bob-password");
        Assert.Equal(410, statusCode);
        Assert.False(await _persistence.Activities.TryGetActivityAsync(FollowIris.RejectIri(_bobActorIri, follow), out _));
    }

    // --- A re-reject of an already-rejected follow is idempotent (202, no new activity) ---

    [Fact]
    public async Task Reject_AlreadyRejected_IsIdempotent()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        await RecordProvisionalFollowAsync(follow);

        Assert.Equal((int)HttpStatusCode.Accepted, await RejectAsync(follow, auth: $"{Bob}:bob-password"));

        // The edge is already gone, so a re-reject of the same follow is a no-op on the edge (the
        // activity is stored under the same deterministic IRI). The endpoint still reports success.
        Assert.Equal((int)HttpStatusCode.Accepted, await RejectAsync(follow, auth: $"{Bob}:bob-password"));
        Assert.False(await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri));
    }

    // --- A malformed body (not a Follow / no id) is a bad request (400) -------------------

    [Fact]
    public async Task Reject_NotAFollow_Is400()
    {
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        await RecordProvisionalFollowAsync(follow);

        // A body that deserializes to a non-Follow (a Note) is a bad request.
        var statusCode = await RawRejectAsync(_bobActorIri, ActivityJson.Serialize(new Note
        {
            Id = "https://b.domain.local/ap/v1/u/bob/notes/1",
            Content = ["not a follow"],
        }), auth: $"{Bob}:bob-password");
        Assert.Equal((int)HttpStatusCode.BadRequest, statusCode);
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Records the original Follow activity + the provisional follow edge (follower → followed) locally,
    /// as an inbound follow would (the activity is stored so it is inspectable; the edge is the
    /// provisional relationship the operator is rejecting).
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
    /// Issues a Basic-authenticated POST of the original <paramref name="follow"/> to bob's reject
    /// endpoint and returns the status code. <paramref name="auth"/> is "user:pass" or null (no auth).
    /// </summary>
    private async Task<int> RejectAsync(Follow follow, string? auth)
        => await RawRejectAsync(_bobActorIri, ActivityJson.Serialize(follow), auth, followId: follow.Id!);

    /// <summary>
    /// Issues a Basic-authenticated POST of the given JSON body to the given actor's reject endpoint and
    /// returns the status code. <paramref name="auth"/> is "user:pass" or null. The route's <c>{followId}</c>
    /// catch-all carries the follow's IRI (for a real follow); a non-follow body (the 400 path) uses a
    /// placeholder — the handler reads the follow from the body, not the route.
    /// </summary>
    private async Task<int> RawRejectAsync(Iri actorIri, string json, string? auth, string followId = "0")
    {
        var url = $"{actorIri.Value.TrimEnd('/')}/follows/{followId}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (auth is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth)));
        }

        request.Content = new StringContent(json);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/activity+json");

        using var response = await _http.SendAsync(request);
        return (int)response.StatusCode;
    }

    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

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
}
