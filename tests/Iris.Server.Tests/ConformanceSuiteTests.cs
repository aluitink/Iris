using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.6 — the conformance test suite. These tests regression-protect the wire-format,
/// header, and status-code behaviors the specs require, per the per-item test list in
/// <c>docs/MISSING_FEATURES.md</c> §4:
/// </summary>
/// <list type="bullet">
/// <item>WebFinger (RFC 8615 / 8410): the response is a JRD document served as <c>application/jrd+json</c>
/// with a <c>subject</c> and a <c>self</c> link whose <c>type</c> is the ActivityStreams media type.</item>
/// <item>NodeInfo 2.0: the document carries <c>version "2.0"</c>, a <c>software</c> name+version, a
/// <c>protocols</c> array, and <c>usage</c> / <c>openRegistrations</c>.</item>
/// <item>The public actor document is served as <c>application/activity+json</c> and carries a JSON-LD
/// <c>@context</c> + <c>endpoints</c> (C-06 / the actor-document conformance).</item>
/// </list>
/// <remarks>
/// These are endpoint-shape assertions (GET the endpoint, assert the wire contract). They complement the
/// functional integration tests (which assert behavior) and the unit-level signature tests (in
/// <c>Iris.Core.Tests</c>) — together they pin the spec-required surface so a regression fails the build.
/// </remarks>
public sealed class ConformanceSuiteTests : IDisposable
{
    private const string Host = "conformance.example";
    private const string Handle = "alice";
    private const string CommunityName = "iris";

    private static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");

    private readonly TestServer _server;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly HttpClient _client;

    public ConformanceSuiteTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(_persistence, Host, Handle);
        TestSeeder.SeedCommunity(_persistence, Host, CommunityName);
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = _persistence,
            RegisterLocalKey = false,
        });
        _client = _server.CreateClient();
    }

    public void Dispose() => _server.Dispose();

    // --- WebFinger (RFC 8615 / 8410) -----------------------------------------------

    [Fact]
    public async Task WebFinger_IsServedAsJrdJson()
    {
        var response = await _client.GetAsync(WebFingerQuery(Handle));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // RFC 8615 §4.1: the WebFinger response is a JRD document — the Content-Type MUST be
        // application/jrd+json (not the generic application/json).
        var contentType = response.Content.Headers.ContentType;
        Assert.NotNull(contentType);
        Assert.Equal("application/jrd+json", contentType!.MediaType);
    }

    [Fact]
    public async Task WebFinger_ResponseHasSubjectAndSelfLink()
    {
        var response = await _client.GetAsync(WebFingerQuery(Handle));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        // The subject echoes the looked-up acct: URI.
        Assert.Equal($"acct:{Handle}@{Host}", doc.RootElement.GetProperty("subject").GetString());

        // The self link points at the actor IRI and is typed with the ActivityStreams media type
        // (the ActivityPub WebFinger convention: type = application/activity+json).
        var link = doc.RootElement.GetProperty("links")[0];
        Assert.Equal("self", link.GetProperty("rel").GetString());
        Assert.Equal(ActorIri.Value, link.GetProperty("href").GetString());
        Assert.Equal("application/activity+json", link.GetProperty("type").GetString());
    }

    [Fact]
    public async Task WebFinger_UnknownHandle_Returns404()
    {
        var response = await _client.GetAsync(WebFingerQuery("nobody"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- NodeInfo 2.0 --------------------------------------------------------------

    [Fact]
    public async Task NodeInfo_DocumentCarriesRequiredFields()
    {
        var response = await _client.GetAsync("/ap/v1/nodeinfo/2.0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // NodeInfo 2.0: version, software (name+version), protocols, usage, openRegistrations.
        Assert.Equal("2.0", root.GetProperty("version").GetString());
        var software = root.GetProperty("software");
        Assert.Equal("iris", software.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(software.GetProperty("version").GetString()));

        // protocols includes activitypub.
        var protocols = root.GetProperty("protocols").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("activitypub", protocols);

        // usage.users.total is present (an integer) and openRegistrations is a boolean.
        Assert.Equal(JsonValueKind.Number, root.GetProperty("usage").GetProperty("users")
            .GetProperty("total").ValueKind);
        Assert.True(root.GetProperty("openRegistrations").ValueKind is JsonValueKind.True or JsonValueKind.False,
            "openRegistrations must be a boolean");
    }

    // --- Actor document ------------------------------------------------------------

    [Fact]
    public async Task ActorDocument_IsServedAsActivityJson_WithContextAndEndpoints()
    {
        var response = await _client.GetAsync("/ap/v1/u/alice");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The public actor document is an ActivityStreams JSON-LD document.
        var contentType = response.Content.Headers.ContentType;
        Assert.NotNull(contentType);
        Assert.Equal("application/activity+json", contentType!.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // C-06: a JSON-LD @context is present (the default ActivityStreams context).
        Assert.True(root.TryGetProperty("@context", out _), "the actor document must carry a @context");

        // The document advertises its endpoints (inbox/outbox are reachable; endpoints is present when
        // the instance configures a shared inbox, and the per-actor collections are always present).
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("inbox").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("outbox").GetString()));
    }

    // --- sharedInbox (F-01) served on endpoints ------------------------------------

    [Fact]
    public async Task ActorDocument_AdvertisesEndpointsSharedInbox_WhenConfigured()
    {
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(persistence, Host, Handle);
        var sharedInbox = new Iri($"https://{Host}/ap/v1/shared-inbox");
        using var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = persistence,
            RegisterLocalKey = false,
            SharedInboxIri = sharedInbox,
        });
        using var client = server.CreateClient();

        var response = await client.GetAsync("/ap/v1/u/alice");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // F-01 serve side: when configured, the public document advertises endpoints.sharedInbox.
        Assert.True(root.TryGetProperty("endpoints", out var endpoints),
            "a configured shared inbox must be advertised on endpoints");
        Assert.Equal(sharedInbox.Value, endpoints.GetProperty("sharedInbox").GetString());
    }

    // --- Helpers --------------------------------------------------------------------

    private static string WebFingerQuery(string handle)
        => $"/ap/v1/.well-known/webfinger?resource=acct:{handle}@{Host}";
}
