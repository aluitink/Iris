using System.Net.Http;
using Iris.Core;
using Iris.Samples.SampleBlazorClient;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleServer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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
}
