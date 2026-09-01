using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Integration test for the community follow-decision endpoint (POST
/// <c>/ap/v1/c/{name}/follows/{followId}</c>, the community variant of the person operator
/// follow-accept/reject endpoint — change 151, here for 19.5.3 "reject/undo flows for inbound follows of
/// the community"). A community's operator accepts or rejects a follow that a remote actor made of a local
/// community. The endpoint is Basic-authenticated (the community's IRI is the credential seam, the same
/// validator as the person endpoints); the follow being decided on is the catch-all route value (the
/// absolute IRI of the original <c>Follow</c>, fetched from the local activity store), and an optional
/// trailing <c>/accept</c> selects acceptance (otherwise it is a rejection). For an accept the endpoint
/// builds the deterministic <see cref="Accept"/> (<see cref="FollowIris.BuildAccept"/>), ensures the
/// community's follower edge, and schedules delivery to the follower's inbox (so the remote finalizes its
/// edge); for a reject it builds the deterministic <see cref="Reject"/>
/// (<see cref="FollowIris.BuildReject"/>), removes the provisional edge, and schedules delivery (so the
/// remote removes its edge). The community's follower edge lives in the
/// <see cref="ICommunityStore"/>'s followers set (not the person <see cref="IFollowStore"/>); both record
/// the activity in the local activity store + the community's outbox (inspectable + idempotent).
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts bob (the instance actor — the host always seeds the
/// <c>Handle</c> actor, and bob's key provides the delivery fetcher self-loop) plus the managed community
/// iris (manually-approving) and delta (a second local community, to exercise the local-follower guard).
/// A remote alice (a IRI on b.domain.local, never hosted) follows iris; the provisional follower edge is
/// recorded in iris's followers set. The tests exercise the endpoint's status codes and its local-side
/// effects (activity + outbox + edge removal) — the cross-instance delivery of the Reject/Accept back to
/// alice is the person path's coverage in <see cref="Security.FederationSignatureIntegrationTests"/>.
/// </remarks>
public sealed class CommunityFollowDecisionEndpointIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Bob = "bob";
    private const string Iris = "iris";
    private const string Delta = "delta";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _irisIri;
    private readonly Iri _deltaIri;
    private readonly Iri _aliceActorIri;

    public CommunityFollowDecisionEndpointIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        // bob is the instance actor (the host seeds the Handle actor); iris is the managed community;
        // delta is a second local community (to exercise the local-follower guard).
        var bob = TestSeeder.SeedPersonWithKey(_persistence, AHost, Bob);
        var (irisKey, irisIri, _) = TestSeeder.SeedManuallyApprovingCommunityWithKey(_persistence, AHost, Iris);
        var delta = TestSeeder.SeedCommunityWithKey(_persistence, AHost, Delta);
        _irisIri = irisIri;
        _deltaIri = delta.CommunityIri;
        _aliceActorIri = new Iri($"https://{BHost}/ap/v1/u/alice");

        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(iri == _irisIri && username == Iris && password == "iris-password"));

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Bob,
            Persistence = _persistence,
            // Register the community's key so the outbound DeliveryWorker can sign as iris when it
            // server-delivers the Accept/Reject back to alice.
            CommunityKey = irisKey,
            CredentialValidator = credentialValidator,
            // The decision's delivery target (remote alice) is never fetched in this test — the fetcher is
            // a self-loop (the only actor document this instance serves is bob's) so the DeliveryWorker's
            // sharedInbox resolution is a no-op and the activity is queued to alice's inbox.
            Fetcher = BuildSelfFetcher(bob.Key, bob.ActorIri, () => _server!.CreateHandler()),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A Basic-authenticated operator accept is accepted, recorded, and ensures the edge ---

    [Fact]
    public async Task Accept_Authenticated_RemoteFollow_IsAcceptedAndRecordsEdgeAndAccept()
    {
        var follow = BuildFollow(_aliceActorIri, _irisIri);
        await RecordProvisionalFollowAsync(follow);

        var statusCode = await DecisionAsync(follow, accept: true, auth: $"{Iris}:iris-password");
        Assert.Equal((int)HttpStatusCode.Accepted, statusCode);

        // The Accept is recorded under its deterministic IRI in the activity store AND iris's outbox.
        var acceptIri = FollowIris.AcceptIri(_irisIri, follow);
        Assert.True(
            await _persistence.Activities.TryGetActivityAsync(acceptIri, out var stored),
            "The Accept should be recorded in the activity store under its deterministic IRI");
        Assert.IsType<Accept>(stored!);
        var outbox = await _persistence.Activities.GetOutboxAsync(_irisIri);
        Assert.Contains(outbox, a => a.Id == acceptIri.Value);

        // The community's follower edge (alice → iris) is recorded (ensured) in the followers set.
        Assert.True(
            await FollowerPresentAsync(_aliceActorIri),
            "After the accept, the community's follower edge (alice → iris) should be recorded");
    }

    // --- A re-accept of an already-accepted follow is idempotent (202, same Accept IRI) ------

    [Fact]
    public async Task Accept_AlreadyAccepted_IsIdempotent()
    {
        var follow = BuildFollow(_aliceActorIri, _irisIri);
        await RecordProvisionalFollowAsync(follow);

        Assert.Equal((int)HttpStatusCode.Accepted, await DecisionAsync(follow, accept: true, auth: $"{Iris}:iris-password"));

        // The edge is already present, so a re-accept is a no-op on the edge (the activity is stored under
        // the same deterministic IRI). The endpoint still reports success.
        Assert.Equal((int)HttpStatusCode.Accepted, await DecisionAsync(follow, accept: true, auth: $"{Iris}:iris-password"));
        Assert.True(await FollowerPresentAsync(_aliceActorIri));
    }

    // --- An unauthenticated accept is rejected (401) -----------------------------------------

    [Fact]
    public async Task Accept_Unauthenticated_Is401()
    {
        var follow = BuildFollow(_aliceActorIri, _irisIri);
        await RecordProvisionalFollowAsync(follow);

        var statusCode = await DecisionAsync(follow, accept: true, auth: null);
        Assert.Equal((int)HttpStatusCode.Unauthorized, statusCode);
        Assert.False(await _persistence.Activities.TryGetActivityAsync(FollowIris.AcceptIri(_irisIri, follow), out _));
    }

    // --- Accepting a follow whose target is not this community is a conflict (409) ----------

    [Fact]
    public async Task Accept_FollowTargetIsNotThisCommunity_Is409()
    {
        // alice follows delta (a different local community). Accepting it on iris's endpoint is a
        // conflict: an accept is always the followed side's decision about a follow made OF that actor.
        var follow = BuildFollow(_aliceActorIri, _deltaIri);
        await _persistence.Activities.PutActivityAsync(follow);
        await _persistence.Communities.AddFollowerAsync(_deltaIri, _aliceActorIri);

        var statusCode = await DecisionAsync(follow, accept: true, auth: $"{Iris}:iris-password");
        Assert.Equal((int)HttpStatusCode.Conflict, statusCode);
        Assert.False(await _persistence.Activities.TryGetActivityAsync(FollowIris.AcceptIri(_irisIri, follow), out _));
    }

    // --- Accepting a local follow (not a pending remote follow) is forbidden (403) ----------

    [Fact]
    public async Task Accept_LocalFollower_Is403()
    {
        // delta (a local community) following iris is a local relationship, not a pending remote follow:
        // a local un-follow is an Undo, not an Accept — the endpoint forbids it. (The local-follower check
        // covers a local community, not just a local person.)
        var follow = BuildFollow(_deltaIri, _irisIri);
        await _persistence.Activities.PutActivityAsync(follow);
        await _persistence.Communities.AddFollowerAsync(_irisIri, _deltaIri);

        var statusCode = await DecisionAsync(follow, accept: true, auth: $"{Iris}:iris-password");
        Assert.Equal((int)HttpStatusCode.Forbidden, statusCode);
        // The local edge is left untouched.
        Assert.True(await FollowerPresentAsync(_deltaIri));
    }

    // --- Accepting a follow that is not recorded locally is gone (410) -----------------------

    [Fact]
    public async Task Accept_NotRecordedLocally_Is410()
    {
        // The follow was never recorded locally (no activity in the store) → 410 Gone, nothing recorded.
        var follow = BuildFollow(_aliceActorIri, _irisIri);
        // (Intentionally no RecordProvisionalFollowAsync — the activity is absent from the store.)

        var statusCode = await DecisionAsync(follow, accept: true, auth: $"{Iris}:iris-password");
        Assert.Equal(410, statusCode);
        Assert.False(await _persistence.Activities.TryGetActivityAsync(FollowIris.AcceptIri(_irisIri, follow), out _));
    }

    // --- Rejects: the no-/accept half of the same endpoint -----------------------------------

    // --- A Basic-authenticated operator reject is accepted, recorded, and removes the edge ---

    [Fact]
    public async Task Reject_Authenticated_RemoteFollow_IsAcceptedAndRemovesEdge()
    {
        var follow = BuildFollow(_aliceActorIri, _irisIri);
        await RecordProvisionalFollowAsync(follow);

        var statusCode = await DecisionAsync(follow, accept: false, auth: $"{Iris}:iris-password");
        Assert.Equal((int)HttpStatusCode.Accepted, statusCode);

        // The Reject is recorded under its deterministic IRI in the activity store AND iris's outbox.
        var rejectIri = FollowIris.RejectIri(_irisIri, follow);
        Assert.True(
            await _persistence.Activities.TryGetActivityAsync(rejectIri, out var stored),
            "The Reject should be recorded in the activity store under its deterministic IRI");
        Assert.IsType<Reject>(stored!);
        var outbox = await _persistence.Activities.GetOutboxAsync(_irisIri);
        Assert.Contains(outbox, a => a.Id == rejectIri.Value);

        // The provisional follower edge (alice → iris) is removed from the followers set.
        Assert.False(
            await FollowerPresentAsync(_aliceActorIri),
            "After the reject, the provisional follower edge (alice → iris) should be removed");
    }

    // --- An unauthenticated reject is rejected (401) and nothing is recorded -----------------

    [Fact]
    public async Task Reject_Unauthenticated_Is401AndRecordsNothing()
    {
        var follow = BuildFollow(_aliceActorIri, _irisIri);
        await RecordProvisionalFollowAsync(follow);

        var statusCode = await DecisionAsync(follow, accept: false, auth: null);
        Assert.Equal((int)HttpStatusCode.Unauthorized, statusCode);

        // No Reject recorded, and the provisional edge survives.
        Assert.False(await _persistence.Activities.TryGetActivityAsync(FollowIris.RejectIri(_irisIri, follow), out _));
        Assert.True(await FollowerPresentAsync(_aliceActorIri));
    }

    // --- Rejecting a follow whose target is not this community is a conflict (409) ----------

    [Fact]
    public async Task Reject_FollowTargetIsNotThisCommunity_Is409()
    {
        // alice follows delta (a different local community). Rejecting it on iris's endpoint is a
        // conflict: a reject is always the followed side's decision about a follow made OF that actor.
        var follow = BuildFollow(_aliceActorIri, _deltaIri);
        await _persistence.Activities.PutActivityAsync(follow);
        await _persistence.Communities.AddFollowerAsync(_deltaIri, _aliceActorIri);

        var statusCode = await DecisionAsync(follow, accept: false, auth: $"{Iris}:iris-password");
        Assert.Equal((int)HttpStatusCode.Conflict, statusCode);
        Assert.False(await _persistence.Activities.TryGetActivityAsync(FollowIris.RejectIri(_irisIri, follow), out _));
    }

    // --- Rejecting a local follow (not a pending remote follow) is forbidden (403) ----------

    [Fact]
    public async Task Reject_LocalFollower_Is403()
    {
        // delta (a local community) following iris is a local relationship, not a pending remote follow:
        // a local un-follow is an Undo, not a Reject — the endpoint forbids it.
        var follow = BuildFollow(_deltaIri, _irisIri);
        await _persistence.Activities.PutActivityAsync(follow);
        await _persistence.Communities.AddFollowerAsync(_irisIri, _deltaIri);

        var statusCode = await DecisionAsync(follow, accept: false, auth: $"{Iris}:iris-password");
        Assert.Equal((int)HttpStatusCode.Forbidden, statusCode);
        // The local edge is left untouched.
        Assert.True(await FollowerPresentAsync(_deltaIri));
    }

    // --- Rejecting a follow that is not recorded locally is gone (410) -----------------------

    [Fact]
    public async Task Reject_NotRecordedLocally_Is410()
    {
        // The follow was never recorded locally (no provisional edge) → 410 Gone, nothing recorded.
        var follow = BuildFollow(_aliceActorIri, _irisIri);
        // (Intentionally no RecordProvisionalFollowAsync — the edge is absent.)

        var statusCode = await DecisionAsync(follow, accept: false, auth: $"{Iris}:iris-password");
        Assert.Equal(410, statusCode);
        Assert.False(await _persistence.Activities.TryGetActivityAsync(FollowIris.RejectIri(_irisIri, follow), out _));
    }

    // --- A re-reject of an already-rejected follow is idempotent (202, no new activity) -----

    [Fact]
    public async Task Reject_AlreadyRejected_IsIdempotent()
    {
        var follow = BuildFollow(_aliceActorIri, _irisIri);
        await RecordProvisionalFollowAsync(follow);

        Assert.Equal((int)HttpStatusCode.Accepted, await DecisionAsync(follow, accept: false, auth: $"{Iris}:iris-password"));

        // The edge is already gone, so a re-reject of the same follow is a no-op on the edge (the activity
        // is stored under the same deterministic IRI). The endpoint still reports success.
        Assert.Equal((int)HttpStatusCode.Accepted, await DecisionAsync(follow, accept: false, auth: $"{Iris}:iris-password"));
        Assert.False(await FollowerPresentAsync(_aliceActorIri));
    }

    // --- Helpers -----------------------------------------------------------------------------

    /// <summary>
    /// Reports whether <paramref name="followerIri"/> is in iris's followers set (the community's follower
    /// edge — the provisional relationship the operator is deciding on). The community store exposes the
    /// set (no point query), so this reads it and checks membership.
    /// </summary>
    private async Task<bool> FollowerPresentAsync(Iri followerIri)
    {
        var followers = await _persistence.Communities.GetFollowersAsync(_irisIri);
        return followers.Contains(followerIri);
    }

    /// <summary>
    /// Records the original Follow activity + the provisional follower edge (follower → community) in
    /// iris's followers set, as an inbound follow would (the activity is stored so it is inspectable; the
    /// edge is the provisional relationship the operator is deciding on).
    /// </summary>
    private async Task RecordProvisionalFollowAsync(Follow follow)
    {
        await _persistence.Activities.PutActivityAsync(follow);
        var follower = follow.Actor?.FirstOrDefault().ResolveObjectIri();
        if (follower is { } f)
        {
            await _persistence.Communities.AddFollowerAsync(_irisIri, f);
        }
    }

    /// <summary>
    /// Issues a Basic-authenticated community follow decision on the original <paramref name="follow"/> and
    /// returns the status code. <paramref name="accept"/> selects the accept half (a trailing
    /// <c>/accept</c> on the route); <paramref name="auth"/> is "user:pass" or null (no auth). The follow
    /// is identified by its IRI in the route (the handler fetches it from the local activity store).
    /// </summary>
    private async Task<int> DecisionAsync(Follow follow, bool accept, string? auth)
    {
        var suffix = accept ? "/accept" : string.Empty;
        var url = $"{_irisIri.Value.TrimEnd('/')}/follows/{follow.Id!}{suffix}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (auth is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth)));
        }

        using var response = await _http.SendAsync(request);
        return (int)response.StatusCode;
    }

    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Id = $"https://{BHost}/activities/follow-{Guid.NewGuid():N}",
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
