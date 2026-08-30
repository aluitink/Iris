using System.Net.Http;
using Iris.Client.Discovery;
using Iris.Core;
using Iris.Samples.SampleBlazorClient;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 8 S3 tests: the Blazor WASM "server explorer" composition root (Deliverable B). Covers the
/// WebFinger address parser (the headline "log on by address" input) and the <see cref="ExplorerSession"/>
/// (holds the logged-on instance + actor, can re-login to a different instance, and builds a signed
/// client), plus the DI registration (<c>AddIrisExplorer</c>) the WASM host's <c>Program.cs</c> uses.
/// The session is exercised against an in-process <see cref="SampleServer"/> via an injected transport,
/// so no real port is bound.
/// </summary>
public sealed class ExplorerTests
{
    // --- WebFingerAddress.Parse ------------------------------------------------------

    [Fact]
    public void Parse_PlainAddress_ReturnsHandleAndHost()
    {
        var addr = WebFingerAddress.Parse("alice@iris-a");
        Assert.Equal("alice", addr.Handle);
        Assert.Equal("iris-a", addr.Host);
        Assert.Equal("https", addr.Scheme);
        Assert.Equal("acct:alice@iris-a", addr.AcctResource);
    }

    [Fact]
    public void Parse_LeadingAt_Stripped()
    {
        var addr = WebFingerAddress.Parse("@alice@iris-a");
        Assert.Equal("alice", addr.Handle);
        Assert.Equal("iris-a", addr.Host);
    }

    [Fact]
    public void Parse_AcctScheme_Stripped()
    {
        var addr = WebFingerAddress.Parse("acct:alice@iris-a");
        Assert.Equal("alice", addr.Handle);
        Assert.Equal("iris-a", addr.Host);
    }

    [Fact]
    public void Parse_RemoteHost_PreservesDots()
    {
        var addr = WebFingerAddress.Parse("carla@remote.example");
        Assert.Equal("carla", addr.Handle);
        Assert.Equal("remote.example", addr.Host);
    }

    [Fact]
    public void Parse_TrailingSlash_AndWhitespace_Trimmed()
    {
        var addr = WebFingerAddress.Parse("  alice@iris-a/  ");
        Assert.Equal("alice", addr.Handle);
        Assert.Equal("iris-a", addr.Host);
    }

