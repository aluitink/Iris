using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Object = KristofferStrube.ActivityStreams.Object;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 22.6.2 integration tests: the typed <see cref="IrisDocumentExtensions"/> readers for the
/// settings-gate state — <c>manuallyApprovesFollowers</c> (person) and <c>manuallyApprovesMembers</c>
/// (community). These are the read half of the AP-native settings write path
/// (<see cref="IActivityPubClient.SetManuallyApprovesFollowersAsync"/> (221) and
/// <see cref="IActivityPubClient.SetManuallyApprovesMembersAsync"/> (217)): the server stores the gate on
/// the actor/community's <see cref="Object.ExtensionData"/> and advertises it verbatim on the public
/// document when set, so a client can read the approval policy from the fetched document alone (no
/// persistence access). When the gate is absent the reader returns <see langword="null"/>; a present-but-
/// disabled gate returns <see langword="false"/>.
/// </summary>
public sealed class SettingsGateReaderTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Community = "devs";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _aliceIri;
    private readonly Iri _communityIri;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _communityKey;

    public SettingsGateReaderTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        // A hosts alice (open — no follow gate) and the "devs" community (open — no member gate), both
        // with real signing keys so the settings write path can be exercised through the signed pipeline.
        var aliceSeeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        _aliceKey = aliceSeeded.Key;
        _aliceIri = aliceSeeded.ActorIri;

        var communitySeeded = TestSeeder.SeedCommunityWithKey(_persistence, AHost, Community);
        _communityKey = communitySeeded.Key;
        _communityIri = communitySeeded.CommunityIri;

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _persistence,
            Fetcher = BuildSelfFetcher(_persistence),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- Person gate: the client write round-trips through the reader -------------------------

    [Fact]
    public async Task PersonDoc_GetManuallyApprovesFollowers_ReflectsClientWrite()
    {
        // Open (no gate): the reader returns null on the fresh person document.
        var openDoc = await FetchDocumentAsync(_aliceIri);
        Assert.Null(openDoc.GetManuallyApprovesFollowers());

        // Enable the gate through the client (an Add of the actor's own document to its outbox).
        Assert.True(await CallSetManuallyApprovesFollowersAsync(_aliceIri, _aliceKey, enabled: true),
            "the Add should be accepted (202)");

        // The public person document now advertises the gate; the reader returns true.
        var gatedDoc = await FetchDocumentAsync(_aliceIri);
        Assert.True(gatedDoc.GetManuallyApprovesFollowers());

        // Disable the gate through the client (a Remove of the actor's own document).
        Assert.True(await CallSetManuallyApprovesFollowersAsync(_aliceIri, _aliceKey, enabled: false),
            "the Remove should be accepted (202)");

        // The gate is gone again: the reader returns null.
        var reopenedDoc = await FetchDocumentAsync(_aliceIri);
        Assert.Null(reopenedDoc.GetManuallyApprovesFollowers());
    }

    // --- Community gate: the client write round-trips through the reader ----------------------

    [Fact]
    public async Task CommunityDoc_GetManuallyApprovesMembers_ReflectsClientWrite()
    {
        // Open (no gate): the reader returns null on the fresh community document.
        var openDoc = await FetchDocumentAsync(_communityIri);
        Assert.Null(openDoc.GetManuallyApprovesMembers());

        // Enable the gate through the client (an Add of the community's own document to its outbox).
        Assert.True(await CallSetManuallyApprovesMembersAsync(_communityIri, _communityKey, enabled: true),
            "the Add should be accepted (202)");

        // The public community document now advertises the gate; the reader returns true.
        var gatedDoc = await FetchDocumentAsync(_communityIri);
        Assert.True(gatedDoc.GetManuallyApprovesMembers());

        // Disable the gate through the client (a Remove of the community's own document).
        Assert.True(await CallSetManuallyApprovesMembersAsync(_communityIri, _communityKey, enabled: false),
            "the Remove should be accepted (202)");

        // The gate is gone again: the reader returns null.
        var reopenedDoc = await FetchDocumentAsync(_communityIri);
        Assert.Null(reopenedDoc.GetManuallyApprovesMembers());
    }

    // --- Cross-symmetry: each reader reads only its own gate ----------------------------------

    [Fact]
    public async Task Gate_Readers_ReadOnlyTheirOwnTerm()
    {
        // Enable BOTH gates.
        Assert.True(await CallSetManuallyApprovesFollowersAsync(_aliceIri, _aliceKey, enabled: true));
        Assert.True(await CallSetManuallyApprovesMembersAsync(_communityIri, _communityKey, enabled: true));

        var personDoc = await FetchDocumentAsync(_aliceIri);
        var communityDoc = await FetchDocumentAsync(_communityIri);

        // The person document carries only the follow gate (the community gate is community-only).
        Assert.True(personDoc.GetManuallyApprovesFollowers());
        Assert.Null(personDoc.GetManuallyApprovesMembers());

        // The community document carries only the member gate (the follow gate is person-only).
        Assert.True(communityDoc.GetManuallyApprovesMembers());
        Assert.Null(communityDoc.GetManuallyApprovesFollowers());
    }

    // --- Absent-term safety: a bare object returns nulls, not throws --------------------------

    [Fact]
    public void Gate_Readers_BareObject_ReturnNull()
    {
        var bare = new Object { Id = "https://a.domain.local/ap/v1/u/x" };

        Assert.Null(bare.GetManuallyApprovesFollowers());
        Assert.Null(bare.GetManuallyApprovesMembers());
    }

    // --- Present-but-disabled: a JSON false gate reads as false (distinct from absent) --------

    [Fact]
    public void Gate_Readers_PresentButDisabled_ReturnFalse()
    {
        var doc = new Object { Id = "https://a.domain.local/ap/v1/u/x" };
        doc.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["manuallyApprovesFollowers"] = JsonDocument.Parse("false").RootElement.Clone(),
            ["manuallyApprovesMembers"] = JsonDocument.Parse("false").RootElement.Clone(),
        };

        Assert.False(doc.GetManuallyApprovesFollowers());
        Assert.False(doc.GetManuallyApprovesMembers());
    }

    // --- Helpers ------------------------------------------------------------------------

    private async Task<Object> FetchDocumentAsync(Iri iri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, iri.Value);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return ActivityJson.Deserialize<Object>(body)!;
    }

    /// <summary>
    /// Builds a signed <see cref="IActivityPubClient"/> (signed as <paramref name="actorIri"/>, key
    /// <paramref name="key"/>) and calls <see cref="IActivityPubClient.SetManuallyApprovesFollowersAsync"/>
    /// against the live server. Returns <see langword="true"/> when the delivery was accepted.
    /// </summary>
    private async Task<bool> CallSetManuallyApprovesFollowersAsync(Iri actorIri, KeyPair key, bool enabled)
    {
        IActivityPubClient client = BuildClient(actorIri, key, new LazyHandler(() => _server!.CreateHandler()));
        var result = await client.SetManuallyApprovesFollowersAsync(actorIri, enabled, CancellationToken.None);
        return result.IsSuccess;
    }

    /// <summary>
    /// Builds a signed <see cref="IActivityPubClient"/> (signed as <paramref name="communityIri"/>, key
    /// <paramref name="key"/>) and calls
    /// <see cref="IActivityPubClient.SetManuallyApprovesMembersAsync"/> against the live server. Returns
    /// <see langword="true"/> when the delivery was accepted.
    /// </summary>
    private async Task<bool> CallSetManuallyApprovesMembersAsync(Iri communityIri, KeyPair key, bool enabled)
    {
        IActivityPubClient client = BuildClient(communityIri, key, new LazyHandler(() => _server!.CreateHandler()));
        var result = await client.SetManuallyApprovesMembersAsync(communityIri, enabled, CancellationToken.None);
        return result.IsSuccess;
    }

    /// <summary>
    /// Builds a signed <see cref="IActivityPubClient"/> (signed as <paramref name="actorIri"/>, key
    /// <paramref name="key"/>) whose transport is the given <paramref name="handler"/>.
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
    /// OWN actor/community documents (so it can resolve the signing key from the document when validating
    /// a signed activity posted to its own outbox).
    /// </summary>
    private IActorDocumentFetcher BuildSelfFetcher(InMemoryPersistenceProvider persistence)
    {
        var aliceKey = KeyPairGenerator.GenerateRsa(new Iri($"{_aliceIri.Value}#key-fetch"));
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(_aliceIri, aliceKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = _aliceIri, EnableRetry = false },
            new LazyHandler(() => _server!.CreateHandler()));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}
