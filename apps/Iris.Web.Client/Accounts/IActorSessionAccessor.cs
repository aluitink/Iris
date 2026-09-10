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

namespace Iris.Web.Client.Accounts;

/// <summary>
/// The name of the custom claim that carries the signed-in user's linked actor IRI.
/// </summary>
public static class ActorClaims
{
    /// <summary>
    /// The claim key for the linked actor's IRI (the account's federated identity).
    /// </summary>
    public const string ActorIri = "actor_iri";

    /// <summary>
    /// The claim key for the instance's public feed IRI (the collection a logged-out visitor
    /// browses — the union of all local actors' outbox activities, 54.27).
    /// </summary>
    public const string PublicFeedIri = "public_feed_iri";
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
    /// Ensures the session's signing key is loaded (fetches the actor document's owner-only
    /// <c>privateKey</c> extension and registers it with the key provider). Idempotent. Must be
    /// called from an async context (a page's <c>OnInitializedAsync</c>) before the synchronous
    /// <see cref="Client"/> / <see cref="LocalModeration"/> / <see cref="MediaClient"/> properties
    /// are read during render — WebAssembly cannot block on a Task, so the synchronous properties
    /// return <c>null</c> until this has completed (then the page calls <c>StateHasChanged</c> to
    /// re-render with the client available). A no-op when signed out.
    /// </summary>
    Task EnsureReadyAsync();

    /// <summary>
    /// The signed-in user's id (the <c>sub</c> claim), or null when signed out.
    /// </summary>
    Guid? UserId { get; }

    /// <summary>
    /// The signed-in user's linked actor IRI, or null when signed out.
    /// </summary>
    Iri? ActorId { get; }

    /// <summary>
    /// The deployment's <c>iris:</c> namespace base IRI (the full-IRI prefix the server uses for its
    /// JSON-LD extension properties — e.g. <c>likedCount</c>, <c>isLiked</c> — whose wire key is
    /// <c>{NamespaceBase}{term}</c>). Derived from the instance's canonical base (the advertised FQDN when
    /// set, otherwise the browser origin) as <c>{base}/ns#</c>, matching the server's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>. Null when the base cannot be determined. Used to read
    /// the server-rendered extensions off a fetched document via
    /// <see cref="Iris.Client.IrisDocumentExtensions"/>.
    /// </summary>
    Iri? IrisNamespaceBase { get; }

    /// <summary>
    /// The signed-in user's role, or null when signed out.
    /// </summary>
    string? Role { get; }

    /// <summary>
    /// The instance's public feed IRI (the collection a logged-out visitor browses — the union of
    /// all local actors' outbox activities, 54.27). Available whether or not the user is signed in.
    /// Null when the public feed IRI could not be determined.
    /// </summary>
    Iri? PublicFeedIri { get; }

    /// <summary>
    /// An <see cref="IActivityPubClient"/> bound to the signed-in user's actor (lazily created, cached
    /// for the circuit's lifetime). Null when signed out, or when <see cref="EnsureReadyAsync"/> has not
    /// yet completed (the key is still loading) — call <c>EnsureReadyAsync</c> in
    /// <c>OnInitializedAsync</c> and re-render once it returns.
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

