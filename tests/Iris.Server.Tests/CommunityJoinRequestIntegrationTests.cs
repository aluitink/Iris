using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.5.2 end-to-end test: the remote-actor join request → accept flow. A community with
/// <c>manuallyApprovesMembers</c> set does NOT auto-grant membership on an inbound <c>Join</c>; instead
/// it records a pending join request. The community operator then publishes an <c>Accept</c> (which adds
/// the requesting actor as a member and removes the pending request) or a <c>Reject</c> (which removes the
/// pending request without granting membership). Communities without the flag retain the legacy
/// auto-grant behavior.
/// </summary>
public sealed class CommunityJoinRequestIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Community = "iris";
    private const string OpenCommunity = "open";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _communityKey;
    private readonly KeyPair _openCommunityKey;
    private readonly Iri _communityIri;
    private readonly Iri _openCommunityIri;
    private readonly Iri _aliceIri;
    private readonly Iri _bobIri;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _bobKey;
    private readonly string _base = $"https://{AHost}";

    public CommunityJoinRequestIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        // A hosts alice and bob (local actors with real signing keys) and two communities:
        // - iris: a Group with manuallyApprovesMembers set (gated join requests)
        // - open: a Group without the flag (auto-grant joins)
        var aliceSeeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        _aliceKey = aliceSeeded.Key;
        _aliceIri = aliceSeeded.ActorIri;

        var bobSeeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Bob);
        _bobKey = bobSeeded.Key;
        _bobIri = bobSeeded.ActorIri;

        // Seed the gated community (manuallyApprovesMembers = true).
        var gated = TestSeeder.SeedCommunityWithKey(_persistence, AHost, Community);
        _communityKey = gated.Key;
        _communityIri = gated.CommunityIri;
        SetManuallyApprovesMembers(_communityIri);

        // Seed the open community (no flag).
        var open = TestSeeder.SeedCommunityWithKey(_persistence, AHost, OpenCommunity);
        _openCommunityKey = open.Key;
        _openCommunityIri = open.CommunityIri;

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

    // --- Gated community: Join records a pending request, does NOT grant membership ----

    [Fact]
    public async Task Join_GatedCommunity_RecordsPendingRequest_DoesNotGrantMembership()
    {
        // Preconditions: alice is a local actor but NOT a member of the gated community.
        Assert.False(await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri));

        // Alice posts a signed Join to the community's inbox.
        var join = BuildJoinActivity(_aliceIri, _communityIri);
        using var request = SignedRequest(_aliceIri, _aliceKey, join, $"/ap/v1/c/{Community}/inbox");
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The Join was stored (signature validated) …
        Assert.True(await _persistence.Activities.TryGetActivityAsync(new Iri(join.Id!), out _),
            "the Join should be stored after the signature validated");

        // … and a pending join request is recorded for alice.
        Assert.True(
            await _persistence.Communities.HasJoinRequestAsync(_communityIri, _aliceIri),
            "a pending join request should be recorded for alice (manuallyApprovesMembers gate)");

        // … but alice is NOT a member (the gated community does not auto-grant).
        Assert.False(
            await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri),
            "alice should NOT be a member after a Join to a manuallyApprovesMembers community");

        // AP-native conformance: the Join activity is stored in the community's outbox (mirroring the
        // inbound-follow pattern), so its IRI is available for an Accept(joinIri)/Reject(joinIri) that
        // references the original activity.
        var outbox = await _persistence.Activities.GetOutboxAsync(_communityIri);
        var joinInOutbox = outbox.OfType<Activity>()
            .FirstOrDefault(a => a.Type?.Contains("Join") == true
                && a.Actor?.FirstOrDefault().ResolveObjectIri()?.Value == _aliceIri.Value);
        Assert.NotNull(joinInOutbox);
        Assert.NotNull(joinInOutbox!.Id);
    }

    // --- Gated community: Accept adds the member and removes the pending request --------

    [Fact]
    public async Task JoinThenAccept_GatedCommunity_AddsMemberAndRemovesPendingRequest()
    {
        // Step 1: Alice posts a signed Join → pending request recorded, no membership.
        var join = BuildJoinActivity(_aliceIri, _communityIri);
        using (var joinRequest = SignedRequest(_aliceIri, _aliceKey, join, $"/ap/v1/c/{Community}/inbox"))
        {
            var joinResponse = await _http.SendAsync(joinRequest);
            Assert.Equal(HttpStatusCode.Accepted, joinResponse.StatusCode);
        }

        Assert.True(
            await _persistence.Communities.HasJoinRequestAsync(_communityIri, _aliceIri),
            "precondition: pending join request should exist");
        Assert.False(
            await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri),
            "precondition: alice should NOT be a member yet");

        // Step 2: The community operator publishes an Accept (object = the Join) to the community's outbox.
        var accept = BuildDecisionActivity(AcceptType.Accept, _communityIri, new Iri(join.Id!));
        using var acceptRequest = SignedRequest(_communityIri, _communityKey, accept, $"/ap/v1/c/{Community}/outbox");
        var acceptResponse = await _http.SendAsync(acceptRequest);
        Assert.Equal(HttpStatusCode.Accepted, acceptResponse.StatusCode);

        // Alice is now a member …
        Assert.True(
            await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri),
            "alice should be a member after the community Accepts the join request");

        // … and the pending request is removed.
        Assert.False(
            await _persistence.Communities.HasJoinRequestAsync(_communityIri, _aliceIri),
            "the pending join request should be removed after the Accept");
    }

    // --- Gated community: Reject removes the pending request, no membership -------------

    [Fact]
    public async Task JoinThenReject_GatedCommunity_RemovesPendingRequest_NoMembership()
    {
        // Step 1: Alice posts a signed Join → pending request recorded, no membership.
        var join = BuildJoinActivity(_aliceIri, _communityIri);
        using (var joinRequest = SignedRequest(_aliceIri, _aliceKey, join, $"/ap/v1/c/{Community}/inbox"))
        {
            var joinResponse = await _http.SendAsync(joinRequest);
            Assert.Equal(HttpStatusCode.Accepted, joinResponse.StatusCode);
        }

        Assert.True(
            await _persistence.Communities.HasJoinRequestAsync(_communityIri, _aliceIri),
            "precondition: pending join request should exist");

        // Step 2: The community operator publishes a Reject (object = the Join) to the community's outbox.
        var reject = BuildDecisionActivity(AcceptType.Reject, _communityIri, new Iri(join.Id!));
        using var rejectRequest = SignedRequest(_communityIri, _communityKey, reject, $"/ap/v1/c/{Community}/outbox");
        var rejectResponse = await _http.SendAsync(rejectRequest);
        Assert.Equal(HttpStatusCode.Accepted, rejectResponse.StatusCode);

        // Alice is NOT a member …
        Assert.False(
            await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri),
            "alice should NOT be a member after the community Rejects the join request");

        // … and the pending request is removed.
        Assert.False(
            await _persistence.Communities.HasJoinRequestAsync(_communityIri, _aliceIri),
            "the pending join request should be removed after the Reject");
    }

    // --- AP-native: the Join activity IRI in the outbox is usable for Accept/Reject ------

    [Fact]
    public async Task Join_GatedCommunity_JoinActivityInOutbox_IsUsableForAccept()
    {
        // Step 1: Alice posts a signed Join → pending request recorded, no membership.
        var join = BuildJoinActivity(_aliceIri, _communityIri);
        using (var joinRequest = SignedRequest(_aliceIri, _aliceKey, join, $"/ap/v1/c/{Community}/inbox"))
        {
            var joinResponse = await _http.SendAsync(joinRequest);
            Assert.Equal(HttpStatusCode.Accepted, joinResponse.StatusCode);
        }

        // Step 2: Read the community's outbox and find the Join activity (AP-native: the operator
        // discovers the pending join request by reading the outbox, exactly as they would for an
        // inbound follow — no separate "join requests" store is needed for the activity IRI).
        var outbox = await _persistence.Activities.GetOutboxAsync(_communityIri);
        var joinInOutbox = outbox.OfType<Activity>()
            .FirstOrDefault(a => a.Type?.Contains("Join") == true
                && a.Actor?.FirstOrDefault().ResolveObjectIri()?.Value == _aliceIri.Value);
        Assert.NotNull(joinInOutbox);
        Assert.NotNull(joinInOutbox!.Id);

        var joinIriFromOutbox = new Iri(joinInOutbox.Id!);

        // Step 3: The community operator publishes an Accept referencing the join IRI from the outbox
        // (AP-native: the Accept's object is the Join activity's IRI, not a separate "join request" id).
        var accept = BuildDecisionActivity(AcceptType.Accept, _communityIri, joinIriFromOutbox);
        using var acceptRequest = SignedRequest(_communityIri, _communityKey, accept, $"/ap/v1/c/{Community}/outbox");
        var acceptResponse = await _http.SendAsync(acceptRequest);
        Assert.Equal(HttpStatusCode.Accepted, acceptResponse.StatusCode);

        // Alice is now a member …
        Assert.True(
            await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri),
            "alice should be a member after the community Accepts the join (by the outbox join IRI)");

        // … and the pending request is removed.
        Assert.False(
            await _persistence.Communities.HasJoinRequestAsync(_communityIri, _aliceIri),
            "the pending join request should be removed after the Accept");
    }

    // --- Open community: Join auto-grants membership (no flag) --------------------------

    [Fact]
    public async Task Join_OpenCommunity_AutoGrantsMembership_NoPendingRequest()
    {
        // Preconditions: alice is a local actor but NOT a member of the open community.
        Assert.False(await _persistence.Communities.IsMemberAsync(_openCommunityIri, _aliceIri));

        // Alice posts a signed Join to the open community's inbox.
        var join = BuildJoinActivity(_aliceIri, _openCommunityIri);
        using var request = SignedRequest(_aliceIri, _aliceKey, join, $"/ap/v1/c/{OpenCommunity}/inbox");
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The Join was stored (signature validated) …
        Assert.True(await _persistence.Activities.TryGetActivityAsync(new Iri(join.Id!), out _),
            "the Join should be stored after the signature validated");

        // … and alice is now a member (the open community auto-grants).
        Assert.True(
            await _persistence.Communities.IsMemberAsync(_openCommunityIri, _aliceIri),
            "alice should be a member after a Join to an open community (auto-grant)");

        // … and NO pending join request is recorded.
        Assert.False(
            await _persistence.Communities.HasJoinRequestAsync(_openCommunityIri, _aliceIri),
            "no pending join request should be recorded for an open community");
    }

    // --- Gated community: duplicate Join is idempotent (no duplicate pending request) ---

    [Fact]
    public async Task Join_GatedCommunity_DuplicateJoin_IsIdempotent()
    {
        // First Join: records a pending request.
        var join1 = BuildJoinActivity(_aliceIri, _communityIri);
        using (var request1 = SignedRequest(_aliceIri, _aliceKey, join1, $"/ap/v1/c/{Community}/inbox"))
        {
            Assert.Equal(HttpStatusCode.Accepted, (await _http.SendAsync(request1)).StatusCode);
        }

        Assert.True(
            await _persistence.Communities.HasJoinRequestAsync(_communityIri, _aliceIri),
            "precondition: pending join request should exist after first Join");

        // Second Join (duplicate): should not throw and should not create a duplicate.
        var join2 = BuildJoinActivity(_aliceIri, _communityIri);
        using var request2 = SignedRequest(_aliceIri, _aliceKey, join2, $"/ap/v1/c/{Community}/inbox");
        var response2 = await _http.SendAsync(request2);
        Assert.Equal(HttpStatusCode.Accepted, response2.StatusCode);

        // The pending request still exists (idempotent).
        Assert.True(
            await _persistence.Communities.HasJoinRequestAsync(_communityIri, _aliceIri),
            "the pending join request should still exist after a duplicate Join");

        // Alice still is NOT a member (the duplicate Join did not grant membership).
        Assert.False(
            await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri),
            "alice should still NOT be a member after a duplicate Join");
    }

    // --- AP-native settings: Add of the community document sets manuallyApprovesMembers ----

    [Fact]
    public async Task Add_CommunityDocument_WithManuallyApprovesMembers_SetsFlag()
    {
        // Precondition: the open community does NOT have the flag set.
        Assert.False(IsManuallyApprovingMembers(_openCommunityIri));

        // The community operator publishes an Add of its own document (with the flag set) to its outbox.
        var add = BuildSettingsAddActivity(_openCommunityIri, enabled: true);
        using var request = SignedRequest(_openCommunityIri, _openCommunityKey, add, $"/ap/v1/c/{OpenCommunity}/outbox");
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The flag is now set on the stored community.
        Assert.True(
            IsManuallyApprovingMembers(_openCommunityIri),
            "the manuallyApprovesMembers flag should be set after the Add");

        // The Add activity is stored in the community's outbox (AP-native: the settings change is
        // auditable in the outbox).
        var outbox = await _persistence.Activities.GetOutboxAsync(_openCommunityIri);
        var addInOutbox = outbox.OfType<Activity>()
            .FirstOrDefault(a => a.Type?.Contains("Add") == true
                && a.Actor?.FirstOrDefault().ResolveObjectIri()?.Value == _openCommunityIri.Value);
        Assert.NotNull(addInOutbox);
        Assert.NotNull(addInOutbox!.Id);
    }

    // --- AP-native settings: Remove of the community document clears manuallyApprovesMembers --

    [Fact]
    public async Task Remove_CommunityDocument_WithManuallyApprovesMembers_ClearsFlag()
    {
        // Precondition: the gated community HAS the flag set.
        Assert.True(IsManuallyApprovingMembers(_communityIri));

        // The community operator publishes a Remove of its own document (with the flag) to its outbox.
        var remove = BuildSettingsRemoveActivity(_communityIri, enabled: true);
        using var request = SignedRequest(_communityIri, _communityKey, remove, $"/ap/v1/c/{Community}/outbox");
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The flag is now cleared on the stored community.
        Assert.False(
            IsManuallyApprovingMembers(_communityIri),
            "the manuallyApprovesMembers flag should be cleared after the Remove");

        // The Remove activity is stored in the community's outbox (AP-native: the settings change is
        // auditable in the outbox).
        var outbox = await _persistence.Activities.GetOutboxAsync(_communityIri);
        var removeInOutbox = outbox.OfType<Activity>()
            .FirstOrDefault(a => a.Type?.Contains("Remove") == true
                && a.Actor?.FirstOrDefault().ResolveObjectIri()?.Value == _communityIri.Value);
        Assert.NotNull(removeInOutbox);
        Assert.NotNull(removeInOutbox!.Id);
    }

    // --- AP-native settings: after clearing the flag, Joins auto-grant membership -----------

    [Fact]
    public async Task Settings_ClearFlag_ThenJoin_AutoGrantsMembership()
    {
        // Step 1: Clear the flag on the gated community (it was set in the constructor).
        var remove = BuildSettingsRemoveActivity(_communityIri, enabled: true);
        using (var removeRequest = SignedRequest(_communityIri, _communityKey, remove, $"/ap/v1/c/{Community}/outbox"))
        {
            Assert.Equal(HttpStatusCode.Accepted, (await _http.SendAsync(removeRequest)).StatusCode);
        }

        Assert.False(IsManuallyApprovingMembers(_communityIri), "precondition: flag should be cleared");

        // Step 2: Alice posts a signed Join to the (now open) community's inbox.
        var join = BuildJoinActivity(_aliceIri, _communityIri);
        using var joinRequest = SignedRequest(_aliceIri, _aliceKey, join, $"/ap/v1/c/{Community}/inbox");
        var joinResponse = await _http.SendAsync(joinRequest);
        Assert.Equal(HttpStatusCode.Accepted, joinResponse.StatusCode);

        // Alice is now a member (the community is open, so the Join auto-grants).
        Assert.True(
            await _persistence.Communities.IsMemberAsync(_communityIri, _aliceIri),
            "alice should be a member after a Join to a community whose flag was just cleared");

        // … and NO pending join request is recorded.
        Assert.False(
            await _persistence.Communities.HasJoinRequestAsync(_communityIri, _aliceIri),
            "no pending join request should be recorded after the flag was cleared");
    }

    // --- AP-native settings: after setting the flag, Joins record a pending request ----------

    [Fact]
    public async Task Settings_SetFlag_ThenJoin_RecordsPendingRequest()
    {
        // Step 1: Set the flag on the open community (it was not set in the constructor).
        var add = BuildSettingsAddActivity(_openCommunityIri, enabled: true);
        using (var addRequest = SignedRequest(_openCommunityIri, _openCommunityKey, add, $"/ap/v1/c/{OpenCommunity}/outbox"))
        {
            Assert.Equal(HttpStatusCode.Accepted, (await _http.SendAsync(addRequest)).StatusCode);
        }

        Assert.True(IsManuallyApprovingMembers(_openCommunityIri), "precondition: flag should be set");

        // Step 2: Alice posts a signed Join to the (now gated) community's inbox.
        var join = BuildJoinActivity(_aliceIri, _openCommunityIri);
        using var joinRequest = SignedRequest(_aliceIri, _aliceKey, join, $"/ap/v1/c/{OpenCommunity}/inbox");
        var joinResponse = await _http.SendAsync(joinRequest);
        Assert.Equal(HttpStatusCode.Accepted, joinResponse.StatusCode);

        // Alice is NOT a member (the community is now gated, so the Join records a pending request).
        Assert.False(
            await _persistence.Communities.IsMemberAsync(_openCommunityIri, _aliceIri),
            "alice should NOT be a member after a Join to a community whose flag was just set");

        // … and a pending join request IS recorded.
        Assert.True(
            await _persistence.Communities.HasJoinRequestAsync(_openCommunityIri, _aliceIri),
            "a pending join request should be recorded after the flag was set");
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// Sets the <c>manuallyApprovesMembers</c> extension flag on the community's <c>ExtensionData</c>.
    /// </summary>
    private void SetManuallyApprovesMembers(Iri communityIri)
    {
        if (!_persistence.Communities.TryGetCommunityAsync(communityIri, out var community, CancellationToken.None).GetAwaiter().GetResult())
        {
            throw new InvalidOperationException("Community not found.");
        }

        community!.ExtensionData ??= new Dictionary<string, JsonElement>();
        community.ExtensionData[Iris.Server.ActivityPubServerConstants.ManuallyApprovesMembersExtensionName] =
            JsonDocument.Parse("true").RootElement.Clone();
        _persistence.Communities.PutCommunityAsync(community, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Builds a <see cref="Join"/> activity: actor = the joining actor, object = the joining actor
    /// (the Iris convention for membership primitives: the <c>object</c> is the member being added,
    /// the community is the recipient of the delivery).
    /// </summary>
    private static Join BuildJoinActivity(Iri actorIri, Iri communityIri)
    {
        var join = new Join
        {
            Id = $"{actorIri.Value}/join-{Guid.NewGuid():N}",
            Object = [new Link { Href = new Uri(actorIri.Value) }],
        };
        join.Actor = [new Link { Href = new Uri(actorIri.Value) }];
        return join;
    }

    /// <summary>
    /// Builds an <see cref="Accept"/> or <see cref="Reject"/> activity: actor = the community, object = the
    /// referenced Join's IRI.
    /// </summary>
    private static Activity BuildDecisionActivity(AcceptType type, Iri communityIri, Iri joinIri)
    {
        Activity activity = type == AcceptType.Accept
            ? new Accept
            {
                Id = $"{communityIri.Value}/accept-{Guid.NewGuid():N}",
                Object = [new Link { Href = new Uri(joinIri.Value) }],
            }
            : new Reject
            {
                Id = $"{communityIri.Value}/reject-{Guid.NewGuid():N}",
                Object = [new Link { Href = new Uri(joinIri.Value) }],
            };
        activity.Actor = [new Link { Href = new Uri(communityIri.Value) }];
        return activity;
    }

    /// <summary>
    /// Builds an <see cref="Add"/> of the community's own document (with the <c>manuallyApprovesMembers</c>
    /// extension set to <paramref name="enabled"/>), published to the community's outbox (AP-native
    /// settings change, change 217).
    /// </summary>
    private static Add BuildSettingsAddActivity(Iri communityIri, bool enabled)
    {
        return new Add
        {
            Id = $"{communityIri.Value}/add-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(communityIri.Value) }],
            Object =
            [
                new KristofferStrube.ActivityStreams.Object
                {
                    Id = communityIri.Value,
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        [Iris.Server.ActivityPubServerConstants.ManuallyApprovesMembersExtensionName] =
                            JsonSerializer.SerializeToElement(enabled),
                    },
                },
            ],
        };
    }

    /// <summary>
    /// Builds a <see cref="Remove"/> of the community's own document (with the <c>manuallyApprovesMembers</c>
    /// extension), published to the community's outbox (AP-native settings change, change 217).
    /// </summary>
    private static Remove BuildSettingsRemoveActivity(Iri communityIri, bool enabled)
    {
        return new Remove
        {
            Id = $"{communityIri.Value}/remove-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(communityIri.Value) }],
            Object =
            [
                new KristofferStrube.ActivityStreams.Object
                {
                    Id = communityIri.Value,
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        [Iris.Server.ActivityPubServerConstants.ManuallyApprovesMembersExtensionName] =
                            JsonSerializer.SerializeToElement(enabled),
                    },
                },
            ],
        };
    }

    /// <summary>
    /// Checks whether the stored community has the <c>manuallyApprovesMembers</c> flag set.
    /// </summary>
    private bool IsManuallyApprovingMembers(Iri communityIri)
    {
        if (!_persistence.Communities.TryGetCommunityAsync(communityIri, out var community, CancellationToken.None).GetAwaiter().GetResult())
        {
            return false;
        }

        return community!.ExtensionData is { } ext
            && ext.TryGetValue(Iris.Server.ActivityPubServerConstants.ManuallyApprovesMembersExtensionName, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    private enum AcceptType { Accept, Reject }

    /// <summary>
    /// Builds a signed <see cref="HttpRequestMessage"/> for the given activity, signed as
    /// <paramref name="actorIri"/> (key <paramref name="key"/>), for POST to <paramref name="path"/>.
    /// </summary>
    private HttpRequestMessage SignedRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClient(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"{_base}{path}")
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}{path}")
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
    /// OWN actor/community documents (so it can resolve the signing key from the actor's document when
    /// validating a signed activity posted to its own inbox).
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

            if (request.Content is { } contentHeaders)
            {
                foreach (var (name, values) in contentHeaders.Headers)
                {
                    headers[name] = values.ToList();
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
