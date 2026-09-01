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
/// Phase 12 Slice 12.17 integration test (F-07 moderation — <strong>mute</strong>): a local,
/// Iris-specific moderation decision. A mute is not a federated activity (there is no ActivityStreams
/// <c>Mute</c> type), so it is not interpreted from an inbox POST — it is a Basic-authenticated request
/// to the acting actor's own instance. The actor's <c>mutes</c> collection is served over the wire
/// (an <c>OrderedCollection</c> of muted-actor links, advertised on the actor document), a mute is
/// recorded (and applied: the muted follow's content is hidden from the muter's feed without severing
/// the follow), and an un-mute removes it.
/// </summary>
/// <remarks>
/// Topology: a single instance (b.domain.local) hosts two local actors — <c>bob</c> (the muter, the
/// instance's Handle actor) and <c>carol</c> (the muted, an extra local actor). Bob follows carol; carol
/// has a post. Bob mutes carol via a Basic-authenticated <c>POST /ap/v1/u/bob/mutes/{carol}</c>; the
/// instance authenticates bob (the <c>IActorCredentialValidator</c>) and records the mute edge. Bob's
/// <c>/mutes</c> collection (a public read endpoint) serves the edge, and carol's content is excluded
/// from bob's followed feed (a soft exclusion — the follow is kept). Un-muting (<c>?unmute=true</c>)
/// removes the edge and restores carol's content.
/// </remarks>
public sealed class MutesCollectionIntegrationTests : IDisposable
{
    private const string BHost = "b.domain.local";
    private const string Bob = "bob";
    private const string Carol = "carol";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _bobActorIri;
    private readonly Iri _carolActorIri;
    private readonly IActivityPubClient _client;
    private readonly ILocalModerationClient _local;

    public MutesCollectionIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var bob = TestSeeder.SeedPersonWithKey(_persistence, BHost, Bob);
        var carol = TestSeeder.SeedPersonWithKey(_persistence, BHost, Carol);
        _bobActorIri = bob.ActorIri;
        _carolActorIri = carol.ActorIri;

