using System.Text;
using Iris.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Client.Tests;

/// <summary>
/// A minimal in-process ActivityPub server (backed by <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>)
/// that serves the endpoints <see cref="Iris.Client.ActivityPubClient"/> exercises: the actor document,
/// WebFinger, and a multi-page outbox collection. It records every request (method, path, and selected
/// headers) so integration tests can assert that the real client sent the correct
/// <c>Signature</c>, <c>Accept</c>, and <c>Content-Type</c> headers over a genuine HTTP stack.
/// </summary>
/// <remarks>
/// This is the "server" half of the client ↔ live-<c>TestServer</c> integration tests. It is a *fake*
/// ActivityPub server (it does not validate signatures or persist) — its job is to serve well-formed
/// ActivityPub documents so the *real* <see cref="Iris.Client.ActivityPubClient"/> (built by the real
/// <see cref="Iris.Client.ActivityPubClientFactory"/>, with its signing/JsonLd/retry/cache pipeline) can be
/// exercised end-to-end. A full-fidelity Iris server with signature validation lands in Phase 3/4.
/// </remarks>
public sealed class FakeActivityPubServer : IDisposable
{
    private readonly TestServer _server;
    private readonly RequestRecorder _recorder;
    private readonly FlakyGate? _flakyGate;

    private FakeActivityPubServer(
        TestServer server,
        RequestRecorder recorder,
        FlakyGate? flakyGate,
        string hostname,
        string actorHandle)
    {
        _server = server;
        _recorder = recorder;
        _flakyGate = flakyGate;
        Hostname = hostname;
        ActorHandle = actorHandle;
        BaseUri = new Uri($"https://{hostname}/");
        ActorIri = new Iri($"https://{hostname}/u/{actorHandle}");
        OutboxIri = new Iri($"https://{hostname}/u/{actorHandle}/outbox");
        // The handler the factory will wrap with the real client pipeline.
        Handler = _server.CreateHandler();
    }

    /// <summary>The distinct <c>*.domain.local</c> hostname (e.g. <c>b.domain.local</c>).</summary>
    public string Hostname { get; }

    /// <summary>The handle of the single local actor (e.g. <c>bob</c>).</summary>
    public string ActorHandle { get; }

    /// <summary>The instance base URI.</summary>
    public Uri BaseUri { get; }

    /// <summary>The absolute IRI of the local actor document.</summary>
    public Iri ActorIri { get; }

    /// <summary>The absolute IRI of the local actor's outbox collection.</summary>
    public Iri OutboxIri { get; }

    /// <summary>
    /// The <see cref="HttpMessageHandler"/> that talks to this instance's in-process endpoint. Pass it
    /// as the transport to <see cref="Iris.Client.IActivityPubClientFactory.Create"/> so the real
    /// client pipeline (retry → JsonLd → signing) wraps a genuine HTTP round-trip.
    /// </summary>
    public HttpMessageHandler Handler { get; }

    /// <summary>The recorded requests, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _recorder.All;

