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
/// Phase 12 Slice 12.18 integration test (F-06 relay — <strong>relay subscription</strong>): a local,
/// Iris-specific decision. A relay (a <c>star</c>-subscribed fan-out server, ActivityPub §5.1.3) is not
/// an activity an actor receives — it is a remote server a local actor points at, so a relay subscription
/// is not interpreted from an inbox POST: it is a Basic-authenticated request to the acting actor's own
/// instance. The actor's <c>relays</c> collection (the <c>star</c> set) is served over the wire (an
/// <c>OrderedCollection</c> of relay links, advertised on the actor document via the <c>star</c>
/// extension), a subscription is recorded, and an un-subscribe removes it. (Relay fan-out — actually
/// delivering the actor's content to the subscribed relays — is the follow-up slice.)
/// </summary>
/// <remarks>
/// Topology: a single instance (b.domain.local) hosts a local actor — <c>bob</c> (the instance's Handle
/// actor) — who subscribes to a remote relay (relay1.example.com). Bob subscribes via a Basic-authenticated
/// <c>POST /ap/v1/u/bob/relays/{relay}</c>; the instance authenticates bob (the
/// <c>IActorCredentialValidator</c>) and records the relay edge. Bob's <c>/relays</c> collection (a
/// public read endpoint) serves the edge, and the actor document advertises the <c>star</c> set (pointing
/// at <c>/relays</c>). Un-subscribing (<c>?unsubscribe=true</c>) removes the edge.
/// </remarks>
public sealed class RelaysCollectionIntegrationTests : IDisposable
{
    private const string BHost = "b.domain.local";
    private const string Bob = "bob";
    private const string RelayIri = "https://relay1.example.com";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _bobActorIri;
    private readonly Iri _relayIri;
    private readonly IActivityPubClient _client;

    public RelaysCollectionIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var bob = TestSeeder.SeedPersonWithKey(_persistence, BHost, Bob);
        _bobActorIri = bob.ActorIri;
        _relayIri = new Iri(RelayIri);

