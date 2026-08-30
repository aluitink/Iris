using System.Security.Cryptography;
using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Auth;

/// <summary>
/// An <see cref="IClientAuthenticator"/> that authenticates with a Bearer token (OAuth2). It fetches
/// the actor document with an <c>Authorization: Bearer</c> header, reads the owner-only
/// <c>privateKey</c> (PKCS#8 PEM) extension field, and loads it into a <see cref="ISigningKey"/>.
/// </summary>
/// <remarks>
/// Phase 15.2b: the client-side half of the OAuth2 authorization code + PKCE flow. The Bearer token
/// is obtained by the host app (via the browser redirect → callback → code exchange against
/// <c>/ap/v1/oauth2/token</c>); this authenticator takes the token and fetches the actor document.
/// The key's <see cref="ISigningKey.KeyId"/> is the actor IRI. The returned key is owned by the
/// caller. This is the drop-in replacement for <see cref="BasicAuthClientAuthenticator"/>: the host
/// app swaps the <see cref="IClientAuthenticator"/> registration to change the auth scheme.
/// </remarks>
public sealed class OAuth2ClientAuthenticator : IClientAuthenticator
{
    private const string PrivateKeyProperty = "privateKey";
    private const string PublicKeyProperty = "publicKey";
    private const string KeyAlgorithmProperty = "keyAlgorithm";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, ValueTask<string?>> _tokenProvider;

    /// <summary>
    /// Initializes a new <see cref="OAuth2ClientAuthenticator"/>.
    /// </summary>
    /// <param name="http">The HTTP client used to fetch the actor document. Not disposed by the authenticator.</param>
    /// <param name="tokenProvider">
    /// A delegate that returns the current Bearer token (or null if no token is available). The host
    /// app wires this to its token store (e.g. the token obtained via the OAuth2 code exchange). The
    /// delegate is async-aware because the token may need to be refreshed.
    /// </param>
    public OAuth2ClientAuthenticator(
        HttpClient http,
        Func<CancellationToken, ValueTask<string?>> tokenProvider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
    }

    /// <inheritdoc/>
    public async Task<AuthenticatedActor?> AuthenticateAsync(Iri actorId, CancellationToken ct = default)
    {
        var token = await _tokenProvider(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, actorId.Value);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var objectOrLink = ActivityJson.Deserialize<IObjectOrLink>(json);
        if (objectOrLink is not Actor actor)
        {
            return null;
        }

        var pem = ExtractPrivateKey(actor);
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        var keyId = ExtractKeyId(actor, actorId);
        var algorithm = ExtractKeyAlgorithm(actor);
        ISigningKey key;
        try
        {
            key = KeyPem.Load(pem, algorithm, keyId);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }

        return new AuthenticatedActor(actor, key);
    }

    private static string? ExtractPrivateKey(Actor actor)
        => actor.ExtensionData is { } ext
           && ext.TryGetValue(PrivateKeyProperty, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Iri ExtractKeyId(Actor actor, Iri fallback)
    {
        if (actor.ExtensionData is { } ext
            && ext.TryGetValue(PublicKeyProperty, out var pk)
            && pk.ValueKind == JsonValueKind.Object
            && pk.TryGetProperty("id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String)
        {
            var id = idElement.GetString();
            if (id is not null && Iri.TryParse(id, out var iri))
            {
                return iri;
            }
        }

        return fallback;
    }

    private static KeyAlgorithm ExtractKeyAlgorithm(Actor actor)
    {
        if (actor.ExtensionData is { } ext
            && ext.TryGetValue(KeyAlgorithmProperty, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.Equals(text, "ecdsa-p256", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "ec", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "ecp256", StringComparison.OrdinalIgnoreCase))
            {
                return KeyAlgorithm.EcP256;
            }

            if (string.Equals(text, "ed25519", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "eddsa", StringComparison.OrdinalIgnoreCase))
            {
                return KeyAlgorithm.Ed25519;
            }
        }

        return KeyAlgorithm.Rsa;
    }
}