    /// <summary>The number of times a given path (ignoring query) has been requested.</summary>
    public int HitsOnPath(string path)
        => Requests.Count(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds a running fake server with the given hostname and actor handle.
    /// </summary>
    /// <param name="hostname">A unique <c>*.domain.local</c> hostname for this instance.</param>
    /// <param name="actorHandle">The handle of the local actor (e.g. <c>bob</c>).</param>
    /// <param name="flaky">
    /// When true, the first request to each path returns a transient 503 and subsequent requests are
    /// served normally — used to prove the client's <see cref="Iris.Client.RetryHandler"/> retries over a
    /// real HTTP stack.
    /// </param>
    public static FakeActivityPubServer Start(string hostname, string actorHandle = "bob", bool flaky = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostname);

        var recorder = new RequestRecorder();
        var flakyGate = flaky ? new FlakyGate() : null;
        var webHostBuilder = new WebHostBuilder()
            .UseTestServer()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(services => services.AddSingleton(recorder))
            .Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    context.RequestServices.GetRequiredService<RequestRecorder>().Record(context.Request);
                    await next();
                });

                // Terminal handler: route by path manually (no minimal-API endpoint routing needed).
                app.Run(async context =>
                {
                    var path = context.Request.Path.Value ?? "/";

                    // Flaky mode: the first hit on a path is a transient 503; later hits are served.
                    if (flakyGate is not null && flakyGate.FirstHit(path))
                    {
                        context.Response.StatusCode = 503;
                        return;
                    }

                    var (status, json) = Route(path, context, hostname, actorHandle);
                    context.Response.StatusCode = status;
                    if (status == 200)
                    {
                        await context.Response.WriteActivityJsonAsync(json);
                    }
                });
            });

        var testServer = new TestServer(webHostBuilder);
        return new FakeActivityPubServer(testServer, recorder, flakyGate, hostname, actorHandle);
    }

    /// <inheritdoc/>
    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Maps a request path to a (status, JSON) pair. Returns 404 for unknown paths.
    /// </summary>
    private static (int Status, string Json) Route(string path, HttpContext context, string host, string handle)
    {
        if (string.Equals(path, $"/u/{handle}", StringComparison.OrdinalIgnoreCase))
        {
            return (200, ActorDoc(host, handle));
        }

        if (string.Equals(path, "/.well-known/webfinger", StringComparison.OrdinalIgnoreCase))
        {
            var resource = context.Request.Query["resource"].ToString();
            if (!string.Equals(resource, $"acct:{handle}@{host}", StringComparison.OrdinalIgnoreCase))
            {
                return (404, string.Empty);
            }

            return (200, WebFingerDoc(host, handle));
        }

        if (string.Equals(path, $"/u/{handle}/outbox", StringComparison.OrdinalIgnoreCase))
        {
            return (200, CollectionDoc(host, handle));
        }

        if (string.Equals(path, $"/u/{handle}/outbox/first", StringComparison.OrdinalIgnoreCase))
        {
            return (200, Page1Doc(host, handle));
        }

        if (string.Equals(path, $"/u/{handle}/outbox/2", StringComparison.OrdinalIgnoreCase))
        {
            return (200, Page2Doc(host, handle));
        }

        return (404, string.Empty);
    }

    /// <summary>Records a single observed request.</summary>
    public sealed record RecordedRequest(string Method, string Path, string? Signature, string? Accept, string? ContentType)
    {
        /// <summary>True if the request carried a non-empty <c>Signature</c> header.</summary>
        public bool IsSigned => !string.IsNullOrWhiteSpace(Signature);
    }

    // --- Wire documents --------------------------------------------------------

    private static string ActorDoc(string host, string handle) => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "https://{{host}}/u/{{handle}}",
          "type": "Person",
          "name": "{{handle}}",
          "preferredUsername": "{{handle}}",
          "inbox": "https://{{host}}/u/{{handle}}/inbox",
          "publicKey": {
            "id": "https://{{host}}/u/{{handle}}#key-1",
            "owner": "https://{{host}}/u/{{handle}}",
            "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8A\n-----END PUBLIC KEY-----"
          }
        }
        """;

    private static string WebFingerDoc(string host, string handle) => $$"""
        {
          "subject": "acct:{{handle}}@{{host}}",
          "links": [
            {
              "rel": "self",
              "type": "application/activity+json",
              "href": "https://{{host}}/u/{{handle}}"
            }
          ]
        }
        """;

    private static string CollectionDoc(string host, string handle) => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "https://{{host}}/u/{{handle}}/outbox",
          "type": "OrderedCollection",
          "totalItems": 3,
          "first": "https://{{host}}/u/{{handle}}/outbox/first"
        }
        """;

    private static string Page1Doc(string host, string handle) => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "https://{{host}}/u/{{handle}}/outbox/first",
          "type": "OrderedCollectionPage",
          "partOf": "https://{{host}}/u/{{handle}}/outbox",
          "totalItems": 3,
          "startIndex": 1,
          "items": [
            { "id": "https://{{host}}/n/1", "type": "Note", "content": "one" },
            { "id": "https://{{host}}/n/2", "type": "Note", "content": "two" }
          ],
          "next": "https://{{host}}/u/{{handle}}/outbox/2"
        }
        """;

    private static string Page2Doc(string host, string handle) => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "https://{{host}}/u/{{handle}}/outbox/2",
          "type": "OrderedCollectionPage",
          "partOf": "https://{{host}}/u/{{handle}}/outbox",
          "totalItems": 3,
          "startIndex": 3,
          "items": [
            { "id": "https://{{host}}/n/3", "type": "Note", "content": "three" }
          ],
          "prev": "https://{{host}}/u/{{handle}}/outbox/first"
        }
        """;
}

/// <summary>
/// Tracks which paths have been hit, so a flaky <see cref="FakeActivityPubServer"/> can return 503 on
/// the first hit per path and serve normally afterwards.
/// </summary>
internal sealed class FlakyGate
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns true exactly on the first hit for a path (false on subsequent hits).</summary>
    public bool FirstHit(string path)
    {
        lock (_seen)
        {
            return _seen.Add(path);
        }
    }
}

/// <summary>
/// Captures observed requests so a <see cref="FakeActivityPubServer"/> can expose them for assertions.
/// </summary>
internal sealed class RequestRecorder
{
    private readonly List<FakeActivityPubServer.RecordedRequest> _requests = [];

    /// <summary>Records an incoming request's method, path, and selected headers.</summary>
    public void Record(HttpRequest request)
    {
        _requests.Add(new FakeActivityPubServer.RecordedRequest(
            request.Method,
            request.Path.Value ?? "/",
            request.Headers.TryGetValue("Signature", out var signature) ? signature.ToString() : null,
            request.Headers.TryGetValue("Accept", out var accept) ? accept.ToString() : null,
            request.ContentType));
    }

    /// <summary>All recorded requests, in order.</summary>
    public IReadOnlyList<FakeActivityPubServer.RecordedRequest> All => _requests;
}

/// <summary>
/// Extension that writes a JSON string as an <c>application/activity+json</c> response.
/// </summary>
internal static class HttpResponseActivityJsonExtensions
{
    public static Task WriteActivityJsonAsync(this HttpResponse response, string json)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ActivityJson.ActivityJsonContentType;
        return response.WriteAsync(json);
    }
}