    /// <summary>
    /// Uploads a media file via cookie auth (the Blazor WASM UI's path — the client has no Basic-auth
    /// credentials). POSTs a multipart file to the actor's own instance's local media endpoint and
    /// returns the same-origin media IRI. Returns null when signed out or the upload fails.
    /// </summary>
    /// <param name="actorId">The actor's IRI (the owner of the upload).</param>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="contentType">The file's MIME type (e.g. <c>image/png</c>).</param>
    /// <param name="fileName">The file's name (e.g. <c>photo.png</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="MediaUploadResult"/> on success; null when signed out or the upload fails.</returns>
    Task<MediaUploadResult?> UploadMediaAsync(Iri actorId, byte[] bytes, string contentType, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Routes a <c>GET</c> request to a remote instance through the home instance's proxy endpoint
    /// (<c>POST /ap/v1/proxy/{target}</c>), authenticating by the site cookie. Used for cross-instance
    /// reads the browser cannot make directly (CORS-blocked): WebFinger resolution, federated search,
    /// and any other public GET on a remote ActivityPub server.
    /// </summary>
    /// <param name="target">The absolute IRI of the remote resource to fetch (e.g.
    /// <c>https://mastodon.social/.well-known/webfinger?resource=acct:admin@mastodon.social</c>).</param>
    /// <param name="accept">The <c>Accept</c> header to send (content negotiation). Null sends no
    /// <c>Accept</c> header.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The remote response body as a string, or null when signed out, the proxy rejects the
    /// request, or the remote returns a non-success status.</returns>
    Task<string?> ProxyGetAsync(string target, string? accept, CancellationToken ct = default);
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
    private readonly Uri? _advertiseBase;
    private readonly Uri? _rewriteBase;
    private readonly Uri _browserBase;
    private readonly HttpClient _sameOriginHttp;
    private AuthenticationState? _state;
    private Task<AuthenticationState>? _stateLoad;
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
    /// <param name="advertiseBase">
    /// The instance's advertised (public FQDN) ActivityPub base, or null when the browser dials the same
    /// origin it advertises (no cross-origin rewrite needed). When set, the session's ActivityPub clients
    /// route FQDN-addressed requests same-origin (via <see cref="SameOriginApHandler"/>) so the browser can
    /// sign writes and read the owner-only key without a CORS failure.
    /// </param>
    /// <param name="browserBaseAddress">
    /// The browser's origin (the instance's dial base, e.g. <c>http://localhost:8088/</c>). Used as the
    /// BaseAddress for the same-origin key-fetch client so relative (rewritten) requests resolve to the
    /// browser's own origin.
    /// </param>
    /// <param name="rewriteBase">
    /// The host the <see cref="SameOriginApHandler"/> matches-and-rewrites to same-origin. Defaults to
    /// <paramref name="advertiseBase"/> (the single-instance case). Program.cs passes the ORIGINAL
    /// (pre-multi-instance-override) FQDN when it differs from <paramref name="advertiseBase"/>, so the
    /// rewriter still rewrites FQDN-addressed requests even though the effective base is the browser
    /// origin (a cross-origin FQDN request would otherwise be CORS-blocked).
    /// </param>
    public ActorSessionAccessor(
        AuthenticationStateProvider authentication,
        HttpClient http,
        IKeyStore keyStore,
        IKeyProvider keyProvider,
        IActivityPubClientFactory clientFactory,
        IJSRuntime? js = null,
        Uri? advertiseBase = null,
        Uri? browserBaseAddress = null,
        Uri? rewriteBase = null)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _js = js;
        _advertiseBase = advertiseBase is null ? null : new Uri(TrimTrailingSlash(advertiseBase.ToString()));
        // The host the SameOriginApHandler matches-and-rewrites to same-origin. Defaults to the
        // advertise base (the common single-instance case); Program.cs passes the ORIGINAL (pre-
        // multi-instance-override) FQDN when it differs, so the rewriter still knows which FQDN host to
        // rewrite even though _advertiseBase (used for DialBase/Proxy/Namespace) is the browser origin.
        _rewriteBase = rewriteBase is null ? _advertiseBase : new Uri(TrimTrailingSlash(rewriteBase.ToString()));
        // A dedicated same-origin client for the owner-only actor-document read (the privateKey fetch).
        // It carries the site cookie (cookie auth) and rewrites FQDN IRIs to same-origin (the browser
        // cannot read a cross-origin owner-only resource — CORS). BaseAddress is the browser origin so a
        // rewritten (relative) request resolves to the browser's own origin.
        _browserBase = browserBaseAddress ?? new Uri("http://localhost/");
        _sameOriginHttp = new HttpClient(new SameOriginApHandler(new HttpClientHandler(), _rewriteBase, _browserBase), disposeHandler: true)
        {
            BaseAddress = _browserBase,
        };
    }

    private static string TrimTrailingSlash(string value)
        => value.EndsWith("/", StringComparison.Ordinal) ? value[..^1] : value;

