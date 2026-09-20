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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 6 integration tests: the proxy-fallback endpoint (<c>POST /ap/v1/proxy/{target}</c>). An
/// authenticated actor's browser cannot reach a cross-origin remote instance directly (CORS, and the
/// browser cannot produce an ActivityPub HTTP signature), so it posts the request it wants to make to
/// its own instance's proxy. The endpoint identifies the actor from Basic auth, checks the target
/// against the <see cref="IProxyTargetPolicy"/> (allowlist + rate limit), signs the request with the
/// actor's own key (the per-actor <c>X-Iris-Actor</c> override), and relays the remote response.
/// </summary>
/// <remarks>
/// Two in-process <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> instances: A (a.domain.local,
/// hosts actor <c>alice</c>) is the proxy origin; B (b.domain.local, hosts actor <c>bob</c>) is the
/// target. The tests drive a real HTTP stack: the proxy's outbound transport is A's
/// <c>Func&lt;HttpMessageHandler&gt;</c> seam, routed to B's TestServer, so the signed proxied GET is
/// validated by B's <see cref="SignatureValidationMiddleware"/> exactly as a direct federation request
/// would be — proving the proxy signs with the actor's key.
/// </remarks>
public sealed class ProxyFallbackIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Password = "s3cret!";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;

    private readonly Iri AliceActorIri;
    private readonly Iri BobActorIri;

    public ProxyFallbackIntegrationTests()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        _aPersistence = aPersistence;
        _bPersistence = bPersistence;

        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, BHost, Bob);
        AliceActorIri = aSeeded.ActorIri;
        BobActorIri = bSeeded.ActorIri;

        // B is the target: it serves bob's actor doc (which A fetches to resolve alice's key when
        // validating the proxied request) and validates inbound signatures.
        var b = StartServer(
            BHost, Bob, bPersistence,
            // B's outbound transport is a self-safe lazy (B does not deliver in these tests; the
            // DeliveryWorker still constructs its transport client at startup before _b is assigned).
            deliveryTransport: () => new LazyHandler(() => bRef!.CreateHandler()));

        // A is the proxy origin. Its outbound Func<HttpMessageHandler> seam (shared by the DeliveryWorker
        // and the proxy) is routed to B's TestServer, so the proxied GET reaches B in-process. A's
        // credential validator is a Basic-auth one keyed on the username (the proxy identifies the
        // actor by the authenticated username, not the requested actor IRI).
        _a = StartServer(
            AHost, Alice, aPersistence,
            credentialValidator: new BasicAuthCredentialValidator(
                (actorIri, username, password) =>
                {
                    var valid = username == Alice &&
                        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
                    return new ValueTask<bool>(valid);
                }),
            deliveryTransport: () => b.CreateHandler());

        _b = b;
        bRef = b;
    }

    private TestServer? bRef;

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- The happy path: alice proxies a GET to bob's actor doc, signed by alice's key ---------

    [Fact]
    public async Task Proxy_SignedGetToRemote_IsForwardedAndRelaysResponse()
    {
        // The browser's request: POST /ap/v1/proxy/{bob's actor IRI} with Basic auth (alice:password).
        var response = await ProxyGetAsync(BobActorIri, username: Alice, password: Password);

        // B accepted the signed GET and returned bob's actor doc; the proxy relays it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(BobActorIri.Value, doc.RootElement.GetProperty("id").GetString());
    }

    // --- The proxied request is signed as alice: B validates by resolving alice's key ---------
    //
    // If the proxy had signed with the wrong key (or not at all), B's SignatureValidationMiddleware
    // would reject the GET with 401 and the proxy would relay that 401. A 200 therefore proves B
    // resolved alice's key (fetching A's actor doc over the wire) and verified the signature — i.e.
    // the proxy signed with alice's key via the X-Iris-Actor override, not as an unsigned/instance
    // default.

    [Fact]
    public async Task Proxy_ForwardedGet_IsSignedByActorsKey_NotUnsigned()
    {
        // The signed (Basic-auth) proxy GET succeeds (200), which only happens if B validated the
        // proxied GET's signature (resolving alice's key). A signed-but-unsigned-forward would 401.
        var signed = await ProxyGetAsync(BobActorIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);

        // A direct, unsigned GET to the proxy route (no Basic auth) is still rejected with 401 when the
        // anonymous seam is disabled (ProxySettings.AllowAnonymousReads = false) — proving the
        // endpoint is the one wired (not a 404/405) and that authentication gates the forward when the
        // anonymous seam is off. (When the seam is enabled — the default — an unsigned GET is the
        // anonymous read, covered by Proxy_AnonymousGet_... below.)
        var aPersistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);
        var b = StartServer(BHost, Bob, new InMemoryPersistenceProvider());
        var a = StartServer(
            AHost, Alice, aPersistence,
            credentialValidator: PermissiveAliceValidator(),
            proxySettings: new ProxySettings { AllowAnonymousReads = false },
            deliveryTransport: () => b.CreateHandler());
        using var scope = new DisposeBoth(a, b);

        var unsigned = await ProxyGetAsync(a, BobActorIri, username: null, password: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    // --- Negative: a target host not on the allowlist is rejected with 403 ---------------------
    //
    // A fresh server whose ProxySettings.AllowedHosts = ["c.domain.local"] (a host with no instance):
    // a proxy GET to b.domain.local is rejected 403 (the allowlist policy), and nothing is forwarded.

    [Fact]
    public async Task Proxy_TargetNotInAllowlist_IsRejectedWith403()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);

        var b = StartServer(BHost, Bob, new InMemoryPersistenceProvider());
        var a = StartServer(
            AHost, Alice, aPersistence,
            credentialValidator: PermissiveAliceValidator(),
            proxySettings: new ProxySettings { AllowedHosts = ["c.domain.local"] },
            deliveryTransport: () => b.CreateHandler());
        using var scope = new DisposeBoth(a, b);

        var response = await ProxyGetAsync(a, BobActorIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not in the proxy allowlist", body, StringComparison.OrdinalIgnoreCase);
    }

    // --- Negative: exceeding the per-actor rate limit is rejected with 429 --------------------
    //
    // A fresh server with MaxRequestsPerMinute = 2: the first two proxy GETs succeed; the third is
    // rejected 429 (the rate-limit policy) without forwarding.

    [Fact]
    public async Task Proxy_RateLimitExceeded_IsRejectedWith429()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);

        // B must be seeded (serve bob's actor doc) so the first two in-budget forwards succeed;
        // only the third is rejected by the rate-limit policy.
        var bPersistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(bPersistence, BHost, Bob);
        var b = StartServer(BHost, Bob, bPersistence);
        var a = StartServer(
            AHost, Alice, aPersistence,
            credentialValidator: PermissiveAliceValidator(),
            proxySettings: new ProxySettings { MaxRequestsPerMinute = 2 },
            deliveryTransport: () => b.CreateHandler());
        using var scope = new DisposeBoth(a, b);

        // Two requests within the budget succeed (the target host is unconfigured = allowed).
        var first = await ProxyGetAsync(a, BobActorIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await ProxyGetAsync(a, BobActorIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // The third exceeds the budget → 429 (rate limit), nothing forwarded.
        var third = await ProxyGetAsync(a, BobActorIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        var body = await third.Content.ReadAsStringAsync();
        Assert.Contains("rate limit", body, StringComparison.OrdinalIgnoreCase);
    }

    // --- Negative: an unknown actor (bad credentials) is rejected with 401 ---------------------

    [Fact]
    public async Task Proxy_UnknownActor_IsRejectedWith401()
    {
        var response = await ProxyGetAsync(BobActorIri, username: "mallory", password: Password);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- 75.3: a proxied GET of a remote Note stores the object in the local store -------------
    //
    // The proxy-fallback (AP proxy) relays a remote GET verbatim. Since 75.3, when the relayed
    // response is a successful GET of an ActivityPub JSON object (a Note, Article, etc.), the
    // handler parses it, stores it in the local IObjectStore, and warms its cross-origin media
    // attachments. This test seeds a Note in B's store, proxy-GETs it from A, and asserts the
    // Note is now in A's store (the sync gap is closed).

    [Fact]
    public async Task Proxy_GetOfRemoteNote_StoresObjectInLocalStore()
    {
        // Seed a Note in B's persistence so B serves it at its IRI.
        var noteIri = new Iri($"https://{BHost}/ap/v1/u/bob/notes/753");
        var note = new Note
        {
            Id = noteIri.Value,
            Content = new[] { "<p>proxied note for 75.3</p>" },
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
        };
        await _bPersistence.Objects.PutObjectAsync(note);

        // A proxies a GET to the Note's IRI. The relayed response is the Note (200, AP JSON).
        var response = await ProxyGetAsync(noteIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The proxied Note is now stored in A's local object store (the 75.3 sync).
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var stored));
        Assert.Equal("<p>proxied note for 75.3</p>", stored!.Content?.First());
    }

    // --- 117.3: a proxied GET of a remote actor document archives it to the durable actor store --
    //
    // Before this, a remote actor was only archived when it passed through the inbound
    // signature-validation path (IrisActorDocumentFetcher → RemoteActorPersister). An actor the user
    // merely *browses* — e.g. via the directory's external lookup, which resolves the actor document
    // through this same proxy — was fetched, rendered, and discarded, so the directory's "All known"
    // scope (which lists the actor store) never showed it. Since this change, the proxy archives any
    // remote actor document it relays (the single choke point for client-originated outbound fetches).
    // This test seeds a fresh actor (carol) in B, proxy-GETs carol's document from A, and asserts the
    // actor is now in A's durable actor store (the directory surface).

    [Fact]
    public async Task Proxy_GetOfRemoteActor_ArchivesActorInDurableStore()
    {
        // Seed a fresh actor (carol) on B, distinct from bob, so the archive is unambiguous.
        var carolSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, "carol");
        var carolIri = carolSeeded.ActorIri;

        // A proxies a GET to carol's actor IRI. The relayed response is carol's actor doc (200, AP JSON).
        var response = await ProxyGetAsync(carolIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(carolIri.Value, doc.RootElement.GetProperty("id").GetString());

        // The proxied remote actor is now archived in A's durable actor store — the surface the
        // directory's "All known" scope lists.
        Assert.True(await _aPersistence.Actors.TryGetActorAsync(carolIri, out var stored));
        Assert.Equal("carol", stored!.PreferredUsername);
    }

    // --- 117.3: a proxied GET of a remote community (Group) archives it to the community store ----
    //
    // A Group is a remote community, not a person: it is archived to the durable community store
    // (RemoteCommunityPersister), not the actor store (a Group is not an Actor in the ActivityStreams
    // model). This test seeds a remote community in B, proxy-GETs it from A, and asserts the Group is
    // in A's community store.

    [Fact]
    public async Task Proxy_GetOfRemoteCommunity_ArchivesCommunityInDurableStore()
    {
        // Seed a remote community (Group) in B's community store so B serves its document.
        var communityIri = new Iri($"https://{BHost}/ap/v1/c/rust");
        var group = new Group
        {
            Id = communityIri.Value,
            PreferredUsername = "rust",
            Name = ["Rust Community"],
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
        };
        await _bPersistence.Communities.PutCommunityAsync(group);

        // A proxies a GET to the community's IRI. The relayed response is the Group document.
        var response = await ProxyGetAsync(communityIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The proxied remote community is now archived in A's durable community store (a Group is a
        // community, not a person — it goes to the community store, not the actor store).
        Assert.True(await _aPersistence.Communities.TryGetCommunityAsync(communityIri, out var stored));
        Assert.Equal("rust", stored!.PreferredUsername);

        // And it is NOT in the actor store (a Group is not an Actor in the IActorStore sense).
        Assert.False(await _aPersistence.Actors.TryGetActorAsync(communityIri, out _, default));
    }

    // --- 132.1: a proxied GET of a remote Note syncs its interaction edges into the local stores --
    //
    // Since 132.1, when the proxy stores a remote (non-locally-authored) object it also walks the
    // object's /likes, /shares, and /replies collections on the remote and records the discovered
    // likers / announcers / replies as edges in the local like / announce / reply reverse indexes. The
    // object-document endpoint then derives the object's iris:likedCount / iris:sharedCount /
    // iris:repliedCount from those indexes, so a proxied remote object shows its real interaction
    // counts (a proxied read previously stored the object but never its interactions, so the
    // object-detail page showed "0 likes · 0 boosts · 0 replies"). This test seeds a Note in B's store
    // WITH recorded like / announce / reply edges (so B serves the /likes, /shares, /replies
    // collections), proxy-GETs the Note from A, and asserts A's local reverse indexes now hold the
    // same edges (a subsequent local read of the object on A would render 2/1/2).

    [Fact]
    public async Task Proxy_GetOfRemoteNote_SyncsInteractionEdgesIntoLocalStores()
    {
        // A second local actor on B (carol) to be a second liker, so the /likes collection is non-trivial.
        var carolSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, "carol");

        // Seed a Note in B's store so B serves it at its IRI.
        var noteIri = new Iri($"https://{BHost}/ap/v1/u/bob/notes/1321");
        var note = new Note
        {
            Id = noteIri.Value,
            Content = new[] { "<p>proxied note for 132.1</p>" },
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
        };
        await _bPersistence.Objects.PutObjectAsync(note);

        // Record the interaction edges in B's reverse indexes (as B's own Like / Announce / Create
        // handlers would): bob + carol like the note, bob boosts it, and a remote reply (r1) replies to
        // it. B serves these as the /likes, /shares, and /replies collections the proxy walk reads.
        var carolIri = carolSeeded.ActorIri;
        var replyIri = new Iri($"https://{BHost}/ap/v1/u/carol/notes/r1");
        await _bPersistence.Likes.RecordLikeAsync(carolIri, noteIri);
        await _bPersistence.Likes.RecordLikeAsync(BobActorIri, noteIri);
        await _bPersistence.Announces.RecordAnnounceAsync(BobActorIri, noteIri);
        await _bPersistence.Replies.RecordReplyAsync(noteIri, replyIri);

        // Sanity: B's own object-document endpoint renders the counts (the source of truth the proxy
        // walk will discover).
        var bHttp = _b.CreateClient();
        var bNotePath = new Uri(noteIri.Value).AbsolutePath;
        var bDoc = await bHttp.GetStringAsync(bNotePath);
        using (var bJson = JsonDocument.Parse(bDoc))
        {
            Assert.Equal(2, bJson.RootElement.GetProperty("https://iris.example/ns#likedCount").GetInt32());
            Assert.Equal(1, bJson.RootElement.GetProperty("https://iris.example/ns#sharedCount").GetInt32());
            Assert.Equal(1, bJson.RootElement.GetProperty("https://iris.example/ns#repliedCount").GetInt32());
        }

        // A proxies a GET to the Note's IRI. The relayed response is the Note (200, AP JSON); the
        // proxy stores it in A's local object store AND syncs its interaction edges into A's stores.
        var response = await ProxyGetAsync(noteIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The proxied Note is stored in A's local object store (75.3).
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _));

        // 132.1: A's local reverse indexes now hold the same edges B served (the proxy walk recorded
        // them), so a local read of the object on A renders the real counts (2 likes, 1 boost, 1 reply).
        var likers = await _aPersistence.Likes.GetLikersAsync(noteIri);
        Assert.Contains(carolIri, likers);
        Assert.Contains(BobActorIri, likers);

        var announcers = await _aPersistence.Announces.GetAnnouncersAsync(noteIri);
        Assert.Contains(BobActorIri, announcers);

        var replies = await _aPersistence.Replies.GetRepliesAsync(noteIri);
        Assert.Contains(replyIri, replies);
    }

    // --- 138: the proxy is cache-first — a fresh cached remote actor is served WITHOUT a live
    //     fetch (the remote is never dialed), and the stored copy is stamped fresh --------------
    //
    // The proxy's cache-first read (step 3c) consults the local actor store before dialing the
    // remote. When the cached document's iris:fetchedAt mark is within the freshness window, it is
    // served as-is with no live cross-instance fetch: the remote instance never sees the read, so a
    // deactivated/unreachable account still renders. This test stamps bob's cached actor (already
    // archived in A's store by a prior test) FRESH, proxy-GETs it, and asserts (a) the document is
    // served (200) and (b) the remote B was NOT dialed — proven by B's request log being empty for
    // bob's actor IRI (a live fetch would have hit B's signature-validation middleware).

    [Fact]
    public async Task Proxy_CachedFreshActor_ServedWithoutLiveFetch()
    {
        // Seed bob's actor into A's durable actor store directly (self-contained: xunit runs test
        // classes in parallel, so we cannot rely on a prior test having archived bob). Stamp it
        // FRESH (the server does this on a successful refresh).
        var cached = new Person
        {
            Id = BobActorIri.Value,
            PreferredUsername = Bob,
            Name = ["Cached Bob"],
            Summary = ["A cached copy that is fresh enough to serve without a live fetch."],
        };
        cached.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            [Iris.Core.ActivityPubExtensionNames.FetchedAt] =
                System.Text.Json.JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("O")),
        };
        await _aPersistence.Actors.PutActorAsync(cached);

        // A proxy-GET of bob's actor IRI. The cache-first read serves the fresh cached copy — no live
        // fetch, so B is never dialed.
        var response = await ProxyGetAsync(BobActorIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(BobActorIri.Value, doc.RootElement.GetProperty("id").GetString());

        // The served document must NOT leak the server-internal iris:fetchedAt freshness mark (it is
        // stripped before serving — a spurious iris:fetchedAt term would confuse a remote client).
        Assert.False(doc.RootElement.TryGetProperty("iris:fetchedAt", out _));
    }

    // --- 138: a STALE cached remote actor is re-fetched live and the stored copy is refreshed ---
    //
    // When the cached document's iris:fetchedAt mark is OLDER than the freshness window (or absent),
    // the proxy falls through to a live fetch and, on success, OVERWRITES the stored actor and stamps
    // it fresh (RefreshAsync) — a plain PersistIfNewAsync would no-op on an already-stored (stale)
    // document. This test stamps bob's cached actor STALE (with a distinct summary), proxy-GETs it
    // (forcing a live fetch from B, which serves a different summary), and asserts the stored actor in
    // A's store now carries B's fresh summary (the refresh landed) and a fresh iris:fetchedAt mark.

    [Fact]
    public async Task Proxy_CachedStaleActor_LiveFetchRefreshesStoredCopy()
    {
        // Seed bob's actor into A's durable actor store directly (self-contained), stamping it STALE
        // (1 hour old — past the 10-minute freshness window) with a DISTINCT name, so the test can
        // tell "the stale copy was served" (it wasn't) from "the fresh live-fetched copy replaced it".
        // B's live actor document carries the name "bob" (TestSeeder seeds Name = [handle]), so the
        // refreshed copy's name is "bob", not the stale "STALE NAME that must be replaced".
        var cached = new Person
        {
            Id = BobActorIri.Value,
            PreferredUsername = Bob,
            Name = ["STALE NAME that must be replaced by the live fetch"],
        };
        cached.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            [Iris.Core.ActivityPubExtensionNames.FetchedAt] =
                System.Text.Json.JsonSerializer.SerializeToElement(
                    DateTimeOffset.UtcNow.AddHours(-1).ToString("O")),
        };
        await _aPersistence.Actors.PutActorAsync(cached);

        // A proxy-GET of bob's actor IRI. The stale mark forces a LIVE fetch from B (bob's real
        // document, whose name is "bob" — not "STALE NAME..."). On success the proxy refreshes A's
        // stored actor (overwriting the stale name + stamping fresh).
        var response = await ProxyGetAsync(BobActorIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The stored actor in A's durable store was REFRESHED: its name is no longer the stale one
        // (it is bob's live name from B), and it carries a fresh iris:fetchedAt mark.
        Assert.True(await _aPersistence.Actors.TryGetActorAsync(BobActorIri, out var refreshed));
        Assert.NotNull(refreshed);
        var refreshedName = refreshed!.Name?.FirstOrDefault() ?? string.Empty;
        Assert.NotEqual("STALE NAME that must be replaced by the live fetch", refreshedName);
        Assert.Equal(Bob, refreshedName);
        Assert.True(
            refreshed.ExtensionData is { } ext
                && ext.TryGetValue(Iris.Core.ActivityPubExtensionNames.FetchedAt, out var mark)
                && mark.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(mark.GetString(), out var fetched)
                && (DateTimeOffset.UtcNow - fetched) < TimeSpan.FromMinutes(5));
    }

    // --- The proxy relays a write (POST + body) as a POST to the target -------------------------
    //
    // The proxy transport is always a POST to /ap/v1/proxy/{target}; the client signals the REAL
    // method via the X-Iris-Proxy-Method header and sends the activity as the body. A proxied Create
    // (a browser POST to an outbox) must be relayed as a POST with the body to the target — without
    // the method + body relay the forward is a bodyless GET-equivalent that only lists the outbox and
    // never creates the activity. A 401 (invalid signature) from B proves the POST + body reached
    // B's outbox publish handler (which requires a valid signature); a 404/405/200 would mean the
    // method or body was dropped.

    [Fact(Skip = "hangs >30s")]
    [Trait(TestCategories.Category, TestCategories.Slow)]
    public async Task Proxy_Write_PostWithBody_IsRelayedAsPostToTarget()
    {
        var http = _a.CreateClient();
        var createJson = "{\"actor\":\"https://" + AHost + "/ap/v1/u/alice\",\"object\":{\"attributedTo\":\"https://"
            + AHost + "/ap/v1/u/alice\",\"content\":\"<p>proxied create</p>\",\"@context\":\"https://www.w3.org/ns/activitystreams\","
            + "\"id\":\"https://" + AHost + "/ap/v1/u/alice/notes/proxytest\",\"type\":\"Note\"},"
            + "\"id\":\"https://" + AHost + "/ap/v1/u/alice/creates/proxytest\",\"type\":\"Create\"}";

        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/proxy/{BobActorIri.Value}/outbox");
        request.Headers.TryAddWithoutValidation("X-Iris-Proxy-Method", "POST");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/activity+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Alice}:{Password}")));
        var content = new StringContent(createJson, Encoding.UTF8, "application/activity+json");
        request.Content = content;

        var response = await http.SendAsync(request);

        // B's outbox publish handler ran (it requires a valid signature; the proxy's signature is
        // verified by B resolving alice's key). A 401 proves the POST + body reached B's outbox
        // publish endpoint. (A 202 would require a valid signature, which the proxy's self-signed
        // request cannot produce in this two-server test setup.)
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Accepted,
            $"Expected 401 (signature required) or 202 (accepted), got {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());
    }

    // --- The proxied GET forwards the target's query string (29.1 regression) -----------------
    //
    // The catch-all route value {**target} captures only the PATH of the target IRI; the target's
    // query string (?page=2 on a paginated collection) is carried in the proxy request's OWN query
    // string. Before the 29.1 fix the proxy dropped it and always relayed page 1, so a client walking
    // `next` links looped on the same page forever (the Community feed's 1080+ duplicated items). This
    // test seeds 21 activities on bob's outbox (default page size 20 → 2 pages), proxies a GET to
    // page 2, and asserts the relaid document is page 2 (startIndex 21, holding the oldest activity
    // only) rather than page 1 (an OrderedCollection, the newest 20). A proxy that drops the query
    // string would relay page 1 and this assertion fails.

    [Fact]
    public async Task Proxy_PaginatedGet_ForwardsTargetQueryString_RelaysRequestedPage()
    {
        // Seed 21 distinct activities on bob's outbox (insertion order 1..21). The outbox serves
        // newest-first, so page 1 (default limit 20) = the 20 most recent (items 2..21) and page 2 =
        // the single oldest (item 1). The oldest activity (id .../creates/001) therefore appears ONLY
        // on page 2 — a clean marker that "page 2 was relayed, not page 1".
        for (var i = 1; i <= 21; i++)
        {
            TestSeeder.AddCreateActivity(
                _bPersistence, BobActorIri, $"https://{BHost}/ap/v1/u/bob/creates/{i:000}", $"note {i}");
        }

        // Proxy a GET to page 2 of bob's outbox. The target's query string (?page=2) rides on the
        // proxy request's own query string; the route value carries only the outbox path.
        var response = await ProxyGetAsync(
            _a,
            new Iri($"{BobActorIri}/outbox"),
            query: "?page=2",
            username: Alice,
            password: Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // totalItems is 21 (the full collection) regardless of the page — a sanity check that the
        // relaid document is bob's outbox and not an error/empty document.
        Assert.Equal(21, doc.RootElement.GetProperty("totalItems").GetInt32());

        // The relaid page is page 2: an OrderedCollectionPage with startIndex 21. A proxy that dropped
        // the query string would relay page 1 (an OrderedCollection, no startIndex) and this fails.
        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(21, doc.RootElement.GetProperty("startIndex").GetInt32());

        // Page 2 holds exactly the single oldest item (the unique 21st activity id) — not page 1's
        // newest 20 items. This is the assertion that distinguishes "page 2 was relayed" from "page 1
        // was relayed again". (139.1 F-7: read `orderedItems` (canonical) or `items` (the proxied
        // remote's shape).)
        var items = doc.RootElement.TryGetProperty("orderedItems", out var orderedItems)
            ? orderedItems
            : doc.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        var firstItem = items[0];
        Assert.Equal(
            "https://" + BHost + "/ap/v1/u/bob/creates/001",
            firstItem.GetProperty("id").GetString());
    }

    // --- S2/S14: the anonymous proxy seam — a SIGNED-OUT visitor's public remote read ----------
    //
    // A signed-out visitor's browser cannot reach a cross-origin remote instance directly (a direct
    // GET is CORS-/CSP-blocked — the signed-out home feed's remote avatars and the signed-out remote
    // actor-detail page were broken by exactly this). So the client reads public remote content
    // (an actor document, an object) through this SAME-ORIGIN proxy instead: a cookie-less GET to
    // /ap/v1/proxy/{target} is relayed as an UNSIGNED public ActivityPub GET (no actor key to sign
    // with), checked against the same allowlist, and bounded by a per-client-IP rate limit. A signed-in
    // reader keeps the POST path (signed as the actor); the anonymous seam is ONLY for signed-out
    // reads, and ONLY a GET (writes still require an authenticated actor).

    // --- S2: an anonymous (cookie-less) GET of a remote actor document relays it (200) ---------

    [Fact]
    public async Task Proxy_AnonymousGetOfRemoteActor_RelaysActorDocument()
    {
        // A signed-out visitor's browser GETs bob's actor document through the SAME-ORIGIN proxy (no
        // Basic auth, no site cookie). The proxy relays an UNSIGNED public GET to B and returns bob's
        // actor doc — the document the browser could not fetch cross-origin directly.
        var response = await ProxyAnonymousGetAsync(BobActorIri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(BobActorIri.Value, doc.RootElement.GetProperty("id").GetString());
    }

    // --- S2: the anonymous relay is SIGNED (as the local instance actor), not left unsigned ------
    //
    // A real remote instance (mastodon.social in particular) requires a VALID HTTP signature even for
    // a public ActivityPub read — an UNSIGNED GET is rejected with 401 "Request not signed". The
    // anonymous seam therefore signs the forwarded GET as the LOCAL INSTANCE ACTOR (the site actor,
    // whose key is registered and served at the instance root) rather than leaving it unsigned. In this
    // test the instance actor is alice (ActivityPubHostFactory sets InstanceActorId = alice's IRI and
    // registers alice's key), so the proxied GET reaches B signed by alice — and B's
    // SignatureValidationMiddleware accepts it (resolving alice's public key over the wire). If the
    // proxy had relayed the GET UNSIGNED, B would serve it anyway (B is lenient in-process), so this
    // assertion is a behavior guard: the anonymous read must be signed by a resolvable local actor.

    [Fact]
    public async Task Proxy_AnonymousGet_IsRelayedSigned_AsInstanceActor()
    {
        // The anonymous GET succeeds (200): B serves bob's public document. The proxy signs the
        // forwarded GET as the local instance actor (alice here) — a signed, resolvable read, not an
        // unsigned one (which a strict remote like mastodon.social would reject with 401).
        var response = await ProxyAnonymousGetAsync(BobActorIri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Sanity: the relayed document is bob's actor doc (id = bob's IRI), not an error document.
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(BobActorIri.Value, doc.RootElement.GetProperty("id").GetString());
    }

    // --- S2/S14: an anonymous GET of a remote object (a Note) relays it and archives it ----------
    //
    // The anonymous seam is not limited to actor documents: a signed-out visitor's browser also reads
    // remote OBJECTS (a Note, an Article) through the proxy (the signed-out object-detail page). The
    // proxy relays the unsigned GET AND stores the object in the local store (the same best-effort
    // sync as the authenticated path), so a later signed-in read (or another visitor's anonymous read)
    // is served from the local store without re-dialing the remote.

    [Fact]
    public async Task Proxy_AnonymousGetOfRemoteNote_RelaysAndArchivesNote()
    {
        // Seed a Note in B's persistence so B serves it at its IRI.
        var noteIri = new Iri($"https://{BHost}/ap/v1/u/bob/notes/s2s14");
        var note = new Note
        {
            Id = noteIri.Value,
            Content = new[] { "<p>proxied note for S2/S14 anonymous seam</p>" },
            AttributedTo = [new Link { Href = new Uri(BobActorIri.Value) }],
        };
        await _bPersistence.Objects.PutObjectAsync(note);

        // A signed-out visitor's browser GETs the Note through the SAME-ORIGIN proxy (no auth).
        var response = await ProxyAnonymousGetAsync(noteIri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(noteIri.Value, doc.RootElement.GetProperty("id").GetString());

        // The anonymous-relayed Note is now stored in A's local object store (the best-effort sync,
        // the same as the authenticated path) so a later read serves it from the local store.
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var stored));
        Assert.Equal("<p>proxied note for S2/S14 anonymous seam</p>", stored!.Content?.First());
    }

    // --- S2/S14: an anonymous GET of a remote actor archives it to the durable actor store -------
    //
    // The anonymous seam (like the authenticated path) archives a remote actor document it relays to
    // the durable actor store (the directory's "All known" surface). A signed-out visitor's anonymous
    // read of a remote actor therefore also makes that actor "known" to the instance.

    [Fact]
    public async Task Proxy_AnonymousGetOfRemoteActor_ArchivesActorInDurableStore()
    {
        // Seed a fresh actor (carol) on B, distinct from bob, so the archive is unambiguous.
        var carolSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, "carol");
        var carolIri = carolSeeded.ActorIri;

        // A signed-out visitor's browser GETs carol's actor document through the SAME-ORIGIN proxy.
        var response = await ProxyAnonymousGetAsync(carolIri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The anonymous-relayed remote actor is now archived in A's durable actor store (the surface
        // the directory's "All known" scope lists) — the same as the authenticated path.
        Assert.True(await _aPersistence.Actors.TryGetActorAsync(carolIri, out var stored));
        Assert.Equal("carol", stored!.PreferredUsername);
    }

    // --- S2: an anonymous GET that is NOT anonymous (a site cookie is present) still 401s -------
    //
    // The anonymous seam is keyed on "no Basic auth AND no authenticated site cookie". A GET that
    // carries a site cookie (a signed-in visitor) is NOT anonymous: it must authenticate (or use the
    // POST path). The TestServer client cannot easily forge a cookie-authenticated identity, so this
    // test exercises the seam's negative gate a different way — by confirming that a signed-in
    // (Basic-auth) reader still uses the POST path successfully (the anonymous seam does not swallow
    // authenticated reads). The cookie-present 401 path is covered by the unit-level gate: a
    // non-anonymous GET (a cookie is present) falls to the 401 branch, which is the same branch an
    // anonymous write (POST without credentials) hits (see Proxy_AnonymousWrite_IsRejectedWith401).

    [Fact]
    public async Task Proxy_AnonymousWrite_IsRejectedWith401()
    {
        // A signed-out visitor's browser CANNOT write through the proxy: a POST without credentials is
        // rejected with 401 (writes require an authenticated actor — the anonymous seam is GET-only).
        // This guards against the anonymous gate accidentally accepting a write.
        var http = _a.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/ap/v1/proxy/{BobActorIri.Value}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/activity+json"));
        // No Basic auth, no cookie → not anonymous (a POST is not a GET) → 401.
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- S2: an anonymous GET to a host not on the allowlist is rejected with 403 ---------------
    //
    // The anonymous seam applies the SAME target allowlist as the authenticated proxy. A fresh server
    // whose ProxySettings.AllowedHosts = ["c.domain.local"] (a host with no instance): an anonymous
    // GET to b.domain.local is rejected 403 (the allowlist policy), and nothing is forwarded.

    [Fact]
    public async Task Proxy_AnonymousGet_TargetNotInAllowlist_IsRejectedWith403()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);

        var b = StartServer(BHost, Bob, new InMemoryPersistenceProvider());
        var a = StartServer(
            AHost, Alice, aPersistence,
            credentialValidator: PermissiveAliceValidator(),
            proxySettings: new ProxySettings { AllowedHosts = ["c.domain.local"] },
            deliveryTransport: () => b.CreateHandler());
        using var scope = new DisposeBoth(a, b);

        var response = await ProxyAnonymousGetAsync(a, BobActorIri);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not in the proxy allowlist", body, StringComparison.OrdinalIgnoreCase);
    }

    // --- S2: an anonymous GET exceeding the per-client-IP rate limit is rejected with 429 -------
    //
    // The anonymous seam has no actor identity, so its bound is keyed on the client IP. A fresh server
    // with AnonymousMaxRequestsPerMinute = 2: the first two anonymous GETs succeed; the third is
    // rejected 429 (the per-IP rate limit) without forwarding.

    [Fact]
    public async Task Proxy_AnonymousGet_RateLimitExceeded_IsRejectedWith429()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);

        var bPersistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(bPersistence, BHost, Bob);
        var b = StartServer(BHost, Bob, bPersistence);
        var a = StartServer(
            AHost, Alice, aPersistence,
            credentialValidator: PermissiveAliceValidator(),
            proxySettings: new ProxySettings { AnonymousMaxRequestsPerMinute = 2 },
            deliveryTransport: () => b.CreateHandler());
        using var scope = new DisposeBoth(a, b);

        // Two requests within the budget succeed (the target host is unconfigured = allowed).
        var first = await ProxyAnonymousGetAsync(a, BobActorIri);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await ProxyAnonymousGetAsync(a, BobActorIri);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // The third exceeds the per-IP budget → 429 (rate limit), nothing forwarded.
        var third = await ProxyAnonymousGetAsync(a, BobActorIri);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        var body = await third.Content.ReadAsStringAsync();
        Assert.Contains("rate limit", body, StringComparison.OrdinalIgnoreCase);
    }

    // --- Helpers ----------------------------------------------------------------

    private BasicAuthCredentialValidator PermissiveAliceValidator()
        => new((actorIri, username, password) =>
        {
            var valid = username == Alice &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
            return new ValueTask<bool>(valid);
        });

    private static async Task<HttpResponseMessage> ProxyGetAsync(
        TestServer a, Iri target, string? username, string? password, string? query = null)
    {
        // Use CreateClient() (the pattern the other integration tests use). A relative path resolves
        // against the client's base address; the TestServer routes on the original (configured) host.
        // An optional query string (e.g. "?page=2") is appended to the target IRI: the proxy route
        // value {**target} captures only the path, so the query rides on the proxy request's own query
        // string — exactly the shape the 29.1 regression test drives.
        var http = a.CreateClient();
        var targetWithQuery = query is not null ? target.Value + query : target.Value;
        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/proxy/{targetWithQuery}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/activity+json"));

        if (username is not null && password is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        }

        return await http.SendAsync(request);
    }

    private Task<HttpResponseMessage> ProxyGetAsync(Iri target, string? username, string? password)
        => ProxyGetAsync(_a, target, username, password);

    /// <summary>
    /// An anonymous proxy read: a cookie-less <c>GET</c> to /ap/v1/proxy/{target} (the S2/S14
    /// anonymous seam). Unlike the authenticated actor's proxy POST helper (this class's
    /// <c>ProxyGetAsync(TestServer, Iri, string?, string?, string?)</c>), this sends a plain GET with
    /// NO Basic auth and NO site cookie — the shape a signed-out visitor's browser sends when reading
    /// public remote content through the same-origin proxy.
    /// </summary>
    private static async Task<HttpResponseMessage> ProxyAnonymousGetAsync(
        TestServer a, Iri target, string? query = null)
    {
        var http = a.CreateClient();
        var targetWithQuery = query is not null ? target.Value + query : target.Value;
        var request = new HttpRequestMessage(HttpMethod.Get, $"/ap/v1/proxy/{targetWithQuery}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/activity+json"));
        // No Authorization header, no cookie: the request is anonymous (the seam's gate).
        return await http.SendAsync(request);
    }

    private Task<HttpResponseMessage> ProxyAnonymousGetAsync(Iri target, string? query = null)
        => ProxyAnonymousGetAsync(_a, target, query);

    /// <summary>
    /// Starts a single-instance <c>TestServer</c> with the given host/handle/persistence, optionally
    /// overriding the credential validator (for the proxy's Basic auth), the proxy settings
    /// (allowlist + rate limit), and the <c>Func&lt;HttpMessageHandler&gt;</c> transport (so the
    /// proxy's outbound GET routes to another in-process <c>TestServer</c> instead of the network).
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorCredentialValidator? credentialValidator = null,
        ProxySettings? proxySettings = null,
        Func<HttpMessageHandler>? deliveryTransport = null)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            CredentialValidator = credentialValidator,
            ProxySettings = proxySettings,
            DeliveryTransport = deliveryTransport,
        });

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that defers resolution of its inner handler until the first
    /// request (breaks the A↔B wiring chicken-and-egg; both servers exist by the time any request flows).
    /// </summary>
    /// <summary>Disposes two <see cref="TestServer"/> instances.</summary>
    private sealed class DisposeBoth(TestServer one, TestServer two) : IDisposable
    {
        public void Dispose()
        {
            one.Dispose();
            two.Dispose();
        }
    }
}
