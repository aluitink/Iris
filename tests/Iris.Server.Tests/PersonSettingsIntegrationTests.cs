using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 22.6.1 end-to-end test: the AP-native person settings change. A person publishes an
/// <c>Add</c> (enable) or <c>Remove</c> (disable) of its OWN document carrying the
/// <c>manuallyApprovesFollowers</c> extension to its own outbox, and the server updates the stored
/// actor's <c>ExtensionData</c> so the <c>FollowActivityHandler</c> gate reflects the change on the next
/// inbound <c>Follow</c>. This closes the read/write asymmetry with the community's
/// <c>SetManuallyApprovesMembersAsync</c> (change 217): the client gains a symmetric
/// <see cref="IActivityPubClient.SetManuallyApprovesFollowersAsync"/>. When the gate is present the
/// public actor document advertises the <c>iris:settings</c> extension and includes <c>"settings"</c> in
/// <c>iris:capabilities</c> (22.6.1); when the gate is absent both are absent.
/// </summary>
public sealed class PersonSettingsIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _aliceIri;
    private readonly Iri _bobIri;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _bobKey;
    private readonly string _base = $"https://{AHost}";

    public PersonSettingsIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        // A hosts alice (open — no gate) and bob (open — no gate), both local actors with real signing keys.
        var aliceSeeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        _aliceKey = aliceSeeded.Key;
        _aliceIri = aliceSeeded.ActorIri;

        var bobSeeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Bob);
        _bobKey = bobSeeded.Key;
        _bobIri = bobSeeded.ActorIri;

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

    // --- Client SetManuallyApprovesFollowersAsync: enable sets the stored flag ----------------

    [Fact]
    public async Task Client_SetManuallyApprovesFollowers_Enabled_SetsStoredFlag()
    {
        // Preconditions: alice has no gate.
        Assert.False(IsManuallyApprovingFollowers(_aliceIri), "precondition: alice should have no gate");

        // Alice publishes a signed Add of her own document (via the client) to her own outbox.
        var result = await CallSetManuallyApprovesFollowersAsync(_aliceIri, _aliceKey, enabled: true);
        Assert.True(result, "the Add should be accepted (202)");

        // The stored actor's ExtensionData now carries manuallyApprovesFollowers = true.
        Assert.True(
            IsManuallyApprovingFollowers(_aliceIri),
            "alice's stored actor should have manuallyApprovesFollowers set after the Add");
    }

    // --- Client SetManuallyApprovesFollowersAsync: disable clears the stored flag -------------

    [Fact]
    public async Task Client_SetManuallyApprovesFollowers_Disabled_ClearsStoredFlag()
    {
        // Seed the gate directly, then clear it through the client.
        SetGateDirectly(_aliceIri);
        Assert.True(IsManuallyApprovingFollowers(_aliceIri), "precondition: alice should have the gate");

        var result = await CallSetManuallyApprovesFollowersAsync(_aliceIri, _aliceKey, enabled: false);
        Assert.True(result, "the Remove should be accepted (202)");

        Assert.False(
            IsManuallyApprovingFollowers(_aliceIri),
            "alice's stored actor should have manuallyApprovesFollowers cleared after the Remove");
    }

    // --- Public document: iris:settings advertised when gate present, absent when cleared -----

    [Fact]
    public async Task SetGate_ThenPublicDoc_AdvertisesSettingsExtension_AndClearing_RemovesIt()
    {
        const string SettingsTerm = "settings";
        const string CapabilitiesTerm = "capabilities";

        // Step 1: enable the gate through the client.
        Assert.True(await CallSetManuallyApprovesFollowersAsync(_aliceIri, _aliceKey, enabled: true));
        Assert.True(IsManuallyApprovingFollowers(_aliceIri), "the stored flag should be set after the client call");

        // The public actor document now advertises iris:settings (the outbox IRI) and includes
        // "settings" in iris:capabilities.
        using (var doc = await FetchPersonDocAsync(_aliceIri))
        {
            var settingsTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + SettingsTerm;
            Assert.True(
                doc.RootElement.TryGetProperty(settingsTerm, out var settings),
                $"the person document must advertise the {settingsTerm} extension after the gate is set");
            Assert.Equal(JsonValueKind.String, settings.ValueKind);
            Assert.Equal($"{_aliceIri.Value}/outbox", settings.GetString());

            var capabilitiesTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + CapabilitiesTerm;
            Assert.True(
                doc.RootElement.TryGetProperty(capabilitiesTerm, out var capabilities),
                "the person document must advertise the iris:capabilities extension");
            var values = capabilities.EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Contains(ActivityPubServerConstants.CapabilitySettings, values);
        }

        // Step 2: clear the gate through the client.
        Assert.True(await CallSetManuallyApprovesFollowersAsync(_aliceIri, _aliceKey, enabled: false));

        // The public actor document no longer advertises iris:settings and the capability is gone.
        using (var doc = await FetchPersonDocAsync(_aliceIri))
        {
            var settingsTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + SettingsTerm;
            Assert.False(
                doc.RootElement.TryGetProperty(settingsTerm, out _),
                "the person document must NOT advertise iris:settings after the gate is cleared");

            var capabilitiesTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + CapabilitiesTerm;
            Assert.True(
                doc.RootElement.TryGetProperty(capabilitiesTerm, out var capabilities),
                "the person document must advertise the iris:capabilities extension");
            var values = capabilities.EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.DoesNotContain(ActivityPubServerConstants.CapabilitySettings, values);
        }
    }

    // --- The gate actually gates inbound Follows ----------------------------------------------

    [Fact]
    public async Task SetGate_ThenInboundFollow_SurfacesInOutbox_WithoutAutoAccept()
    {
        // Step 1: alice enables her gate.
        Assert.True(await CallSetManuallyApprovesFollowersAsync(_aliceIri, _aliceKey, enabled: true));

        // Step 2: bob follows alice (signed, to alice's inbox).
        var follow = BuildFollowActivity(_bobIri, _aliceIri);
        using var followRequest = SignedRequest(_bobIri, _bobKey, follow, $"/ap/v1/u/{Alice}/inbox");
        var followResponse = await _http.SendAsync(followRequest);
        Assert.Equal(HttpStatusCode.Accepted, followResponse.StatusCode);

        // The gated inbound follow is surfaced in alice's own outbox (the "Inbound follows" surface) so
        // she can Accept/Reject it.
        var outbox = await _persistence.Activities.GetOutboxAsync(_aliceIri);
        var followInOutbox = outbox.OfType<Activity>()
            .FirstOrDefault(a => a is Follow f && f.Actor?.FirstOrDefault().ResolveObjectIri()?.Value == _bobIri.Value);
        Assert.NotNull(followInOutbox); // the gated inbound Follow should be surfaced in alice's outbox

        // And — the point of the gate — NO auto-Accept was recorded for it (only an explicit operator
        // Accept would create an Accept activity referencing the follow).
        var autoAccept = outbox.OfType<Activity>()
            .FirstOrDefault(a => a is Accept accept
                && accept.Actor?.FirstOrDefault().ResolveObjectIri()?.Value == _aliceIri.Value
                && accept.Object?.FirstOrDefault().ResolveObjectIri()?.Value == followInOutbox!.Id);
        Assert.Null(autoAccept); // a manuallyApprovesFollowers person must NOT auto-accept an inbound Follow
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// Builds a signed <see cref="IActivityPubClient"/> (signed as <paramref name="actorIri"/>, key
    /// <paramref name="key"/>) and calls <see cref="IActivityPubClient.SetManuallyApprovesFollowersAsync"/>
    /// against the live server. Returns <see langword="true"/> when the delivery was accepted.
    /// </summary>
    private async Task<bool> CallSetManuallyApprovesFollowersAsync(Iri actorIri, KeyPair key, bool enabled)
    {
        var capture = new CaptureHandler();
        IActivityPubClient client = BuildClient(actorIri, key, new LazyHandler(() => _server!.CreateHandler()));
        var result = await client.SetManuallyApprovesFollowersAsync(actorIri, enabled, CancellationToken.None);
        return result.IsSuccess;
    }

    /// <summary>
    /// Sets the <c>manuallyApprovesFollowers</c> extension flag directly on the stored actor's
    /// <c>ExtensionData</c> (bypassing the wire, for test setup).
    /// </summary>
    private void SetGateDirectly(Iri actorIri)
    {
        if (!_persistence.Actors.TryGetActorAsync(actorIri, out var actor, CancellationToken.None).GetAwaiter().GetResult())
        {
            throw new InvalidOperationException("Actor not found.");
        }

        actor!.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData[ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName] =
            JsonDocument.Parse("true").RootElement.Clone();
        _persistence.Actors.PutActorAsync(actor, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Checks whether the stored actor has the <c>manuallyApprovesFollowers</c> flag set.
    /// </summary>
    private bool IsManuallyApprovingFollowers(Iri actorIri)
    {
        if (!_persistence.Actors.TryGetActorAsync(actorIri, out var actor, CancellationToken.None).GetAwaiter().GetResult())
        {
            return false;
        }

        return actor!.ExtensionData is { } ext
            && ext.TryGetValue(ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    private async Task<JsonDocument> FetchPersonDocAsync(Iri actorIri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, actorIri.Value);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Builds a <see cref="Follow"/> activity: actor = the follower, object = the followed actor.
    /// </summary>
    private static Follow BuildFollowActivity(Iri followerIri, Iri targetIri)
    {
        return new Follow
        {
            Id = $"{followerIri.Value}/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(followerIri.Value) }],
            Object = [new Link { Href = new Uri(targetIri.Value) }],
        };
    }

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
