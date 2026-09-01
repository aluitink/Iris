using Iris.Client;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;
using KeyPair = Iris.Core.Identity.KeyPair;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.6.1 end-to-end test: the client's one-call community-membership operations —
/// <see cref="IActivityPubClient.AddMemberAsync"/> and
/// <see cref="IActivityPubClient.RemoveMemberAsync"/> — are expressible as signed ActivityStream
/// activities (an <c>Add</c> / a <c>Remove</c>, actor = the community, delivered to the community's own
/// inbox through the signed pipeline), completing the 19.6.1 invariant that every management operation
/// is a one-call client method (no side channel). This is the client-side counterpart to
/// <see cref="CommunityMembershipManagementIntegrationTests"/> (which drives the same primitives over raw
/// signed HTTP).
/// </summary>
/// <remarks>
/// A community manages its own membership (19.5.2 self-management): the <c>Add</c>/<c>Remove</c>'s
/// <c>actor</c> is the recipient community, so the client is signed as the community (its key), and the
/// activity is delivered to <c>communityId.InboxOf()</c> — the community outbox publish endpoint accepts
/// only <c>Follow</c>/<c>Undo</c>/<c>Accept</c>/<c>Reject</c>, so membership edits go to the inbox. The
/// instance's <c>AddActivityHandler</c>/<c>RemoveActivityHandler</c> apply the gate (actor == recipient
/// community) and record the member.
/// </remarks>
public sealed class CommunityMembershipClientIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Community = "iris";

    private readonly TestServer _server;
    private readonly IActivityPubClient _client;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _communityIri;
    private readonly Iri _aliceIri;
    private readonly Iri _bobIri;

    public CommunityMembershipClientIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        // A hosts alice + bob (local actors, potential members) and the community iris (a Group with a
        // real signing key, so the client can sign as it).
        _aliceIri = TestSeeder.SeedPerson(_persistence, AHost, Alice);
        _bobIri = TestSeeder.SeedPerson(_persistence, AHost, Bob);
        var seeded = TestSeeder.SeedCommunityWithKey(_persistence, AHost, Community);
        _communityIri = seeded.CommunityIri;

        // The instance's fetcher reaches its OWN documents (so it can resolve the community's signing
        // key from its own Group document when validating a community-signed Add/Remove posted to its own
        // inbox).
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _persistence,
            ExtraLocalActors = [_bobIri],
            Fetcher = BuildSelfFetcher(_persistence, _aliceIri, () => _server!.CreateHandler()),
        });

        // A signed client signed AS THE COMMUNITY (its key): AddMemberAsync/RemoveMemberAsync set the
        // activity's actor to the community, so the signing identity must be the community for the
        // actor and the signature to agree.
        _client = BuildClient(_communityIri, seeded.Key, _server.CreateHandler());
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    // --- The client's AddMemberAsync adds the member ------------------------------------

    [Fact]
    public async Task AddMemberAsync_SignedAsCommunity_AddsMember()
    {
        // Preconditions: bob is a local actor but not yet a member.
        Assert.False(await _persistence.Communities.IsMemberAsync(_communityIri, _bobIri));

        var result = await _client.AddMemberAsync(_communityIri, _bobIri);
        Assert.True(result.IsSuccess, $"the community-signed Add must be accepted (got {result.StatusCode})");
        Assert.Equal(202, result.StatusCode);

        // The 19.5.2 gate passed (actor == community): bob is now a member.
        Assert.True(
            await _persistence.Communities.IsMemberAsync(_communityIri, _bobIri),
            "bob should be a member after the community-signed AddMemberAsync (19.5.2 self-management)");
    }

    // --- The client's RemoveMemberAsync removes the member ------------------------------

    [Fact]
    public async Task RemoveMemberAsync_SignedAsCommunity_RemovesMember()
    {
        // Seed bob as an existing member (as a prior AddMemberAsync would have recorded him).
        TestSeeder.AddMember(_persistence, _communityIri, _bobIri);
        Assert.True(await _persistence.Communities.IsMemberAsync(_communityIri, _bobIri));

        var result = await _client.RemoveMemberAsync(_communityIri, _bobIri);
        Assert.True(result.IsSuccess, $"the community-signed Remove must be accepted (got {result.StatusCode})");
        Assert.Equal(202, result.StatusCode);

        // The 19.5.2 gate passed (actor == community): bob is no longer a member.
        Assert.False(
            await _persistence.Communities.IsMemberAsync(_communityIri, _bobIri),
            "bob should no longer be a member after the community-signed RemoveMemberAsync (19.5.2 self-management)");
    }

    // --- The full round-trip: add then remove, each recorded as a stored activity --------

    [Fact]
    public async Task AddThenRemove_RoundTrip_EachOperationIsStoredAndMembershipToggles()
    {
        Assert.False(await _persistence.Communities.IsMemberAsync(_communityIri, _bobIri));

        Assert.True((await _client.AddMemberAsync(_communityIri, _bobIri)).IsSuccess);
        Assert.True(await _persistence.Communities.IsMemberAsync(_communityIri, _bobIri));

        Assert.True((await _client.RemoveMemberAsync(_communityIri, _bobIri)).IsSuccess);
        Assert.False(await _persistence.Communities.IsMemberAsync(_communityIri, _bobIri));
    }

    /// <summary>
    /// Builds a signed <see cref="IActivityPubClient"/> (signed as <paramref name="actorIri"/>, key
    /// <paramref name="key"/>) whose transport routes to <paramref name="handler"/>.
    /// </summary>
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

    /// <summary>
    /// Builds a self-referential <see cref="IActorDocumentFetcher"/>: the instance's fetcher reaches its
    /// OWN actor/community documents (so it can resolve the community's signing key from its own
    /// <c>Group</c> document when validating a community-signed activity posted to its own inbox).
    /// </summary>
    private static IActorDocumentFetcher BuildSelfFetcher(
        InMemoryPersistenceProvider persistence,
        Iri aliceIri,
        Func<HttpMessageHandler> transportFactory)
    {
        var aliceKey = KeyPairGenerator.GenerateRsa(new Iri($"{aliceIri.Value}#key-1"));
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(aliceIri, aliceKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = aliceIri, EnableRetry = false },
            new LazyHandler(transportFactory));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}
