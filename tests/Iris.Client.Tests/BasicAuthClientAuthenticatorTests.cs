using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for <see cref="Iris.Client.Auth.BasicAuthClientAuthenticator"/> (implements
/// <see cref="IClientAuthenticator"/>): the Basic-auth → actor-doc-with-privateKey → loaded
/// <see cref="KeyPair"/> flow.
/// </summary>
public class BasicAuthClientAuthenticatorTests
{
    private const string ActorIri = "https://a.domain.local/u/alice";
    private const string User = "alice";
    private const string Password = "s3cret";

    private static readonly Iri Actor = new(ActorIri);
    private static readonly Iri KeyId = new($"{ActorIri}#key-1");

    private static (BasicAuthClientAuthenticator auth, FakeHttpHandler fake) Create(
        HttpResponseMessage? response = null)
    {
        var fake = new FakeHttpHandler(response ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        var auth = new BasicAuthClientAuthenticator(new HttpClient(fake), Actor, User, Password);
        return (auth, fake);
    }

    private static HttpResponseMessage ActorResponse(string privateKeyPem, string? publicKeyId = null, string keyAlgorithm = "rsa")
    {
        var doc = new Dictionary<string, object>
        {
            ["id"] = ActorIri,
            ["type"] = "Person",
            ["preferredUsername"] = "alice",
            ["privateKey"] = privateKeyPem,
            ["keyAlgorithm"] = keyAlgorithm,
        };

        if (publicKeyId is not null)
        {
            doc["publicKey"] = new Dictionary<string, string> { ["id"] = publicKeyId };
        }

        var json = JsonSerializer.Serialize(doc);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        };
    }

    [Fact]
    public async Task Authenticate_ValidCredentials_ReturnsActorAndKey()
    {
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        var pem = key.ExportPrivateKeyPem();

        var (auth, fake) = Create(ActorResponse(pem, KeyId.Value));

        var result = await auth.AuthenticateAsync(Actor);

        Assert.NotNull(result);
        Assert.NotNull(result!.Actor);
        Assert.Equal(ActorIri, result.Actor.Id);
        Assert.Equal(KeyId, result.Key.KeyId);
        Assert.Equal(KeyAlgorithm.Rsa, result.Key.Algorithm);
        // The loaded key must be usable: sign with it and verify against the original key material.
        var message = Encoding.UTF8.GetBytes("hello iris");
        var signature = result.Key.Sign(message);
        Assert.True(key.Verify(message, signature));
    }

    [Fact]
    public async Task Authenticate_SendsBasicAuthorizationHeader()
    {
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        var (auth, fake) = Create(ActorResponse(key.ExportPrivateKeyPem()));

        await auth.AuthenticateAsync(Actor);

        var header = fake.LastRequest!.Headers.Authorization;
        Assert.NotNull(header);
        Assert.Equal("Basic", header!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")), header.Parameter);
    }

    [Fact]
    public async Task Authenticate_UsesPublicKeyIdAsKeyId_WhenPresent()
    {
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        var (auth, _) = Create(ActorResponse(key.ExportPrivateKeyPem(), KeyId.Value));

        var result = await auth.AuthenticateAsync(Actor);

        Assert.NotNull(result);
        Assert.Equal(KeyId, result!.Key.KeyId);
    }

    [Fact]
    public async Task Authenticate_FallsBackToActorIriAsKeyId_WhenNoPublicKeyId()
    {
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        var (auth, _) = Create(ActorResponse(key.ExportPrivateKeyPem()));

        var result = await auth.AuthenticateAsync(Actor);

        Assert.NotNull(result);
        Assert.Equal(Actor, result!.Key.KeyId);
    }

    [Fact]
    public async Task Authenticate_NonSuccess_ReturnsNull()
    {
        var (auth, _) = Create(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        Assert.Null(await auth.AuthenticateAsync(Actor));
    }

    [Fact]
    public async Task Authenticate_NoPrivateKeyField_ReturnsNull()
    {
        // A valid actor doc but without the owner-only privateKey property.
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = ActorIri,
            ["type"] = "Person",
        });
        var (auth, _) = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        });

        Assert.Null(await auth.AuthenticateAsync(Actor));
    }

    [Fact]
    public async Task Authenticate_NonActorDocument_ReturnsNull()
    {
        const string noteJson = """{"id":"https://a.domain.local/n/1","type":"Note","content":"hi"}""";
        var (auth, _) = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(noteJson, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        });

        Assert.Null(await auth.AuthenticateAsync(Actor));
    }

    [Fact]
    public async Task Authenticate_InvalidPem_ReturnsNull()
    {
        const string bogusPem = "-----BEGIN PRIVATE KEY-----\nnot-a-real-key\n-----END PRIVATE KEY-----\n";
        var (auth, _) = Create(ActorResponse(bogusPem));

        Assert.Null(await auth.AuthenticateAsync(Actor));
    }

    [Fact]
    public async Task Authenticate_EcP256Key_LoadsEcAlgorithm()
    {
        using var key = KeyPairGenerator.GenerateEcP256(KeyId);
        var (auth, _) = Create(ActorResponse(key.ExportPrivateKeyPem(), keyAlgorithm: "ecdsa-p256"));

        var result = await auth.AuthenticateAsync(Actor);

        Assert.NotNull(result);
        Assert.Equal(KeyAlgorithm.EcP256, result!.Key.Algorithm);
    }

    [Fact]
    public async Task Authenticate_MissingKeyAlgorithm_DefaultsToRsa()
    {
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        // No keyAlgorithm field — the authenticator must default to RSA.
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = ActorIri,
            ["type"] = "Person",
            ["privateKey"] = key.ExportPrivateKeyPem(),
        });
        var (auth, _) = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        });

        var result = await auth.AuthenticateAsync(Actor);

        Assert.NotNull(result);
        Assert.Equal(KeyAlgorithm.Rsa, result!.Key.Algorithm);
    }
}