        // A Basic-auth credential validator: bob's credentials are ("bob", "bob-password") for bob's IRI.
        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(
                iri == _bobActorIri && username == Bob && password == "bob-password"));

        // Bob is the instance actor (Handle). The client is a Basic-authenticated local-decision client
        // (a relay subscription is not a signed inbox delivery — it is a Basic-authenticated POST to
        // bob's own instance). The fetcher is wired to the in-process TestServer (the actor document is
        // readable).
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _persistence,
            CredentialValidator = credentialValidator,
            Fetcher = BuildSelfFetcher(bob.Key, bob.ActorIri, () => _server!.CreateHandler()),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
        _client = BuildLocalClient(_bobActorIri, bob.Key, () => _server.CreateHandler(),
            new ProxyCredentials(Bob, "bob-password"));
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
        _server.Dispose();
    }

    // --- The actor document advertises the relays (star) collection ---------------------

    [Fact]
    public async Task ActorDocument_AdvertisesRelaysCollection()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The actor document advertises the relays (star) collection link (via the ActivityStreams
        // extension data, since the library's Person has no typed `star` property).
        Assert.Equal($"https://{BHost}/ap/v1/u/{Bob}/relays", doc.RootElement.GetProperty("star").GetString());
    }

    // --- The relays collection is an empty OrderedCollection before any subscription ----

    [Fact]
    public async Task RelaysCollection_Empty_IsOrderedCollection()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/relays");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"https://{BHost}/ap/v1/u/{Bob}/relays", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- An authenticated subscription is recorded and appears in the relays collection --

    [Fact]
    public async Task SubscribeRelay_Authenticated_RecordsRelayEdge()
    {
        var statusCode = await _client.SubscribeRelayAsync(_bobActorIri, _relayIri);
        Assert.Equal(204, statusCode);

        // The instance authenticated bob (Basic auth) and recorded the relay edge (bob → relay).
        Assert.True(await _persistence.Relays.IsRelayAsync(_bobActorIri, _relayIri));
        Assert.Contains(_relayIri, await _persistence.Relays.GetRelaysAsync(_bobActorIri));
    }

    // --- The subscriber's own /relays collection serves the recorded edge ----------------

    [Fact]
    public async Task SubscribeRelay_AppearsInRelaysCollection()
    {
        await _client.SubscribeRelayAsync(_bobActorIri, _relayIri);

        // Bob's /relays collection (a public read endpoint) serves the recorded edge (as a link to the
        // relay).
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/relays");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal(_relayIri.Value, items[0]);
    }

    // --- The client can read back the relays collection ---------------------------------

    [Fact]
    public async Task Client_GetRelaysAsync_ReadsRelaysCollection()
    {
        await _client.SubscribeRelayAsync(_bobActorIri, _relayIri);

        // The client reads bob's relays collection (via the real collection endpoint) and sees the
        // relay's IRI (a plain link item deserializes to a Link with its Href).
        var relays = new List<Iri>();
        await foreach (var item in _client.GetRelaysAsync(_bobActorIri))
        {
            if (item is Link { Href: { } href })
            {
                relays.Add(new Iri(href.ToString()));
            }
        }

        Assert.Single(relays);
        Assert.Equal(_relayIri.Value, relays[0].Value);
    }

    // --- An unauthenticated subscription is rejected --------------------------------------

    [Fact]
    public async Task SubscribeRelay_Unauthenticated_IsRejected()
    {
        // A subscription with no/invalid Basic auth is rejected (401) and no edge is recorded: a relay
        // subscription is a local decision that requires the acting actor's identity.
        var unauthorized = await LocalPostAsync(_bobActorIri, _relayIri, auth: null, unsubscribe: false);
        Assert.Equal(401, unauthorized);
        Assert.False(await _persistence.Relays.IsRelayAsync(_bobActorIri, _relayIri));
    }

    // --- Un-subscribing removes the edge -------------------------------------------------

    [Fact]
    public async Task UnsubscribeRelay_RemovesEdge()
    {
        await _client.SubscribeRelayAsync(_bobActorIri, _relayIri);
        Assert.True(await _persistence.Relays.IsRelayAsync(_bobActorIri, _relayIri));

        // bob un-subscribes (?unsubscribe=true, 204): the edge is removed and the /relays collection is
        // empty again.
        Assert.Equal(204, await _client.UnsubscribeRelayAsync(_bobActorIri, _relayIri));
        Assert.False(await _persistence.Relays.IsRelayAsync(_bobActorIri, _relayIri));
        Assert.Empty(await _persistence.Relays.GetRelaysAsync(_bobActorIri));

        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/relays");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- An un-subscribe of a non-existent subscription is a no-op ------------------------

    [Fact]
    public async Task UnsubscribeRelay_NonExistent_IsNoOp()
    {
        // Un-subscribing from a relay that was never subscribed to is a no-op (204 — the subscription's
        // steady state is authoritative; no edge is created).
        var statusCode = await _client.UnsubscribeRelayAsync(_bobActorIri, _relayIri);
        Assert.Equal(204, statusCode);
        Assert.False(await _persistence.Relays.IsRelayAsync(_bobActorIri, _relayIri));
        Assert.Empty(await _persistence.Relays.GetRelaysAsync(_bobActorIri));
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Issues a raw Basic-authenticated local-relay POST (used to exercise the unauthenticated 401 path,
    /// which the client's typed <c>SubscribeRelayAsync</c> cannot reach — it always sends credentials).
    /// </summary>
    private async Task<int> LocalPostAsync(Iri actorIri, Iri relayIri, string? auth, bool unsubscribe)
    {
        var url = $"{actorIri.Value.TrimEnd('/')}/relays/{relayIri.Value.TrimStart('/')}"
            + (unsubscribe ? "?unsubscribe=true" : string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (auth is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth)));
        }

        using var response = await _http.SendAsync(request);
        return (int)response.StatusCode;
    }

    private static IActivityPubClient BuildLocalClient(
        Iri actorIri, KeyPair key, Func<HttpMessageHandler> handlerFactory, ProxyCredentials credentials)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        // A local-decision client: LocalCredentials supply the Basic auth for the relay endpoint, and
        // the transport is the in-process TestServer (deferred via the LazyHandler, so the fetcher and
        // the client can both reach the server once it is built).
        return factory.Create(
            new ActivityPubClientOptions
            {
                ActorId = actorIri,
                EnableRetry = false,
                LocalCredentials = credentials,
            },
            new LazyHandler(handlerFactory));
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

}