        // A Basic-auth credential validator: bob's credentials are ("bob", "bob-password") for bob's IRI.
        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(
                iri == _bobActorIri && username == Bob && password == "bob-password"));

        // Bob is the instance actor (Handle) and carol is an extra local actor (her inbox is served by
        // this instance and her key is resolvable). The client is a Basic-authenticated local-moderation
        // client (a mute is not a signed inbox delivery — it is a Basic-authenticated POST to bob's own
        // instance). The fetcher is wired to the in-process TestServer (the actor document is readable).
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _persistence,
            ExtraLocalActors = [carol.ActorIri],
            CredentialValidator = credentialValidator,
            Fetcher = BuildSelfFetcher(bob.Key, bob.ActorIri, () => _server!.CreateHandler()),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
        _client = BuildLocalClient(_bobActorIri, bob.Key, () => _server.CreateHandler(),
            new ProxyCredentials(Bob, "bob-password"));
        _local = BuildLocalModerationClient(_bobActorIri, bob.Key, () => _server.CreateHandler(),
            new ProxyCredentials(Bob, "bob-password"));
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
        _server.Dispose();
    }

    // --- The actor document advertises the mutes collection --------------------------

    [Fact]
    public async Task ActorDocument_AdvertisesMutesCollection()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The actor document advertises the mutes collection link (via the ActivityStreams extension
        // data, since the library's Person has no typed `mutes` property).
        Assert.Equal($"https://{BHost}/ap/v1/u/{Bob}/mutes", doc.RootElement.GetProperty("mutes").GetString());
    }

    // --- The mutes collection is an empty OrderedCollection before any mute ----------

    [Fact]
    public async Task MutesCollection_Empty_IsOrderedCollection()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/mutes");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"https://{BHost}/ap/v1/u/{Bob}/mutes", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- An authenticated mute is recorded and appears in the mutes collection --------

    [Fact]
    public async Task Mute_Authenticated_RecordsMuteEdge()
    {
        var statusCode = await _local.MuteAsync(_bobActorIri, _carolActorIri);
        Assert.Equal(204, statusCode.StatusCode);

        // The instance authenticated bob (Basic auth) and recorded the mute edge (bob → carol).
        Assert.True(await _persistence.Moderation.IsMutedAsync(_bobActorIri, _carolActorIri));
        Assert.Contains(_carolActorIri, await _persistence.Moderation.GetMutesAsync(_bobActorIri));
    }

    // --- The muter's own /mutes collection serves the recorded edge -------------------

    [Fact]
    public async Task Mute_AppearsInMutersMutesCollection()
    {
        await _local.MuteAsync(_bobActorIri, _carolActorIri);

        // Bob's /mutes collection (a public read endpoint) serves the recorded edge (as a link to
        // carol).
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/mutes");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal(_carolActorIri.Value, items[0]);
    }

    // --- The client can read back the mutes collection --------------------------------

    [Fact]
    public async Task Client_GetMutesAsync_ReadsMutesCollection()
    {
        await _local.MuteAsync(_bobActorIri, _carolActorIri);

        // The client reads bob's mutes collection (via the real collection endpoint) and sees the
        // muted actor's IRI (a plain link item deserializes to a Link with its Href).
        var muted = new List<Iri>();
        await foreach (var item in _client.GetMutesAsync(_bobActorIri))
        {
            if (item is Link { Href: { } href })
            {
                muted.Add(new Iri(href.ToString()));
            }
        }

        Assert.Single(muted);
        Assert.Equal(_carolActorIri.Value, muted[0].Value);
    }

    // --- An unauthenticated mute is rejected ------------------------------------------

    [Fact]
    public async Task Mute_Unauthenticated_IsRejected()
    {
        // A mute with no/invalid Basic auth is rejected (401) and no edge is recorded: a mute is a local
        // moderation decision that requires the acting actor's identity.
        var unauthorized = await LocalPostAsync(_bobActorIri, _carolActorIri, auth: null, unmute: false);
        Assert.Equal(401, unauthorized);
        Assert.False(await _persistence.Moderation.IsMutedAsync(_bobActorIri, _carolActorIri));
    }

    // --- F-07 (apply the mute edge): a mute hides content without severing the follow --

    [Fact]
    public async Task Mute_ExcludesContentFromFeed_WithoutSeveringFollow()
    {
        // bob follows carol and carol has a post in her outbox (so bob's feed includes it). Muting carol
        // hides her content from bob's feed (a soft exclusion — the follow is kept), unlike a block,
        // which severs the relationship.
        await _persistence.Follows.RecordFollowAsync(_bobActorIri, _carolActorIri);
        const string noteIri = "https://b.domain.local/ap/v1/u/carol/notes/1";
        await _persistence.Activities.AddToOutboxAsync(_carolActorIri, new Create
        {
            Id = "https://b.domain.local/ap/v1/u/carol/creates/1",
            Actor = [new Link { Href = new Uri(_carolActorIri.Value) }],
            Object = [new Note { Id = noteIri, Content = ["carol post"] }],
        });

        // Before the mute: carol's content is in bob's followed feed.
        Assert.Contains(noteIri, await FeedNoteIrisAsync());

        // bob mutes carol (204): the edge is recorded, the follow is intact, but carol's content is
        // excluded from bob's feed.
        Assert.Equal(204, (await _local.MuteAsync(_bobActorIri, _carolActorIri)).StatusCode);
        Assert.True(await _persistence.Moderation.IsMutedAsync(_bobActorIri, _carolActorIri));
        Assert.Contains(_carolActorIri, await _persistence.Follows.GetFollowingAsync(_bobActorIri));
        Assert.DoesNotContain(noteIri, await FeedNoteIrisAsync());

        // bob un-mutes carol (?unmute=true, 204): the edge is removed and carol's content returns.
        Assert.Equal(204, (await _local.UnmuteAsync(_bobActorIri, _carolActorIri)).StatusCode);
        Assert.False(await _persistence.Moderation.IsMutedAsync(_bobActorIri, _carolActorIri));
        Assert.Contains(noteIri, await FeedNoteIrisAsync());
    }

    // --- F-07 (un-mute): an un-mute of a non-existent mute is a no-op ------------------

    [Fact]
    public async Task Unmute_NonExistent_IsNoOp()
    {
        // Un-muting an actor that was never muted is a no-op (204 — the mute's steady state is
        // authoritative; no edge is created).
        var statusCode = await _local.UnmuteAsync(_bobActorIri, _carolActorIri);
        Assert.Equal(204, statusCode.StatusCode);
        Assert.False(await _persistence.Moderation.IsMutedAsync(_bobActorIri, _carolActorIri));
        Assert.Empty(await _persistence.Moderation.GetMutesAsync(_bobActorIri));
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// Reads bob's followed feed over the wire and returns the IRIs of the content objects (the
    /// <c>Note</c>s) it contains (the feed's items are the followed actors' <c>Create</c>s; each item's
    /// embedded note IRI is read from its <c>object</c>).
    /// </summary>
    private async Task<IReadOnlyList<string>> FeedNoteIrisAsync()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/feed");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return JsonDoc.GetItems(doc.RootElement)
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("object", out var obj))
            .Select(e => e.GetProperty("object"))
            .Select(o => o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().First().GetProperty("id").GetString()!
                : o.GetProperty("id").GetString()!)
            .ToList();
    }

    /// <summary>
    /// Issues a raw Basic-authenticated local-mute POST (used to exercise the unauthenticated 401 path,
    /// which the client's typed <c>MuteAsync</c> cannot reach — it always sends credentials).
    /// </summary>
    private async Task<int> LocalPostAsync(Iri actorIri, Iri targetIri, string? auth, bool unmute)
    {
        var url = $"{actorIri.Value.TrimEnd('/')}/mutes/{targetIri.Value.TrimStart('/')}"
            + (unmute ? "?unmute=true" : string.Empty);
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
        // A local-moderation client: LocalCredentials supply the Basic auth for the mute endpoint, and
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

    private static ILocalModerationClient BuildLocalModerationClient(
        Iri actorIri, KeyPair key, Func<HttpMessageHandler> handlerFactory, ProxyCredentials credentials)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        // A local-moderation client: the default credentials supply the Basic auth for the mute
        // endpoint, and the transport is the in-process TestServer (deferred via the LazyHandler).
        return factory.CreateLocalModerationClient(
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
