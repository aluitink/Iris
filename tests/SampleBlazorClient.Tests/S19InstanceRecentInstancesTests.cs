using Bunit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 19.8.1 gap (b) S19 tests: the Instance page's recent-instances list. The <c>/instance</c> page
/// now shows a "Recent instances" card (the session's <see cref="Iris.Samples.SampleBlazorClient.Explorer.ExplorerSession.RecentInstances"/>)
/// with one-click switching, mirroring the Home page's surface. The current instance (dial-base match)
/// is marked "(current)" instead of offering a switch button. These tests render the full <c>Instance</c>
/// page in-process (bUnit + the in-process SampleServer) and assert the recent-instances list renders.
/// </summary>
public sealed class S19InstanceRecentInstancesTests
{
    private static Uri DialBase => new("http://localhost:5000");

    private static string Password => Iris.Samples.SampleServer.SampleServer.Password;

    private sealed class ServerFixture : IDisposable
    {
        public ServerFixture()
        {
            Server = new TestServer(Iris.Samples.SampleServer.SampleServer.CreateWebHostBuilder());
        }

        public TestServer Server { get; }

        public void Dispose() => Server.Dispose();
    }

    /// <summary>
    /// Logs on to two actors (alice + bob) so the session's <c>RecentInstances</c> has two entries, then
    /// renders the <c>Instance</c> page and asserts the recent-instances list renders both entries with
    /// the current one (bob, the last logged on) marked "(current)".
    /// </summary>
    [Fact]
    public async Task InstancePage_RecentInstances_RendersListWithCurrentMarker()
    {
        using var fixture = new ServerFixture();
        using var session = new Iris.Samples.SampleBlazorClient.Explorer.ExplorerSession(
            () => fixture.Server.CreateHandler());

        var okAlice = await session.LogOnAsync("alice@localhost", Password, DialBase);
        Assert.True(okAlice);
        var okBob = await session.LogOnAsync("bob@localhost", Password, DialBase);
        Assert.True(okBob);
        Assert.Equal(2, session.RecentInstances.Count);

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton(session);

        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Pages.Instance>();

        // The recent-instances card renders with two entries.
        var recents = cut.FindAll("ul.recents li");
        Assert.Equal(2, recents.Count);

        // Both handles are listed.
        var text = cut.Markup;
        Assert.Contains("alice", text);
        Assert.Contains("bob", text);

        // The current instance (bob, the last logged on) is marked "(current)".
        Assert.Contains("(current)", text);
    }

    /// <summary>
    /// A single recent instance (only one logon) renders the list with the current one marked
    /// "(current)" and no switch button (it's the current identity).
    /// </summary>
    [Fact]
    public async Task InstancePage_SingleRecentInstance_CurrentMarkedNoSwitchButton()
    {
        using var fixture = new ServerFixture();
        using var session = new Iris.Samples.SampleBlazorClient.Explorer.ExplorerSession(
            () => fixture.Server.CreateHandler());

        var ok = await session.LogOnAsync("alice@localhost", Password, DialBase);
        Assert.True(ok);
        Assert.Single(session.RecentInstances);

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton(session);

        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Pages.Instance>();

        var recents = cut.FindAll("ul.recents li");
        Assert.Single(recents);
        Assert.Contains("(current)", cut.Markup);

        // No "Use" or "Switch" button for the current instance.
        Assert.Empty(cut.FindAll("ul.recents li button"));
    }
}