    /// <summary>
    /// The plain browser transport handler (cookie auth) that the factory's signed pipeline wraps. This
    /// is the INNERMOST handler — the <see cref="SameOriginApHandler"/> that rewrites FQDN IRIs to
    /// same-origin sits OUTERMOST (see <see cref="BuildSameOriginRewriter"/>) so the rewrite precedes
    /// signing.
    /// </summary>
    private HttpMessageHandler BuildTransportHandler() => new HttpClientHandler();

    /// <summary>
    /// Builds the outermost <see cref="SameOriginApHandler"/> for the session's signed clients: it
    /// rewrites absolute FQDN IRIs to same-origin BEFORE the <see cref="Iris.Client.Pipeline.SigningHandler"/>
    /// signs, so the signature's <c>host</c> component matches the host the server receives on the wire
    /// (the browser sends the site's dial host because <c>Host</c> is a forbidden header it cannot
    /// override — see <see cref="SameOriginApHandler"/>).
    /// </summary>
    private SameOriginApHandler BuildSameOriginRewriter() => new(new HttpClientHandler(), _rewriteBase, _browserBase);

    /// <summary>
    /// Builds the <see cref="ActivityPubClientOptions"/> for the session's signed ActivityPub client,
    /// enabling the cross-instance-read proxy fallback (54.7). A browser cannot reach a cross-origin
    /// remote instance directly — a direct cross-origin <c>GET</c> is CORS-blocked (a network failure
    /// with no status code, so the 401/403 fallback never engages). Routing a cross-instance <c>GET</c>
    /// straight through the same-origin home proxy (which relays it to the remote and returns the
    /// response) is the only way the browser can load a remote actor/object.
    /// </summary>
    /// <remarks>
    /// The proxy and dial bases are the instance's canonical base — the advertised FQDN when set (the
    /// host the client's own actor IRIs use, so a local read is same-host and dials directly, not
    /// proxied), otherwise the browser origin (the same-origin case). <see cref="ActivityPubClientOptions.
    /// ProxyCredentials"/> is left null: the proxy request is same-origin, so the browser's transport
    /// attaches the site cookie and the proxy identifies the actor from the cookie's <c>actor_iri</c>
    /// claim (the browser has no Basic credentials — it cannot be given the user's password).
    /// </remarks>
    private ActivityPubClientOptions BuildClientOptions(Iri actorId)
    {
        // The home instance's canonical base: the advertised FQDN (the host local actor IRIs use) when
        // set, else the browser origin (the client dials the same origin it advertises). This is both
        // the proxy base (where POST /ap/v1/proxy/{target} lives) and the dial base (the host a GET
        // must differ from to be a cross-instance read).
        var baseUri = _advertiseBase ?? _browserBase;
        return new ActivityPubClientOptions
        {
            ActorId = actorId,
            ProxyBaseUrl = new Iri(baseUri.ToString()),
            ProxyCredentials = null,
            DialBaseUri = baseUri,
            RouteCrossInstanceReadsViaProxy = true,
        };
    }

    private static bool IsAuthenticated(AuthenticationState? state)
        => state is not null && state.User.Identity is { IsAuthenticated: true };

    /// <inheritdoc/>
    /// <remarks>
    /// Non-blocking: returns the cached <see cref="AuthenticationState"/> (primed by
    /// <see cref="EnsureReadyAsync"/>). When the state has not yet been loaded (the page has not run
    /// <c>OnInitializedAsync</c>), returns <c>false</c> rather than blocking on
    /// <c>GetAuthenticationStateAsync</c> — WebAssembly cannot wait on a monitor (Task.Wait throws
    /// PlatformNotSupportedException on this runtime).
    /// </remarks>
    public bool IsSignedIn
        => IsAuthenticated(_state);

    /// <inheritdoc/>
    public Guid? UserId
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            var sub = _state!.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
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