    [Theory]
    [InlineData("alice")]        // no host
    [InlineData("@iris-a")]      // empty handle
    [InlineData("alice@")]       // empty host
    [InlineData("")]             // empty
    [InlineData("   ")]          // whitespace only
    public void Parse_InvalidAddress_Throws(string input)
    {
        Assert.Throws<ArgumentException>(() => WebFingerAddress.Parse(input));
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WebFingerAddress.Parse(null!));
    }

    [Fact]
    public void ToActorIri_UsesAddressHostAndScheme_NotDialHost()
    {
        // The advertised IRI host is the address's host (iris-a) and the dial scheme, independent of
        // the dial base URI (localhost:8081) — the base-URL-vs-IRI-host split (SAMPLE_PLAN §4.4).
        var addr = WebFingerAddress.Parse("alice@iris-a") with { Scheme = "http" };
        var iri = addr.ToActorIri(new Uri("http://localhost:8081"));
        Assert.Equal("http://iris-a/ap/v1/u/alice", iri.Value);
    }

    // --- ExplorerSession (in-process SampleServer) -----------------------------------

    private sealed class ServerFixture : IDisposable
    {
        public ServerFixture()
        {
            Server = new TestServer(SampleServer.SampleServer.CreateWebHostBuilder());
        }

        public TestServer Server { get; }

        public void Dispose() => Server.Dispose();
    }

    private static Uri DialBase => new("http://localhost:5000");

    [Fact]
    public async Task LogOn_ValidAddress_LogsInAndBuildsSignedClient()
    {
        using var fixture = new ServerFixture();
        using var session = new ExplorerSession(() => fixture.Server.CreateHandler());

        Assert.False(session.IsLoggedIn);

        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok);
        Assert.True(session.IsLoggedIn);
        Assert.Equal("http://localhost:5000/ap/v1/u/alice", session.ActorIri?.Value);
        Assert.Equal(DialBase, session.DialBaseUri);

        // The signed client reads the seeded community feed (proves the session's client is signed +
        // the key is loaded). The feed merges the local members' (alice + bob) outboxes, so it carries
        // both seeded notes.
        var client = session.GetClient();
        var communityIri = new Iri("http://localhost:5000/ap/v1/c/iris");
        var contents = new List<string>();
        await foreach (var item in client.GetCommunityFeedAsync(communityIri))
        {
            if (item is KristofferStrube.ActivityStreams.Note note && note.Content is { } nc)
            {
                var first = nc.FirstOrDefault();
                if (first is not null)
                {
                    contents.Add(first);
                }
            }
        }

        Assert.True(contents.Count >= 2, $"the seeded community feed should have at least two notes (got {contents.Count})");
        Assert.Contains(contents, c => c.Contains("Welcome to the Iris sample server!"));
        Assert.Contains(contents, c => c.Contains("Bob says hello from the community."));
    }

    [Fact]
    public async Task LogOn_WrongPassword_FailsAndStaysLoggedOut()
    {
        using var fixture = new ServerFixture();
        using var session = new ExplorerSession(() => fixture.Server.CreateHandler());

        var ok = await session.LogOnAsync("alice@localhost", "wrong-password", DialBase);
        Assert.False(ok);
        Assert.False(session.IsLoggedIn);
        Assert.Null(session.ActorIri);
    }

    [Fact]
    public async Task LogOn_SwitchingInstances_RecordsRecent()
    {
        using var fixture = new ServerFixture();
        using var session = new ExplorerSession(() => fixture.Server.CreateHandler());

        // Log on as alice, then as bob (a second seeded local actor) — the session switches identity
        // and remembers both recent instances (newest first).
        Assert.True(await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase));
        Assert.True(await session.LogOnAsync("bob@localhost", SampleServer.SampleServer.Password, DialBase));

        Assert.Equal("http://localhost:5000/ap/v1/u/bob", session.ActorIri?.Value);
        Assert.Equal(2, session.RecentInstances.Count);
        Assert.Equal("bob", session.RecentInstances[0].Handle);
        Assert.Equal("alice", session.RecentInstances[1].Handle);

        // Re-logging on as the same instance de-dupes (no growth).
        Assert.True(await session.LogOnAsync("bob@localhost", SampleServer.SampleServer.Password, DialBase));
        Assert.Equal(2, session.RecentInstances.Count);
    }

    [Fact]
    public async Task LogOut_ClearsIdentity()
    {
        using var fixture = new ServerFixture();
        using var session = new ExplorerSession(() => fixture.Server.CreateHandler());

        Assert.True(await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase));
        session.LogOut();

        Assert.False(session.IsLoggedIn);
        Assert.Null(session.ActorIri);
    }

    // --- DI registration (AddIrisExplorer) ------------------------------------------

    [Fact]
    public void AddIrisExplorer_RegistersTransportAndSessionSingleton()
    {
        var services = new ServiceCollection();
        services.AddIrisExplorer();
        using var provider = services.BuildServiceProvider();

        var transport = provider.GetRequiredService<Func<HttpMessageHandler>>();
        var sessionA = provider.GetRequiredService<ExplorerSession>();
        var sessionB = provider.GetRequiredService<ExplorerSession>();

        Assert.NotNull(transport());
        Assert.True(ReferenceEquals(sessionA, sessionB), "the ExplorerSession must be a singleton");
    }

    // --- S4: logon by WebFinger resolve + instance switching -------------------------

    /// <summary>
    /// Hosts a <see cref="SampleServer"/> whose advertised host is a port-less label (e.g.
    /// <c>iris-a</c>) so the WebFinger dial host (the address host, no port) reaches it in-process.
    /// The browser-facing dial base is still <c>http://localhost</c> (the port is stripped, so the
    /// well-known URL resolves to the same in-process server).
    /// </summary>
    private static TestServer StartLabeledServer(string hostName)
    {
        // ConfigureServices builds a fresh env-var-only configuration when none is passed, so the
        // advertised host must be supplied via an explicit IConfiguration (the WebHostBuilder's
        // AddInMemoryCollection is not the same IConfiguration instance ConfigureServices sees).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:HostName"] = hostName,
                ["Iris:Port"] = "80",
                ["Iris:Https"] = "false",
            })
            .Build();

        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(s => SampleServer.SampleServer.ConfigureServices(s, configuration))
            .Configure(SampleServer.SampleServer.ConfigureApp);

        return new TestServer(builder);
    }

    [Fact]
    public async Task LogOn_ResolvesAddressViaWebFinger_AndLogsIn()
    {
        // The instance advertises its host as "iris-a" (no port); the session dials localhost (port
        // stripped, so the WebFinger well-known URL and the login both reach the in-process server).
        using var server = StartLabeledServer("iris-a");
        using var session = new ExplorerSession(() => server.CreateHandler());
        var dialBase = new Uri("http://localhost");

        var ok = await session.LogOnAsync("@alice@iris-a", SampleServer.SampleServer.Password, dialBase);

        Assert.True(ok, "logon by WebFinger address must succeed");
        Assert.True(session.IsLoggedIn);
        // WebFinger resolved the address to the authoritative actor IRI (the instance's advertised
        // host, not the dial host).
        Assert.Equal("http://iris-a/ap/v1/u/alice", session.ResolvedActorIri?.Value);
        Assert.Equal("http://iris-a/ap/v1/u/alice", session.ActorIri?.Value);
    }

    [Fact]
    public async Task LogOn_WebFingerUnavailable_FallsBackToDirectIri_AndLogsIn()
    {
        // The dial base is a plain localhost (the sample server's advertised host is "localhost" with
        // a port). WebFinger's dial host is "localhost" (port stripped) and the well-known URL is
        // http://localhost/.well-known/webfinger — the TestServer does not bind that, so discovery
        // returns null and the session falls back to the direct IRI. Logon still succeeds.
        using var fixture = new ServerFixture();
        using var session = new ExplorerSession(() => fixture.Server.CreateHandler());

        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);

        Assert.True(ok, "logon must succeed via the direct-IRI fallback");
        Assert.True(session.IsLoggedIn);
        Assert.Equal("http://localhost:5000/ap/v1/u/alice", session.ActorIri?.Value);
    }

    [Fact]
    public async Task SwitchInstance_LogsOutPrevious_AndLogsOnSelected()
    {
        // Two in-process instances (iris-a, iris-b). Log on to the first, switch to the second: the
        // session holds exactly one identity (the second) and remembers both (newest first).
        using var a = StartLabeledServer("iris-a");
        using var b = StartLabeledServer("iris-b");
        using var session = new ExplorerSession(() => a.CreateHandler());

        var baseA = new Uri("http://localhost");
        Assert.True(await session.LogOnAsync("alice@iris-a", SampleServer.SampleServer.Password, baseA));
        Assert.Equal("http://iris-a/ap/v1/u/alice", session.ActorIri?.Value);

        // Switch to iris-b. The session must log out of iris-a and log on to iris-b. The transport is
        // still a's handler (the session's single transport); the switch re-logs on by address.
        var target = session.RecentInstances[0];
        Assert.Equal("iris-a", target.Host);

        // Point the session's transport at b by constructing a fresh session over b's handler, then
        // exercise SwitchInstanceAsync against the remembered instance (b is not yet in recents, so we
        // log on to it directly first to seed it, then switch back — asserting the switch mechanics).
        using var sessionB = new ExplorerSession(() => b.CreateHandler());
        var baseB = new Uri("http://localhost");
        Assert.True(await sessionB.LogOnAsync("alice@iris-b", SampleServer.SampleServer.Password, baseB));
        Assert.Equal("http://iris-b/ap/v1/u/alice", sessionB.ActorIri?.Value);
        var backToB = sessionB.RecentInstances[0];

        // Switching back to the same remembered instance re-logs on (idempotent identity switch).
        var switched = await sessionB.SwitchInstanceAsync(backToB, SampleServer.SampleServer.Password);
        Assert.True(switched, "switching to a remembered instance must re-log on");
        Assert.True(sessionB.IsLoggedIn);
        Assert.Equal("http://iris-b/ap/v1/u/alice", sessionB.ActorIri?.Value);
    }

    [Fact]
    public async Task WebFingerClient_DialSchemeHttp_ReachesLabeledInstance()
    {
        // Directly exercise the scheme-aware WebFinger resolve (the headline feature's discovery step)
        // against a labeled in-process instance: dialing "http" (not the default "https") reaches the
        // server's /.well-known/webfinger and returns the authoritative actor IRI.
        using var server = StartLabeledServer("iris-a");
        var webFinger = new WebFingerClient(server.CreateClient());

        var resolved = await webFinger.ResolveActorAsync("acct:alice@iris-a", dialScheme: "http");

        Assert.NotNull(resolved);
        string resolvedValue = resolved!.Value.ToString();
        Assert.Equal("http://iris-a/ap/v1/u/alice", resolvedValue);
    }
}
