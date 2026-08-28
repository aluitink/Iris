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

    private readonly Iri AliceActorIri;
    private readonly Iri BobActorIri;

    public ProxyFallbackIntegrationTests()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();

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
        // A direct, unsigned GET to the proxy route (no Basic auth) is rejected with 401 — proving the
        // endpoint is the one wired (not a 404/405) and that authentication gates the forward.
        var unsigned = await ProxyGetAsync(BobActorIri, username: null, password: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        // The signed (Basic-auth) proxy GET succeeds (200), which only happens if B validated the
        // proxied GET's signature (resolving alice's key). A signed-but-unsigned-forward would 401.
        var signed = await ProxyGetAsync(BobActorIri, username: Alice, password: Password);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
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
        TestServer a, Iri target, string? username, string? password)
    {
        // Use CreateClient() (the pattern the other integration tests use). A relative path resolves
        // against the client's base address; the TestServer routes on the original (configured) host.
        var http = a.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/proxy/{target.Value}");
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
    {
        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri($"https://{host}");
                    opts.InstanceName = $"iris-{host}";
                    opts.InstanceActorId = new Iri($"https://{host}/ap/v1/u/{handle}");
                    if (proxySettings is not null)
                    {
                        opts.ProxySettings = proxySettings;
                    }
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(persistence.Keys);

                if (credentialValidator is not null)
                {
                    s.AddSingleton<IActorCredentialValidator>(credentialValidator);
                }

                if (deliveryTransport is { } transport)
                {
                    s.AddSingleton<Func<HttpMessageHandler>>(() => transport());
                }
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        var server = new TestServer(builder);

        // Register the local actor's key with the IKeyProvider so the proxy (which signs as the
        // authenticated actor via the X-Iris-Actor override) and the outbound DeliveryWorker can find
        // the key. The key IRI is the actor's publicKey.id (the #key-1 convention used by Seed).
        var keyProvider = server.Services.GetRequiredService<IKeyProvider>();
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, new Iri($"{actorIri}#key-1"));

        return server;
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that defers resolution of its inner handler until the first
    /// request (breaks the A↔B wiring chicken-and-egg; both servers exist by the time any request flows).
    /// </summary>
    private sealed class LazyHandler(Func<HttpMessageHandler> innerFactory) : HttpMessageHandler
    {
        private readonly Func<HttpMessageHandler> _innerFactory = innerFactory;
        private HttpMessageHandler? _inner;
        private HttpClient? _client;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _client ??= new HttpClient(_inner ??= _innerFactory(), disposeHandler: false);
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } content)
            {
                clone.Content = new ByteArrayContent(content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return _client.SendAsync(clone, cancellationToken);
        }
    }

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