            var value = _state!.User.FindFirst(ActorClaims.ActorIri)?.Value;
            return value is not null && Iri.TryParse(value, out var iri) ? (Iri?)iri : null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Derived from the instance's <b>canonical</b> base — the advertised FQDN (the host the server
    /// advertises its <c>NamespaceIri</c> from), otherwise the browser origin (the same-origin case, where
    /// the server derives the namespace from the request's own base) — as <c>{base}/ns#</c>, the same
    /// derivation <c>WebAppFactory</c> uses for <c>ActivityPubServerOptions.NamespaceIri</c>.
    ///
    /// This must track the canonical FQDN, NOT the (possibly multi-instance-rewritten) dial base: the
    /// server writes its <c>iris:</c> extension properties (the <c>likedCount</c> / <c>sharedCount</c> /
    /// <c>isLiked</c> counters the engagement UI reads to skip the per-object collection walk) under the
    /// namespace of its own <c>BaseUri</c> (the advertised FQDN). When the browser dials
    /// that instance on a different origin (the multi-instance case), the session's <c>_advertiseBase</c>
    /// is rewritten to the browser origin for dialing, but the counter extensions are still namespaced to
    /// the canonical FQDN — so reading them off the (rewritten) dial base would find nothing and fall back
    /// to the full <c>/likes</c> / <c>/shares</c> walk per object. <c>_rewriteBase</c> holds the canonical
    /// FQDN (the pre-rewrite original), so it is the correct base for the namespace here; it equals
    /// <c>_advertiseBase</c> in the single-instance case, so the behavior is unchanged when there is no
    /// rewrite.
    /// </remarks>
    public Iri? IrisNamespaceBase
    {
        get
        {
            var baseUri = _rewriteBase ?? _browserBase;
            return Iri.TryParse($"{baseUri.Scheme}://{baseUri.Authority}/ns#", out var ns) ? ns : null;
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

            return _state!.User.FindFirst(ClaimTypes.Role)?.Value;
        }
    }

    /// <inheritdoc/>
    public Iri? PublicFeedIri
    {
        get
        {
            if (_state is null)
            {
                return null;
            }

            var value = _state.User.FindFirst(ActorClaims.PublicFeedIri)?.Value;
            return value is not null && value.Length > 0 && Iri.TryParse(value, out var iri) ? (Iri?)iri : null;
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

            // Start the key load (non-blocking). Only build the client if the load has already
            // completed successfully; otherwise return null so the page can render a loading state
            // and re-render after EnsureReadyAsync (WebAssembly cannot block on the load).
            EnsureKeyLoadStarted();
            if (!KeyLoadSucceeded)
            {
                return null;
            }

            // Non-nullable capture (the null check above guarantees a value).
            Iri signedAs = actorId.Value;
            _client = _clientFactory.Create(
                BuildClientOptions(signedAs),
                BuildTransportHandler(),
                outermost: BuildSameOriginRewriter());
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

            EnsureKeyLoadStarted();
            if (!KeyLoadSucceeded)
            {
                return null;
            }

            _localModeration = _clientFactory.CreateLocalModerationClient(
                new ActivityPubClientOptions { ActorId = actorId },
                BuildSameOriginRewriter());
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

            EnsureKeyLoadStarted();
            if (!KeyLoadSucceeded)
            {
                return null;
            }

            _mediaClient = _clientFactory.CreateMediaClient(
                new ActivityPubClientOptions { ActorId = actorId },
                BuildTransportHandler());
            return _mediaClient;
        }
    }

