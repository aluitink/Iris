using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Identity;

/// <summary>
/// Phase 84.4 — <strong>Key-rotation durability + per-actor rotation</strong> (integration). The
/// in-memory actor→key binding (<see cref="IKeyProvider"/>) is wiped on a host restart; before 84.4 the
/// startup restore re-registered a hard-coded <c>#key-1</c>, so a rotated key (<c>#key-2</c>) would be
/// lost (the actor would re-sign with the stale key, or fail to resolve once the old key is retired).
/// These tests prove the rotation now survives a restart via <see cref="KeyProviderRehydration"/> (the
/// restart-restore path the production <c>WebAppFactory.RestoreLocalSigningKeys</c> uses), and that the
/// new <c>?actor=</c> option rotates a specific local actor.
/// </summary>
public sealed class KeyRotationDurabilityIntegrationTests
{
    private const string Host = "a.domain.local";
    private const string InstanceHandle = "iris";
    private const string Password = "iris-password";

    private readonly InMemoryPersistenceProvider _persistence = new();
    private readonly Iri _instanceActorIri;
    private readonly Iri _originalKeyId;
    private readonly BasicAuthCredentialValidator _credentialValidator;

    public KeyRotationDurabilityIntegrationTests()
    {
        var seeded = TestSeeder.SeedPersonWithKey(_persistence, Host, InstanceHandle);
        _instanceActorIri = seeded.ActorIri;
        _originalKeyId = seeded.KeyId;
        _credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(iri == _instanceActorIri && username == InstanceHandle && password == Password));
    }

    private TestServer CreateHost(bool registerLocalKey) => ActivityPubHostFactory.Create(new ActivityPubHostOptions
    {
        Host = Host,
        Handle = InstanceHandle,
        Persistence = _persistence,
        CredentialValidator = _credentialValidator,
        RegisterLocalKey = registerLocalKey,
    });

    private static HttpRequestMessage AuthenticatedPost(string url, string? query = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url + (query is null ? string.Empty : $"?{query}"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{InstanceHandle}:{Password}")));
        return request;
    }

    // ------------------------------------------------- restart durability

    [Fact]
    public async Task Rotate_ThenRestartOverSamePersistence_RotatedKeyIsRebound()
    {
        // Host #1: the default startup registration (binds the instance actor to #key-1).
        using (var host1 = CreateHost(registerLocalKey: true))
        {
            var http1 = new HttpClient(host1.CreateHandler(), disposeHandler: false);
            using var response = await http1.SendAsync(AuthenticatedPost($"https://{Host}/ap/v1/keys/rotate"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var rotatedKeyId = new Iri(doc.RootElement.GetProperty("newKeyIri").GetString()!);
            Assert.Equal($"{_instanceActorIri}#key-2", rotatedKeyId.Value);
            http1.Dispose();
        }

        // The persisted actor document now advertises the rotated key.
        var storedAfterRotate = await _persistence.Actors.TryGetActorAsync(_instanceActorIri, out var actorAfterRotate);
        Assert.True(storedAfterRotate && actorAfterRotate is not null);
        Assert.Equal($"{_instanceActorIri}#key-2", actorAfterRotate!.GetPublicKeyIri()?.Value);

        // Host #2: a "restart" over the SAME persistence, with the (pre-84.4) hard-coded #key-1 startup
        // registration DISABLED. Without the rehydration, the actor would have no binding at all.
        using (var host2 = CreateHost(registerLocalKey: false))
        {
            var keyProvider = host2.Services.GetRequiredService<IKeyProvider>();
            Assert.False(keyProvider.TryGetIdentity(_instanceActorIri, out _),
                "before the rehydration the restarted host has no actor→key binding");

            // The production restart restore (WebAppFactory.RestoreLocalSigningKeys) calls this.
            var rehydrated = await KeyProviderRehydration.RehydrateFromActorsAsync(
                keyProvider,
                _persistence.Actors,
                _persistence.Keys);
            Assert.True(rehydrated >= 1, "the instance actor's binding must be rehydrated");

            // The actor now resolves to the ROTATED key (#key-2), not the stale #key-1.
            Assert.True(keyProvider.TryGetIdentity(_instanceActorIri, out var identity) && identity is not null);
            Assert.Equal($"{_instanceActorIri}#key-2", identity!.KeyId.Value);
            Assert.NotEqual(_originalKeyId, identity.KeyId);
        }
    }

    [Fact]
    public async Task Rotate_RetireOldKey_ThenRestart_RotatedKeyStillResolves()
    {
        // The strongest durability guarantee: even after the old key is retired (removed from the store),
        // a restart re-binds the actor to the rotated key (the retired #key-1 is gone, but #key-2 is not).
        using var host1 = CreateHost(registerLocalKey: true);
        var http1 = new HttpClient(host1.CreateHandler(), disposeHandler: false);

        // Rotate (#key-1 -> #key-2) and then retire the old key.
        using (var rotateResponse = await http1.SendAsync(AuthenticatedPost($"https://{Host}/ap/v1/keys/rotate")))
        {
            Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);
        }
        var retireRequest = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}/ap/v1/keys/retire");
        retireRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{InstanceHandle}:{Password}")));
        retireRequest.Content = new StringContent(
            JsonSerializer.Serialize(new { keyIri = _originalKeyId.Value }), Encoding.UTF8, "application/json");
        using (var retireResponse = await http1.SendAsync(retireRequest))
        {
            Assert.Equal(HttpStatusCode.NoContent, retireResponse.StatusCode);
        }
        Assert.False(_persistence.Keys.TryGetKey(_originalKeyId, out _), "the old key must be retired");
        http1.Dispose();

        // Restart over the same persistence (no hard-coded #key-1 registration), then rehydrate.
        using (var host2 = CreateHost(registerLocalKey: false))
        {
            var keyProvider = host2.Services.GetRequiredService<IKeyProvider>();
            await KeyProviderRehydration.RehydrateFromActorsAsync(keyProvider, _persistence.Actors, _persistence.Keys);

            // The actor resolves to the rotated key; the retired key is NOT resurrected.
            Assert.True(keyProvider.TryGetIdentity(_instanceActorIri, out var identity) && identity is not null);
            Assert.Equal($"{_instanceActorIri}#key-2", identity!.KeyId.Value);
        }
    }

    // ------------------------------------------------- per-actor rotation (?actor=)

    [Fact]
    public async Task Rotate_WithActorQuery_RotatesThatSpecificLocalActor()
    {
        // A second local actor (seeded with its own key at #key-1).
        const string otherHandle = "bob";
        var other = TestSeeder.SeedPersonWithKey(_persistence, Host, otherHandle);

        using var host = CreateHost(registerLocalKey: true);
        var http = new HttpClient(host.CreateHandler(), disposeHandler: false);

        // Rotate bob (not the instance actor) via ?actor=.
        using var response = await http.SendAsync(AuthenticatedPost($"https://{Host}/ap/v1/keys/rotate", $"actor={Uri.EscapeDataString(other.ActorIri.Value)}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(other.ActorIri.Value, doc.RootElement.GetProperty("actor").GetString());
        Assert.Equal($"{other.ActorIri}#key-2", doc.RootElement.GetProperty("newKeyIri").GetString());

        // bob's document advertises the new key + replaces; the instance actor is untouched (still #key-1).
        var storedBob = await _persistence.Actors.TryGetActorAsync(other.ActorIri, out var bobActor);
        Assert.True(storedBob && bobActor is not null);
        Assert.Equal($"{other.ActorIri}#key-2", bobActor!.GetPublicKeyIri()?.Value);
        var storedInstance = await _persistence.Actors.TryGetActorAsync(_instanceActorIri, out var instanceActor);
        Assert.True(storedInstance && instanceActor is not null);
        // The instance actor must be untouched (still on #key-1) — only bob was rotated.
        Assert.Equal(_originalKeyId.Value, instanceActor!.GetPublicKeyIri()?.Value);
    }

    [Fact]
    public async Task Rotate_WithActorQuery_UnknownActor_Returns404()
    {
        using var host = CreateHost(registerLocalKey: true);
        var http = new HttpClient(host.CreateHandler(), disposeHandler: false);

        using var response = await http.SendAsync(AuthenticatedPost(
            $"https://{Host}/ap/v1/keys/rotate",
            $"actor={Uri.EscapeDataString($"https://{Host}/ap/v1/u/does-not-exist")}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Rotate_DefaultIsInstanceActor_WhenNoActorQuery()
    {
        // No ?actor= → the instance actor is rotated (the 84.3 default, unchanged).
        using var host = CreateHost(registerLocalKey: true);
        var http = new HttpClient(host.CreateHandler(), disposeHandler: false);

        using var response = await http.SendAsync(AuthenticatedPost($"https://{Host}/ap/v1/keys/rotate"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(_instanceActorIri.Value, doc.RootElement.GetProperty("actor").GetString());
        Assert.Equal($"{_instanceActorIri}#key-2", doc.RootElement.GetProperty("newKeyIri").GetString());
    }
}
