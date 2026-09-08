using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.WebCrypto;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Iris.Web.Accounts;

/// <summary>
/// The name of the custom claim that carries the signed-in user's linked actor IRI.
/// </summary>
public static class ActorClaims
{
    /// <summary>
    /// The claim key for the linked actor's IRI (the account's federated identity).
    /// </summary>
    public const string ActorIri = "actor_iri";
}

/// <summary>
/// The scoped, per-circuit accessor that binds a signed-in user's browser session to their local
/// ActivityPub actor. Reads the user's claims (the <see cref="ActorClaims.ActorIri"/> custom claim and
/// the user id) from the <see cref="AuthenticationStateProvider"/> and exposes an
/// <see cref="IActivityPubClient"/> bound to that actor's identity.
/// </summary>
/// <remarks>
/// This is the one sanctioned path from the UI down into ActivityPub state: every post / follow /
/// like / moderation action / media upload goes through <see cref="Client"/> (an
/// <see cref="IActivityPubClient"/> making signed requests against <c>Iris.Server</c>'s
/// <c>/ap/v1/...</c> routes), exactly like any other authenticated action. Components gate on
/// <c>AuthorizeView</c> and never reach around the client into the store interfaces directly.
///
/// The signing key is fetched from the actor document endpoint (cookie-auth, same-origin) which
/// includes the owner-only <c>privateKey</c> PEM extension. In a Blazor WebAssembly host the PEM is
/// loaded via <see cref="WebCryptoSigningKeyFactory"/> (browser WebCrypto); in a server host it is
/// loaded via the BCL (<c>KeyPem.Load</c>). When the user is signed out, <see cref="IsSignedIn"/>
/// is false and <see cref="Client"/> is null — components render the signed-out experience.
/// </remarks>
public interface IActorSessionAccessor
{
    /// <summary>
    /// Whether a user is currently signed in.
    /// </summary>
    bool IsSignedIn { get; }

    /// <summary>
    /// The signed-in user's id (the <c>sub</c> claim), or null when signed out.
    /// </summary>
    Guid? UserId { get; }

    /// <summary>
    /// The signed-in user's linked actor IRI, or null when signed out.
    /// </summary>
    Iri? ActorId { get; }

    /// <summary>
    /// The signed-in user's role (<see cref="Iris.Server.Data.Accounts.UserRole"/>), or null when signed out.
    /// </summary>
    string? Role { get; }

    /// <summary>
    /// An <see cref="IActivityPubClient"/> bound to the signed-in user's actor (lazily created, cached
    /// for the circuit's lifetime). Null when signed out.
    /// </summary>
    IActivityPubClient? Client { get; }

    /// <summary>
    /// An <see cref="ILocalModerationClient"/> bound to the signed-in user's actor (lazily created,
    /// cached for the circuit's lifetime). Null when signed out.
    /// </summary>
    ILocalModerationClient? LocalModeration { get; }

    /// <summary>
    /// An <see cref="IMediaClient"/> bound to the signed-in user's actor (lazily created, cached for the
    /// circuit's lifetime). Null when signed out. Used to upload a note's image attachment (Phase 20.4
    /// (a) / F-27) — a local, Basic-authenticated multipart POST (not a signed inbox delivery).
    /// </summary>
    IMediaClient? MediaClient { get; }
}

/// <summary>
/// The default <see cref="IActorSessionAccessor"/>. Reads the current <see cref="AuthenticationState"/>
/// (the cookie-auth claims minted by registration/login) and, when signed in, fetches the actor
/// document (with the owner-only <c>privateKey</c> extension) via a same-origin cookie-auth HTTP
/// request, loads the signing key (WebCrypto in WASM, BCL on server), and builds a cached
/// <see cref="IActivityPubClient"/> bound to the user's actor.
/// </summary>
public sealed class ActorSessionAccessor : IActorSessionAccessor
{
    private readonly AuthenticationStateProvider _authentication;
    private readonly HttpClient _http;
    private readonly IKeyStore _keyStore;
    private readonly IKeyProvider _keyProvider;
    private readonly IActivityPubClientFactory _clientFactory;
    private readonly IJSRuntime? _js;
    private AuthenticationState? _state;
    private IActivityPubClient? _client;
    private ILocalModerationClient? _localModeration;
    private IMediaClient? _mediaClient;
    private Task<bool>? _keyLoaded;