    /// <inheritdoc/>
    public async Task<MediaUploadResult?> UploadMediaAsync(Iri actorId, byte[] bytes, string contentType, string fileName, CancellationToken ct = default)
    {
        if (!IsSignedIn)
        {
            return null;
        }

        // Cookie-auth upload: the WASM client has no Basic-auth credentials, so the media upload goes
        // through the same-origin client (which carries the site cookie). The server's local media
        // endpoint accepts cookie auth (the actor_iri claim must match the requested actor).
        try
        {
            // Build the local media upload URI: {origin}/local/v1/u/{handle}/media.
            var handle = ExtractHandle(actorId);
            if (handle is null)
            {
                return null;
            }

            var uri = $"{_browserBase.Scheme}://{_browserBase.Authority}/local/v1/u/{handle}/{MediaConstants.UploadSegment}";

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(fileContent, "file", fileName);

            using var response = await _sameOriginHttp.PostAsync(uri, form, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var json = System.Text.Json.JsonDocument.Parse(body);
            var root = json.RootElement;

            var mediaIri = root.TryGetProperty("id", out var m) ? m.GetString() : null;
            var mediaType = root.TryGetProperty("type", out var c) ? c.GetString() : null;
            var name = root.TryGetProperty("name", out var f) ? f.GetString() : null;

            if (string.IsNullOrWhiteSpace(mediaIri))
            {
                return null;
            }

            return new MediaUploadResult(new Iri(mediaIri), mediaType ?? contentType, name ?? fileName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the handle (the final path segment) from an actor IRI (e.g. <c>https://host/ap/v1/u/alice</c>
    /// → <c>alice</c>). Null when the IRI does not match the expected shape.
    /// </summary>
    private static string? ExtractHandle(Iri actorIri)
    {
        var uri = new Uri(actorIri.Value);
        var segments = uri.AbsolutePath.TrimEnd('/').Split('/');
        // Expected shape: /ap/v1/u/{handle}
        if (segments.Length < 4)
        {
            return null;
        }

        var idx = Array.IndexOf(segments, "u");
        if (idx < 0 || idx + 1 >= segments.Length)
        {
            return null;
        }

        return segments[idx + 1];
    }

    /// <inheritdoc/>
    public async Task EnsureReadyAsync()
    {
        // Load the auth state first (async — WASM-safe; the synchronous IsSignedIn/ActorId/… properties
        // only observe _state, they never block). Idempotent.
        _stateLoad ??= LoadStateAsync();
        _state = await _stateLoad;

        if (!IsAuthenticated(_state))
        {
            return;
        }

        // Signed in: start the key load (idempotent) and await it (an async context — a page's
        // OnInitializedAsync). The synchronous Client/LocalModeration/MediaClient properties only ever
        // observe this task; they never block on it.
        _keyLoaded ??= LoadKeyAsync();
        await _keyLoaded;
    }

    private async Task<AuthenticationState> LoadStateAsync()
    {
        var state = await _authentication.GetAuthenticationStateAsync();
        return state;
    }

    /// <summary>
    /// Starts the actor's signing-key load if it hasn't been started yet (idempotent, non-blocking).
    /// Fetches the actor document (cookie-auth, same-origin) which includes the owner-only
    /// <c>privateKey</c> PEM extension, loads it (WebCrypto in WASM, BCL on server), and registers it
    /// with the key provider so the signing handler can resolve it. The synchronous
    /// <see cref="Client"/> / <see cref="LocalModeration"/> / <see cref="MediaClient"/> properties call
    /// this and then only return a client if the load has ALREADY completed — they never block, because
    /// WebAssembly cannot wait on a monitor (Task.Wait / GetAwaiter().GetResult() throw
    /// PlatformNotSupportedException on this runtime).
    /// </summary>
    private void EnsureKeyLoadStarted()
    {
        _keyLoaded ??= LoadKeyAsync();
    }

    /// <summary>
    /// True when the key-load task has completed successfully (the signing key is registered and the
    /// client is safe to build). The synchronous client properties consult this before building a
    /// client; when false they return null and the page re-renders after <see cref="EnsureReadyAsync"/>.
    /// </summary>
    private bool KeyLoadSucceeded
        => _keyLoaded is { IsCompletedSuccessfully: true } && _keyLoaded.Result;

    private async Task<bool> LoadKeyAsync()
    {
        var actorId = ActorId;
        if (actorId is not { } me)
        {
            return false;
        }

        try
        {
            // The owner-only actor-document read must go same-origin: it carries the site cookie (cookie
            // auth) and the FQDN IRI is rewritten to a same-origin path by the handler (a cross-origin
            // read would be CORS-blocked). _sameOriginHttp is that same-origin client.
            using var response = await _sameOriginHttp.GetAsync(me.Value);
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

    /// <inheritdoc/>
    public async Task<string?> ProxyGetAsync(string target, string? accept, CancellationToken ct = default)
    {
        if (!IsSignedIn)
        {
            return null;
        }

        try
        {
            var proxyUri = $"{_browserBase.Scheme}://{_browserBase.Authority}/ap/v1/proxy/{Uri.EscapeDataString(target)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, proxyUri);
            request.Headers.TryAddWithoutValidation("X-Iris-Proxy-Method", "GET");
            if (accept is not null)
            {
                request.Headers.Accept.ParseAdd(accept);
            }

            using var response = await _sameOriginHttp.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }
}
