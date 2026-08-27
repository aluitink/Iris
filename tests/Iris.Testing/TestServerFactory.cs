using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Testing;

/// <summary>
/// Builds in-process <see cref="TestServerInstance"/> instances for integration tests.
/// Each instance gets a distinct <c>*.domain.local</c> hostname, its own in-memory
/// persistence, and a default local actor with a known handle and Basic-auth credentials.
/// </summary>
public static class TestServerFactory
{
    /// <summary>
    /// The hostname suffix shared by all test instances.
    /// </summary>
    public const string HostnameSuffix = ".domain.local";

    /// <summary>
    /// Creates a single in-process server instance with the given hostname.
    /// </summary>
    /// <param name="hostname">
    /// The hostname for the instance (e.g. <c>a.domain.local</c>). Must be unique across a test's instances.
    /// </param>
    /// <param name="actorHandle">The handle of the default local actor (e.g. <c>alice</c>).</param>
    /// <param name="username">The Basic-auth username for the default local actor.</param>
    /// <param name="password">The Basic-auth password for the default local actor.</param>
    /// <returns>A running <see cref="TestServerInstance"/>.</returns>
    public static TestServerInstance CreateInstance(
        string hostname,
        string actorHandle = "alice",
        string username = "alice",
        string password = "correct-horse-battery")
    {
        ArgumentException.ThrowIfNullOrEmpty(hostname);

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.None);
        });
        services.AddRouting();

        // Register the minimal harness store so the pipeline has a resolvable persistence marker.
        // Register by the IHarnessStore interface (not the concrete type) so tests resolve it via
        // GetRequiredService<IHarnessStore>(). Phase 3 swaps this for the real InMemoryPersistenceProvider.
        services.AddSingleton<IHarnessStore>(new InMemoryHarnessStore(hostname));

        // Build the in-process web host. UseTestServer swaps the real Kestrel listener for an
        // in-memory one, so requests from the HttpClient traverse the full HTTP stack
        // (headers, routing, middleware) without binding a real socket. WebHostBuilder and
        // TestServer(IWebHostBuilder) are the only API that wires an IApplicationBuilder into
        // TestServer in a way that resolves correctly; both are obsolete in .NET 10, so the
        // specific deprecation codes are suppressed for this test-harness file only.
        var webHostBuilder = new WebHostBuilder()
            .UseTestServer()
            .ConfigureServices(s =>
            {
                foreach (var descriptor in services)
                {
                    s.Add(descriptor);
                }
            })
            .Configure(app =>
            {
                // No endpoints are mapped yet (Phase 3). The pipeline is a pass-through:
                // any request yields a 404, which still proves the full HTTP stack works.
            });

        var testServer = new TestServer(webHostBuilder);

        // Build a dedicated service provider from the same descriptors so tests can resolve
        // the harness store and (in later phases) the real persistence provider directly.
        // testServer.Services is the inner application provider and does not reliably surface
        // these additions, so we keep an independent provider for direct DI access in tests.
        var provider = services.BuildServiceProvider();

        return new TestServerInstance(
            testServer,
            hostname,
            actorHandle,
            username,
            password,
            provider);
    }
}