    /// <summary>
    /// Initializes the accessor.
    /// </summary>
    /// <param name="authentication">The Blazor <see cref="AuthenticationStateProvider"/> (one per circuit).</param>
    /// <param name="http">A same-origin <see cref="HttpClient"/> (cookie-auth via the browser).</param>
    /// <param name="keyStore">The in-memory key store (holds the session's signing key).</param>
    /// <param name="keyProvider">The key provider (maps actor IRI to key IRI).</param>
    /// <param name="clientFactory">The ActivityPub client factory (builds the signed HTTP client).</param>
    /// <param name="js">The JS runtime (WASM only; null on server — uses BCL key loading).</param>
    public ActorSessionAccessor(
        AuthenticationStateProvider authentication,
        HttpClient http,
        IKeyStore keyStore,
        IKeyProvider keyProvider,
        IActivityPubClientFactory clientFactory,
        IJSRuntime? js = null)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _js = js;
    }

    private static bool IsAuthenticated(AuthenticationState? state)
        => state is not null && state.User.Identity is { IsAuthenticated: true };

    /// <inheritdoc/>
    public bool IsSignedIn
    {
        get
        {
            _state ??= _authentication.GetAuthenticationStateAsync().GetAwaiter().GetResult();
            return IsAuthenticated(_state);
        }
    }

    /// <inheritdoc/>
    public Guid? UserId
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            var sub = _state!.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(sub, out var id) ? id : null;
        }
    }

    /// <inheritdoc/>
    public Iri? ActorId
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            var value = _state!.User.FindFirstValue(ActorClaims.ActorIri);
            return value is not null && Iri.TryParse(value, out var iri) ? (Iri?)iri : null;
        }
    }

    /// <inheritdoc/>
    public string? Role
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            return _state!.User.FindFirstValue(ClaimTypes.Role);
        }
    }

    /// <inheritdoc/>
    public IActivityPubClient? Client
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            if (_client is not null)
            {
                return _client;
            }

            var actorId = ActorId;
            if (actorId is null)
            {
                return null;
            }

            EnsureKeyLoaded();
            _client = _clientFactory.Create(new ActivityPubClientOptions { ActorId = actorId }, new HttpClientHandler());
            return _client;
        }
    }

    /// <inheritdoc/>
    public ILocalModerationClient? LocalModeration
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            if (_localModeration is not null)
            {
                return _localModeration;
            }

            var actorId = ActorId;
            if (actorId is null)
            {
                return null;
            }

            EnsureKeyLoaded();
            _localModeration = _clientFactory.CreateLocalModerationClient(new ActivityPubClientOptions { ActorId = actorId }, new HttpClientHandler());
            return _localModeration;
        }
    }

    /// <inheritdoc/>
    public IMediaClient? MediaClient
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            if (_mediaClient is not null)
            {
                return _mediaClient;
            }

            var actorId = ActorId;
            if (actorId is null)
            {
                return null;
            }

            EnsureKeyLoaded();
            _mediaClient = _clientFactory.CreateMediaClient(new ActivityPubClientOptions { ActorId = actorId }, new HttpClientHandler());
            return _mediaClient;
        }
    }

    /// <summary>
    /// Ensures the actor's signing key is loaded into the key store (idempotent). Fetches the
    /// actor document (cookie-auth, same-origin) which includes the owner-only <c>privateKey</c>
    /// PEM extension, loads it (WebCrypto in WASM, BCL on server), and registers it with the
    /// key provider so the signing handler can resolve it.
    /// </summary>
    private void EnsureKeyLoaded()
    {
        if (_keyLoaded is not null)
        {
            _keyLoaded.GetAwaiter().GetResult();
            return;
        }

        _keyLoaded = LoadKeyAsync();
        _keyLoaded.GetAwaiter().GetResult();
    }

    private async Task<bool> LoadKeyAsync()
    {
        var actorId = ActorId;
        if (actorId is not { } me)
        {
            return false;
        }

        try
        {
            using var response = await _http.GetAsync(me.Value);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            var objectOrLink = ActivityJson.Deserialize<IObjectOrLink>(json);
            if (objectOrLink is not Actor actor)
            {
                return false;
            }

            var pem = ExtractPrivateKey(actor);
            if (string.IsNullOrWhiteSpace(pem))
            {
                return false;
            }

            var keyId = ExtractKeyId(actor, me);
            var algorithm = ExtractKeyAlgorithm(actor);

            ISigningKey key;
            if (_js is not null)
            {
                var factory = new WebCryptoSigningKeyFactory(_js);
                key = await factory.CreateAsync(pem, algorithm, keyId);
            }
            else
            {
                key = KeyPem.Load(pem, algorithm, keyId);
            }

            _keyStore.PutKey(key);
            _keyProvider.RegisterKey(me, key.KeyId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractPrivateKey(Actor actor)
        => actor.ExtensionData is { } ext
           && ext.TryGetValue(Iris.Core.ActivityPubExtensionNames.PrivateKey, out var value)
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

    private static KeyAlgorithm ExtractKeyAlgorithm(Actor actor)
    {
        if (actor.ExtensionData is { } ext
            && ext.TryGetValue(ActivityPubExtensionNames.KeyAlgorithm, out var value)
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
