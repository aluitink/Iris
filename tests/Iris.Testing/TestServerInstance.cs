using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Testing;

/// <summary>
/// A single in-process ActivityPub server instance backed by a
/// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> (in-memory HTTP transport), with its own
/// distinct <c>*.domain.local</c> hostname, in-memory persistence, and a wired-up
/// <see cref="System.Net.Http.HttpClient"/> so tests exercise the full HTTP stack.
/// </summary>
public sealed class TestServerInstance : IDisposable
{
    private readonly TestServer _server;
    private readonly ServiceProvider _services;

    internal TestServerInstance(
        TestServer server,
        string hostname,
        string actorHandle,
        string username,
        string password,
        ServiceProvider services)
    {
        _server = server;
        _services = services;
        Hostname = hostname;
        ActorHandle = actorHandle;
        Username = username;
        Password = password;
        BaseUri = new Uri($"https://{hostname}/");
        ActorIri = new Uri($"https://{hostname}/u/{actorHandle}");
        HttpClient = _server.CreateClient();
        HttpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// The distinct <c>*.domain.local</c> hostname for this instance (e.g. <c>a.domain.local</c>).
    /// </summary>
    public string Hostname { get; }

    /// <summary>
    /// The handle of the default local actor (e.g. <c>alice</c>).
    /// </summary>
    public string ActorHandle { get; }

    /// <summary>
    /// The Basic-auth username for the default local actor.
    /// </summary>
    public string Username { get; }

    /// <summary>
    /// The Basic-auth password for the default local actor.
    /// </summary>
    public string Password { get; }

    /// <summary>
    /// The instance's base URI (scheme + host).
    /// </summary>
    public Uri BaseUri { get; }

    /// <summary>
    /// The absolute IRI of the default local actor document.
    /// </summary>
    public Uri ActorIri { get; }

    /// <summary>
    /// A real <see cref="System.Net.Http.HttpClient"/> wired to this instance's in-process endpoint.
    /// </summary>
    public HttpClient HttpClient { get; }

    /// <summary>
    /// The instance's DI service provider (exposed for direct store access in tests).
    /// </summary>
    public IServiceProvider Services => _services;

    /// <inheritdoc/>
    public void Dispose()
    {
        _server.Dispose();
        _services.Dispose();
    }
}
