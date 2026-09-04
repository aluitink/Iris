using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Auth;

/// <summary>
/// An <see cref="IClientAuthenticator"/> that authenticates with HTTP Basic auth. It fetches the
/// actor document with a <c>Authorization: Basic</c> header, reads the owner-only
/// <c>privateKey</c> (PKCS#8 PEM) extension field, and loads it into a <see cref="KeyPair"/>.
/// </summary>
/// <remarks>
/// The <see cref="KeyPair"/> is inferred from the PEM header and loaded with the matching
/// <see cref="KeyAlgorithm"/>. The key's <see cref="KeyPair.KeyId"/> is the actor IRI. The returned
/// <see cref="KeyPair"/> is owned by the caller. This is the concrete "Basic-auth → private-key (PEM)"
/// flow described in the roadmap; a future OAuth authenticator would be a separate implementation
/// of <see cref="IClientAuthenticator"/>.
/// </remarks>
public sealed class BasicAuthClientAuthenticator : IClientAuthenticator
{
    private static readonly string PrivateKeyProperty = ActivityPubExtensionNames.PrivateKey;
    private static readonly string KeyAlgorithmProperty = ActivityPubExtensionNames.KeyAlgorithm;

    private readonly HttpClient _http;
    private readonly Iri _actorId;
    private readonly string _credentials;
    private readonly Func<string, KeyAlgorithm, Iri, CancellationToken, Task<ISigningKey>>? _keyFactory;

    /// <summary>
    /// Initializes a new <see cref="BasicAuthClientAuthenticator"/>.
    /// </summary>
    /// <param name="http">The HTTP client used to fetch the actor document. Not disposed by the authenticator.</param>
    /// <param name="actorId">The IRI of the actor (the authenticated owner).</param>
    /// <param name="user">The Basic-auth username.</param>
    /// <param name="password">The Basic-auth password.</param>
    public BasicAuthClientAuthenticator(HttpClient http, Iri actorId, string user, string password)
        : this(http, actorId, user, password, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="BasicAuthClientAuthenticator"/> with a custom private-key loader.
    /// </summary>
    /// <param name="http">The HTTP client used to fetch the actor document. Not disposed by the authenticator.</param>
    /// <param name="actorId">The IRI of the actor (the authenticated owner).</param>
    /// <param name="user">The Basic-auth username.</param>
    /// <param name="password">The Basic-auth password.</param>
    /// <param name="keyFactory">
    /// An optional asynchronous private-key loader (PEM + algorithm + key id → loaded
    /// <see cref="ISigningKey"/>). When supplied it replaces the default
    /// <see cref="Iris.Core.Identity.KeyPem.Load"/>; a Blazor WebAssembly host supplies a WebCrypto
    /// loader because the .NET-on-WASM BCL cannot load an RSA private key. When
    /// <see langword="null"/> the default BCL/BouncyCastle loader is used.
    /// </param>
    public BasicAuthClientAuthenticator(
        HttpClient http,
        Iri actorId,
        string user,
        string password,
        Func<string, KeyAlgorithm, Iri, CancellationToken, Task<ISigningKey>>? keyFactory)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _actorId = actorId;
        _credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
        _keyFactory = keyFactory;
    }

    /// <inheritdoc/>
    public async Task<AuthenticatedActor?> AuthenticateAsync(Iri actorId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, actorId.Value);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _credentials);

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
            // A custom loader (e.g. WebCrypto in a Blazor WebAssembly host, where the BCL cannot load
            // an RSA private key) takes precedence; otherwise the default BCL/BouncyCastle loader.
            key = _keyFactory is not null
                ? await _keyFactory(pem, algorithm, keyId, ct).ConfigureAwait(false)
                : KeyPem.Load(pem, algorithm, keyId);
        }
        catch (CryptographicException)
        {
            // Malformed PEM / PEM-algorithm mismatch: an authentication failure, not an error.
            return null;
        }
        catch (ArgumentException)
        {
            // ImportFromPem throws ArgumentException for structurally invalid PEM (and the WASM BCL
            // throws ArgumentException "Arg_PlatformNotSupported" for RSA — a load failure, not a crash).
            return null;
        }
        catch (FormatException)
        {
            // Invalid base64 / DER within the PEM block.
            return null;
        }

        return new AuthenticatedActor(actor, key);
    }

    /// <summary>
    /// Gets the Basic-auth credentials (base64 of <c>user:password</c>) applied to outgoing requests.
    /// </summary>
    public string Credentials => _credentials;

    private static string? ExtractPrivateKey(Actor actor)
        => actor.ExtensionData is { } ext
           && ext.TryGetValue(PrivateKeyProperty, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Iri ExtractKeyId(Actor actor, Iri fallback)
    {
        if (actor.ExtensionData is { } ext
            && ext.TryGetValue(ActivityPubExtensionNames.PublicKey, out var pk)
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

    /// <summary>
    /// Determines the <see cref="KeyAlgorithm"/> for the loaded key. Both RSA and EC private keys are
    /// exported as identical PKCS#8 ("BEGIN PRIVATE KEY") PEM, so the algorithm cannot be inferred from
    /// the header; the actor document carries it in the <c>keyAlgorithm</c> extension field. When
    /// absent, defaults to RSA (the Iris default). See ROADMAP Resolved Decision on key algorithm
    /// round-tripping.
    /// </summary>
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
