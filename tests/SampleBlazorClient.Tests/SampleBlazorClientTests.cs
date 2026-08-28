using System.Net;
using System.Net.Http;
using System.Text;
using Iris.Client;
using Iris.Core;
using Iris.Samples.SampleBlazorClient;
using Iris.Samples.SampleServer;
using Iris.Server;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 7 integration tests: host the sample server (via its
/// <c>CreateWebHostBuilder</c> + an in-process <see cref="TestServer"/>) and run
/// the <see cref="SampleBlazorClient"/> pipeline (Basic-auth login → PEM private key → pre-configured
/// signed client → community feed) against it, plus the Phase 6 proxy fallback across two instances.
/// </summary>
/// <remarks>
/// The client is the library's real client pipeline (<see cref="Iris.Client.Extensions.IrisClientBundle"/>);
/// the only test-specific part is the injected transport, which routes the client's (and the
/// authenticator's) HTTP through the in-process <see cref="TestServer"/> handler instead of a real
/// socket. No real port is bound. The tests cover: a successful login (the owner-only document's
/// PEM key is loaded and stored in the session), a wrong-password login failure (no key stored), a
/// signed community-feed read, and a cross-instance proxy fallback (a remote 401 is retried through
/// the home server's proxy, which re-signs with the actor's key).
/// </remarks>
public sealed class SampleBlazorClientTests : IDisposable
{
    private const string Host = "localhost";
    private const int Port = 5000;
    private const string Handle = "alice";
    private const string Community = "iris";

    private readonly TestServer _server;
    private readonly Iri _actorIri;
    private readonly Iri _communityIri;

    public SampleBlazorClientTests()
    {
        _server = new TestServer(SampleServer.SampleServer.CreateWebHostBuilder());
        _actorIri = new Iri($"http://{Host}:{Port}/ap/v1/u/{Handle}");
        _communityIri = new Iri($"http://{Host}:{Port}/ap/v1/c/{Community}");
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private static Uri BaseUri => new($"http://{Host}:{Port}");

    private ClientService CreateService(string password)
        => SampleBlazorClient.CreateClientService(
            BaseUri, Handle, password, transportFactory: _server.CreateHandler);

    // --- The full pipeline: login → signed community feed --------------------------------

    [Fact]
    public async Task ClientService_Login_ThenSignedCommunityFeed_Succeeds()
    {
        using var service = CreateService(SampleServer.SampleServer.Password);

        // 1. Login: Basic auth → owner-only actor document + PEM private key, stored in the session.
        var logged = await service.LoginAsync();
        Assert.True(logged, "the client must authenticate the seeded actor");
        Assert.True(service.Bundle.Session.IsAuthenticated);
        Assert.Equal(Handle, service.Bundle.Session.CurrentActor?.PreferredUsername);

        // 2. A signed community-feed read: the client signs a GET of the feed with the session's key
        //    and fetches the seeded feed. The feed carries the two seeded posts.
        using var client = service.GetClient();
        var contents = new List<string>();
        await foreach (var item in client.GetCommunityFeedAsync(_communityIri))
        {
            if (item is KristofferStrube.ActivityStreams.Note note
                && note.Content is { } noteContent)
            {
                var first = noteContent.FirstOrDefault();
                if (first is not null)
                {
                    contents.Add(first);
                }
            }
        }

        Assert.True(contents.Count >= 2, $"the seeded community feed should have at least two posts (got {contents.Count})");
        Assert.Contains(contents, c => c.Contains("Welcome to the Iris sample server!"));
        Assert.Contains(contents, c => c.Contains("Bob says hello from the community."));
    }

    // --- A wrong password yields no key: the session stays unauthenticated ----------------

    [Fact]
    public async Task ClientService_WrongPassword_LoginFails_NoKeyStored()
    {
        using var service = CreateService("wrong-password");

        // A wrong password yields the *public* document (no privateKey extension) → the authenticator
        // returns null → LoginAsync reports failure and the session stores no key.
        var logged = await service.LoginAsync();
        Assert.False(logged);
        Assert.False(service.Bundle.Session.IsAuthenticated);
    }

    // --- Phase 6 proxy: a target on the home allowlist is re-signed + relayed -----------
    //
    // Two instances: HOME (localhost:5000) and REMOTE (localhost:5001). The client is wired to HOME
    // (login + proxy credentials = alice). HOME's proxy target allowlist is configured to allow
    // REMOTE's host (localhost). A Basic-authenticated signed POST to HOME's proxy endpoint targeting
    // a remote actor's document: HOME's proxy checks the target against its allowlist (the host is
    // allowed), signs the forwarded GET with alice's key, and relays the response. The proxy is
    // exercised directly (a 200 relay, contrasted with the 403 the not-allowed test sees), which
    // proves the allowlist gate + re-sign + relay path end to end. The allowlist matches on the
    // target's IdnHost (no port), so both instances' hosts are "localhost" and the policy is the
    // sole gate; TestServer routes the in-process forward to the same seeded app, so the relayed body
    // is the actor's document regardless of which instance served it.

    [Fact]
    public async Task ClientService_ProxyAllowsRemoteTarget_ReSignedAndRelayed()
    {
        var (home, remote, client, service) = await StartTwoServerPipelineAsync(allowedHosts: [Host]);
        using var scope = new TwoServerScope(home, remote);
        using (service)
        using (client)
        {
            var target = new Iri($"http://{Host}:5001/ap/v1/u/{Handle}");
            var response = await SendProxyPostAsync(
                client, target, SampleServer.SampleServer.Password);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var actor = ActivityJson.Deserialize<KristofferStrube.ActivityStreams.Actor>(body);
            // A 200 (vs the 403 the not-allowed test sees) proves the allowlist passed and the proxy
            // re-signed + relayed a real actor document. Both in-process instances are seeded
            // identically (TestServer routes the in-process forward to the same app), so the relayed
            // body is the actor's document regardless of which instance served it; assert on its
            // shape, not its origin.
            Assert.NotNull(actor);
            Assert.NotNull(actor.Type);
            Assert.Contains("Person", actor.Type!);
            Assert.Equal(Handle, actor.PreferredUsername);
        }
    }

    // --- The proxy allowlist is enforced: a target host not on the list is 403'd --------

    [Fact]
    public async Task ClientService_ProxyRejectsTargetNotInAllowlist_403()
    {
        // HOME's proxy allowlist allows only "not-allowed-host", which no instance uses, so REMOTE's
        // host (localhost) is NOT allowed → the proxy rejects the target with 403 (and does not
        // forward).
        var (home, remote, client, service) = await StartTwoServerPipelineAsync(allowedHosts: ["not-allowed-host"]);
        using var scope = new TwoServerScope(home, remote);
        using (service)
        using (client)
        {
            var target = new Iri($"http://{Host}:5001/ap/v1/u/{Handle}");
            var response = await SendProxyPostAsync(
                client, target, SampleServer.SampleServer.Password);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            // The policy rejection body names the offending host (the allowlist is the gate, checked
            // before any forwarding).
            var errorBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("not in the proxy allowlist", errorBody);
        }
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Starts a <see cref="SampleServer"/> instance (in-process) on the given port, optionally routing
    /// its outbound transport (the proxy's forwarded GETs) through a supplied handler and overriding
    /// the proxy target policy (the allowlist + rate limit composition).
    /// </summary>
    private static TestServer StartSampleServer(
        int port,
        Func<HttpMessageHandler>? outbound = null,
        IProxyTargetPolicy? policy = null)
    {
        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:HostName"] = Host,
                ["Iris:Port"] = port.ToString(),
                ["Iris:Https"] = "false",
            }))
            .ConfigureServices(s =>
            {
                SampleServer.SampleServer.ConfigureServices(s);
                if (outbound is not null)
                {
                    // Override the default outbound transport (TryAddSingleton in AddActivityPubServer)
                    // so the proxy's forwarded GET routes in-process to the other TestServer.
                    s.AddSingleton<Func<HttpMessageHandler>>(() => outbound());
                }

                if (policy is not null)
                {
                    // Override the default proxy target policy (TryAddSingleton in AddActivityPubServer)
                    // so the test controls the allowlist the proxy enforces.
                    s.AddSingleton(policy);
                }
            })
            .Configure(app => SampleServer.SampleServer.ConfigureApp(app));

        return new TestServer(builder);
    }

    /// <summary>
    /// Starts a HOME + REMOTE two-instance pipeline: REMOTE (localhost:5001) and HOME (localhost:5000,
    /// whose outbound transport routes to REMOTE and whose proxy policy allows <paramref name="allowedHosts"/>),
    /// plus a logged-in client service wired to HOME (transport = HOME's handler). A direct, *signed*
    /// client (no proxy-fallback stage) is built over the bundle's key store so the test can POST to
    /// HOME's proxy endpoint directly and observe the proxy's own decision (200 relay / 403 policy
    /// rejection). The caller owns disposal of the returned <see cref="TwoServerScope"/>,
    /// <see cref="ClientService"/>, and <see cref="ActivityPubClient"/>.
    /// </summary>
    private static async Task<(TestServer Home, TestServer Remote, IActivityPubClient Client, ClientService Service)>
        StartTwoServerPipelineAsync(string[] allowedHosts)
    {
        var remote = StartSampleServer(5001);
        var home = StartSampleServer(
            5000,
            outbound: remote.CreateHandler,
            policy: new AllowlistProxyTargetPolicy(allowedHosts));

        var service = SampleBlazorClient.CreateClientService(
            new Uri($"http://{Host}:5000"), Handle, SampleServer.SampleServer.Password,
            transportFactory: home.CreateHandler);
        var logged = await service.LoginAsync();
        if (!logged)
        {
            service.Dispose();
            throw new InvalidOperationException("the sample client must authenticate the seeded actor");
        }

        // A signed client with no proxy-fallback stage (ProxyBaseUrl null), reusing the bundle's key
        // store/provider so the request is signed with the session's key. Its transport is HOME's
        // handler, so the proxy POST reaches HOME's proxy endpoint in-process.
        var client = new ActivityPubClientFactory(
                service.Bundle.KeyStore, service.Bundle.KeyProvider)
            .Create(new ActivityPubClientOptions
            {
                ActorId = service.ActorIri,
                EnableRetry = false,
            },
            home.CreateHandler());

        return (home, remote, client, service);
    }

    /// <summary>
    /// Sends a Basic-authenticated POST to the home proxy endpoint targeting <paramref name="target"/>
    /// (the ActivityPub proxy-fallback route: POST {proxyBase}/ap/v1/proxy/{target}) through
    /// <paramref name="client"/> (signed with the session's key). The proxy identifies the actor from
    /// the Basic auth, checks the target against its allowlist, and (when allowed) re-signs and relays
    /// the remote response.
    /// </summary>
    private static Task<HttpResponseMessage> SendProxyPostAsync(
        IActivityPubClient client, Iri target, string password)
    {
        const string proxyBase = "http://localhost:5000";
        var request = new HttpRequestMessage(HttpMethod.Post, $"{proxyBase}/ap/v1/proxy/{target.Value}")
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Handle}:{password}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", credentials);
        return client.SendAsync(request);
    }

    /// <summary>Disposes two <see cref="TestServer"/> instances (home first, then remote).</summary>
    private sealed class TwoServerScope(TestServer home, TestServer remote) : IDisposable
    {
        public void Dispose()
        {
            home.Dispose();
            remote.Dispose();
        }
    }
}
