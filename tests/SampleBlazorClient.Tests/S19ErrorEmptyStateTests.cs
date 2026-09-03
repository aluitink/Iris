using Bunit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 19.8.7 S19 tests: error &amp; empty states. Each error/empty state on the sample client pages
/// shows a **clear message** (not a blank page or a raw error dump). These tests render the real pages
/// in-process (bUnit + the in-process SampleServer, the session injected via DI) and drive each state:
/// <list type="bullet">
/// <item><b>ObjectPage 404 / unknown object</b> — fetching a non-existent IRI surfaces a clear error
/// (not a blank page).</item>
/// <item><b>ObjectPage invalid IRI</b> — a non-absolute IRI input surfaces "not a valid absolute URI."</item>
/// <item><b>Actors page empty results</b> — a search with no matches shows "No matching actors or
/// content."</item>
/// <item><b>Feed page empty</b> — a freshly-logged-on actor with no follows shows "No followed items
/// yet."</item>
/// </list>
/// The proxy-fallback-failure and unreachable-instance states are exercised in the client-level tests
/// (S8) and are not re-driven here.
/// </summary>
public sealed class S19ErrorEmptyStateTests
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
    /// Creates a logged-on <c>ExplorerSession</c> against the in-process server.
    /// </summary>
    private static async Task<Iris.Samples.SampleBlazorClient.Explorer.ExplorerSession> CreateLoggedInSession(
        ServerFixture fixture,
        string handle = "alice")
    {
        var session = new Iris.Samples.SampleBlazorClient.Explorer.ExplorerSession(
            () => fixture.Server.CreateHandler());
        var ok = await session.LogOnAsync($"{handle}@localhost", Password, DialBase);
        if (!ok)
        {
            throw new InvalidOperationException($"Log on as {handle} failed.");
        }

        return session;
    }

    /// <summary>
    /// Renders a page with a logged-on session injected via bUnit DI.
    /// </summary>
    private static BunitContext CreateContext(
        Iris.Samples.SampleBlazorClient.Explorer.ExplorerSession session)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(session);
        return ctx;
    }

    /// <summary>
    /// ObjectPage 404 / unknown object: fetching a non-existent object IRI surfaces a clear error
    /// message (not a blank page). The test sets the IRI input and clicks Load.
    /// </summary>
    [Fact]
    public async Task ObjectPage_UnknownObject_SurfacesClearError()
    {
        using var fixture = new ServerFixture();
        using var session = await CreateLoggedInSession(fixture);
        using var ctx = CreateContext(session);

        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Pages.ObjectPage>();

        // Set the IRI input to a non-existent object and click Load.
        var input = cut.Find("#iri");
        input.Change(value: "http://localhost:5000/ap/v1/n/does-not-exist-404");
        cut.Find("button").Click();

        await cut.WaitForAssertionAsync(() =>
        {
            // The error div renders (not a blank page).
            var errorEls = cut.FindAll(".error");
            Assert.NotEmpty(errorEls);

            // The error message is a clear, human-readable string (not a raw stack trace / exception
            // type dump).
            var text = errorEls[0].TextContent.Trim();
            Assert.False(string.IsNullOrEmpty(text));
            Assert.DoesNotContain("System.", text);
            Assert.DoesNotContain("at ", text);
        });
    }

    /// <summary>
    /// ObjectPage invalid IRI: a non-absolute IRI input (e.g. a bare handle) surfaces "not a valid
    /// absolute URI." — a clear message, not a crash.
    /// </summary>
    [Fact]
    public async Task ObjectPage_InvalidIri_SurfacesClearError()
    {
        using var fixture = new ServerFixture();
        using var session = await CreateLoggedInSession(fixture);
        using var ctx = CreateContext(session);

        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Pages.ObjectPage>();

        var input = cut.Find("#iri");
        input.Change(value: "not-a-valid-iri");
        cut.Find("button").Click();

        await cut.WaitForAssertionAsync(() =>
        {
            var errorEls = cut.FindAll(".error");
            Assert.NotEmpty(errorEls);
            Assert.Contains("not a valid absolute URI", errorEls[0].TextContent);
        });
    }

    /// <summary>
    /// Actors page empty results: a search that matches nothing shows "No matching actors or
    /// content." — a clear empty state, not a blank page.
    /// </summary>
    [Fact]
    public async Task ActorsPage_EmptyResults_ShowsEmptyMessage()
    {
        using var fixture = new ServerFixture();
        using var session = await CreateLoggedInSession(fixture);
        using var ctx = CreateContext(session);

        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Pages.Actors>();

        var input = cut.Find("#query");
        input.Change(value: "zzz-no-such-actor-xyz-12345");
        cut.Find("button").Click();

        await cut.WaitForAssertionAsync(() =>
        {
            Assert.Contains("No matching actors or content", cut.Markup);
        });
    }

    /// <summary>
    /// Feed page empty: a freshly-logged-on actor with no follows shows "No followed items yet." — a
    /// clear empty state, not a blank page.
    /// </summary>
    [Fact]
    public async Task FeedPage_Empty_ShowsEmptyMessage()
    {
        using var fixture = new ServerFixture();
        using var session = await CreateLoggedInSession(fixture);
        using var ctx = CreateContext(session);

        var cut = ctx.Render<Iris.Samples.SampleBlazorClient.Pages.Feed>();

        // The feed loads on init (OnInitializedAsync). Wait for the empty-state message to render.
        await cut.WaitForAssertionAsync(() =>
        {
            Assert.Contains("No followed items yet", cut.Markup);
        });
    }
}
