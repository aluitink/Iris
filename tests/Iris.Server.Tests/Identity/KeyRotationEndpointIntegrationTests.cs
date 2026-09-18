using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Iris.Server.Observability;
using Iris.Server.Security;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Identity;

/// <summary>
/// Phase 84.3 — <strong>Operator key-rotation endpoint</strong>: the follow-up to 84.2's
/// <see cref="KeyRotationService"/>. The service was only reachable as a library dependency; these
/// integration tests cover the two admin-gated HTTP endpoints that expose it to an operator:
/// <c>POST /ap/v1/keys/rotate</c> (rotates the instance actor's signing key) and <c>POST
/// /ap/v1/keys/retire</c> (retires a specific key IRI). Admin-gating: the caller's authenticated actor
/// (Basic auth via <c>IActorCredentialValidator</c>) must be the instance actor
/// (<c>ActivityPubServerOptions.InstanceActorId</c>); otherwise 401 (unauthenticated) or 403 (not the
/// instance actor). In degraded (read-only) mode (83.4) the write is refused with 503.
/// </summary>
public sealed class KeyRotationEndpointIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string InstanceHandle = "iris";
    private const string Password = "iris-password";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _instanceActorIri;
    private readonly Iri _originalKeyId;

    public KeyRotationEndpointIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        // Seed the instance actor + its signing key at #key-1 (the factory's Handle IS the instance
        // actor: InstanceActorId = https://{Host}/ap/v1/u/{Handle}).
        var seeded = TestSeeder.SeedPersonWithKey(_persistence, Host, InstanceHandle);
        _instanceActorIri = seeded.ActorIri;
        _originalKeyId = seeded.KeyId;

        // Basic-auth validator keyed on the INSTANCE actor IRI (the key endpoints always call it with that IRI).
        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(iri == _instanceActorIri && username == InstanceHandle && password == Password));

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = InstanceHandle,
            Persistence = _persistence,
            CredentialValidator = credentialValidator,
            // RegisterLocalKey defaults to true => IKeyProvider.RegisterKey(instanceActor, #key-1).
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    private HttpRequestMessage RotateRequest(string? user, string? pass)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}/ap/v1/keys/rotate");
        if (user is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
        }

        return request;
    }

    private HttpRequestMessage RetireRequest(string? keyIri, string? user = InstanceHandle, string? pass = Password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}/ap/v1/keys/retire");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { keyIri }),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    // ------------------------------------------------- rotate: success

    [Fact]
    public async Task Rotate_AuthenticatedAsInstanceActor_Returns200AndNewKeyIri()
    {
        using var response = await _http.SendAsync(RotateRequest(InstanceHandle, Password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(_instanceActorIri.Value, doc.RootElement.GetProperty("actor").GetString());
        Assert.Equal($"{_instanceActorIri}#key-2", doc.RootElement.GetProperty("newKeyIri").GetString());

        // The new key is in the (shared) key store; the old key remains (the overlap window).
        Assert.True(_persistence.Keys.TryGetKey(new Iri($"{_instanceActorIri}#key-2"), out var newKey) && newKey is not null,
            "the new key must be in the store after rotation");
        Assert.True(_persistence.Keys.TryGetKey(_originalKeyId, out _),
            "the old key must remain in the store during the overlap window");

        // The actor document advertises the new key + a replaces pointer to the old.
        var stored = await _persistence.Actors.TryGetActorAsync(_instanceActorIri, out var actor);
        Assert.True(stored && actor is not null);
        Assert.Equal($"{_instanceActorIri}#key-2", actor!.GetPublicKeyIri()?.Value);
        var publicKeyExt = actor.ExtensionData![ActivityPubExtensionNames.PublicKey];
        Assert.Equal(_originalKeyId.Value, publicKeyExt.GetProperty("replaces").GetString());
    }

    // ------------------------------------------------- rotate: auth failures

    [Fact]
    public async Task Rotate_Unauthenticated_Returns401()
    {
        using var response = await _http.SendAsync(RotateRequest(null, null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rotate_WrongCredentials_Returns403()
    {
        // A credential presented but rejected (not the instance actor's) → 403 (authenticated, not authorized).
        using var response = await _http.SendAsync(RotateRequest(InstanceHandle, "wrong-password"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    // ------------------------------------------------- rotate: degraded mode

    [Fact]
    public async Task Rotate_DegradedMode_Returns503()
    {
        _server.Services.GetRequiredService<IDegradedModeGate>().MarkDegraded();
        using var response = await _http.SendAsync(RotateRequest(InstanceHandle, Password));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    // ------------------------------------------------- retire: success + unknown + missing

    [Fact]
    public async Task Retire_KnownKey_Returns204AndRemovesIt()
    {
        // Retire the original key (present in the store from seeding). A signature made with it before
        // retirement still verifies against the already-advertised public key (captured up front, since
        // retire disposes the key).
        var message = "a message signed before retirement";
        var signature = _persistence.Keys.TryGetKey(_originalKeyId, out var originalKey) && originalKey is not null
            ? originalKey.Sign(Encoding.UTF8.GetBytes(message))
            : throw new InvalidOperationException("seeded key must be in the store");
        var advertisedPem = originalKey!.ExportPublicKeyPem();

        using (var response = await _http.SendAsync(RetireRequest(_originalKeyId.Value)))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.False(_persistence.Keys.TryGetKey(_originalKeyId, out _),
            "the retired key must be removed from the store");

        // The already-advertised public key still verifies the pre-retirement signature.
        var advertised = KeyPair.FromPem(advertisedPem, KeyAlgorithm.Rsa, _originalKeyId);
        Assert.True(advertised.Verify(Encoding.UTF8.GetBytes(message), signature),
            "a pre-retirement signature must still verify against the already-advertised public key");
    }

    [Fact]
    public async Task Retire_UnknownKey_Returns404()
    {
        using var response = await _http.SendAsync(RetireRequest($"{_instanceActorIri}#key-999"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Retire_MissingKeyIri_Returns400()
    {
        // An empty body (no keyIri, no ?keyIri=) → 400.
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}/ap/v1/keys/retire");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{InstanceHandle}:{Password}")));
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    // ------------------------------------------------- retire: auth failure

    [Fact]
    public async Task Retire_Unauthenticated_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}/ap/v1/keys/retire");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { keyIri = _originalKeyId.Value }),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
