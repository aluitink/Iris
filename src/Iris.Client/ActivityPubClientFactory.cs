using System.Net;
using System.Net.Http.Headers;
using Iris.Core;

namespace Iris.Client;

/// <summary>
/// The default <see cref="IActivityPubClientFactory"/>. Composes an <see cref="ActivityPubClient"/> with a
/// handler pipeline: <see cref="RetryHandler"/> → <see cref="JsonLdHandler"/> →
/// <see cref="SigningHandler"/> over the caller-supplied transport handler.
/// </summary>
/// <remarks>
/// Pipeline order matters:
/// <list type="number">
/// <item><see cref="RetryHandler"/> (outermost) replays the whole signed request on transient
/// failure, so it must wrap the signing stage.</item>
/// <item><see cref="JsonLdHandler"/> sets <c>Accept</c>/<c>Content-Type</c> before the
/// <see cref="SigningHandler"/> reads the content type into the signature.</item>
/// <item><see cref="SigningHandler"/> adds <c>Date</c>/<c>Signature</c> and (for body requests) the
/// digest-covered content.</item>
/// </list>
/// The <see cref="SigningHandler"/> signs as <see cref="ActivityPubClientOptions.ActorId"/>. The signer and
/// key store are owned by the factory; the key store must outlive the returned clients (keys are
/// borrowed, not cloned — see <see cref="IKeyStore"/>). The transport handler passed to
/// <see cref="IActivityPubClientFactory.Create(ActivityPubClientOptions, System.Net.Http.HttpMessageHandler)"/>
/// is not owned by the returned client.
/// </remarks>
public sealed class ActivityPubClientFactory : IActivityPubClientFactory
{
    private readonly IKeyProvider _keyProvider;
    private readonly IKeyStore _keyStore;
    private readonly ISignatureSigner _signer;

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClientFactory"/>.
    /// </summary>
    /// <param name="keyStore">The key store backing <paramref name="keyProvider"/>.</param>
    /// <param name="keyProvider">Resolves the signing <see cref="IIdentity"/> for an actor IRI.</param>
    /// <param name="signer">The HTTP-signature signer. Defaults to <see cref="HttpSignatureSigner"/>.</param>
    public ActivityPubClientFactory(IKeyStore keyStore, IKeyProvider keyProvider, ISignatureSigner? signer = null)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _signer = signer ?? new HttpSignatureSigner(keyStore);
    }

    /// <inheritdoc/>
    public IActivityPubClient Create(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
        => Create(options, httpHandler, outermost: null);

    /// <summary>
    /// Builds a signed <see cref="IActivityPubClient"/>, optionally wrapping the signed pipeline in an
    /// outermost handler.
    /// </summary>
    /// <param name="options">The client options (actor to sign as, retry, proxy, caches).</param>
    /// <param name="httpHandler">The caller-supplied transport handler (e.g. a browser
    /// <see cref="HttpClientHandler"/> for cookie auth).</param>
    /// <param name="outermost">
    /// An optional handler to wrap the ENTIRE signed pipeline (outermost). Used by the browser
    /// (WASM) session to rewrite FQDN-addressed requests to same-origin BEFORE they are signed — the
    /// signature's <c>host</c> component must match the host the server receives on the wire, so the
    /// rewrite must precede the <see cref="Pipeline.SigningHandler"/>. Null for the default (no
    /// outermost wrapper) behavior.
    /// </param>
    public IActivityPubClient Create(
        ActivityPubClientOptions options,
        HttpMessageHandler httpHandler,
        DelegatingHandler? outermost)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpHandler);
        if (options.ActorId is null)
        {
            throw new ArgumentException("ActorId is required for a signed ActivityPub client.", nameof(options));
        }

        var signingHandler = new SigningHandler(_signer, _keyProvider, httpHandler)
        {
            ActorId = options.ActorId.Value,
        };

        // Retry → JsonLd → Signing → transport. Retry is outermost so it replays the signed
        // request; JsonLd sets content negotiation headers before Signing signs them in.
        DelegatingHandler pipeline = new JsonLdHandler(signingHandler);
        if (options.EnableRetry)
        {
            pipeline = new RetryHandler(options.MaxRetryAttempts, pipeline);
        }

        // An outermost handler (e.g. the browser's SameOriginApHandler) rewrites the request URL to
        // same-origin BEFORE the Signed request is built, so the signature's host component matches the
        // wire host the server receives. It sits outside Retry so the rewrite is stable across retries.
        if (outermost is not null)
        {
            outermost.InnerHandler = pipeline;
            pipeline = outermost;
        }

        // Proxy fallback (Phase 6): when the home instance's proxy is configured, wrap the whole
        // signed pipeline so a 401/403 from a remote instance is retried through the proxy (which
        // re-signs with the actor's key). The proxy POST is unsigned (the proxy signs the forwarded
        // request), so it must bypass the SigningHandler — hence ProxyFallbackHandler is outermost.
        if (options.ProxyBaseUrl is { } proxyBase && options.ProxyCredentials is { } proxyCreds)
        {
            pipeline = new ProxyFallbackHandler(
                proxyBase,
                proxyCreds,
                pipeline,
                options.AlwaysProxy,
                options.DialBaseUri,
                options.RouteCrossInstanceReadsViaProxy);
        }

        // The client owns this HttpClient (and disposes the pipeline on Dispose); the transport
        // httpHandler is NOT disposed by it.
        var httpClient = new HttpClient(pipeline, disposeHandler: true)
        {
            Timeout = options.HttpClientTimeout ?? Timeout.InfiniteTimeSpan,
        };

        var caches = options.Caches;
        return new ActivityPubClient(
            httpClient,
            caches?.Actors,
            caches?.CollectionPages);
    }

    /// <inheritdoc/>
    public ILocalModerationClient CreateLocalModerationClient(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpHandler);

        // Local moderation (F-07 mute, F-06 relay): when the home instance's Basic-auth credentials are
        // configured, the client can perform local, non-federated moderation requests (a mute or a relay
        // subscription is not a signed inbox delivery — it is a Basic-authenticated POST to the actor's
        // own instance). The local-auth handler is a separate, unsigned pipeline (it must not go through
        // the SigningHandler, which would throw for a request it cannot sign). With no LocalCredentials
        // the client is built without a default handler — only the explicit-credential overloads work.
        var localAuth = options.LocalCredentials is { } localCreds
            ? new LocalAuthHandler(localCreds, httpHandler)
            : null;

        return new LocalModerationClient(localAuth);
    }

    /// <inheritdoc/>
    public IMediaClient CreateMediaClient(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpHandler);

        // Media upload (Phase 20.4 (a)): when the home instance's Basic-auth credentials are configured,
        // the client can upload a note's attachment (a file is not a signed inbox delivery — it is a
        // Basic-authenticated multipart POST to the actor's own instance). The local-auth handler is a
        // separate, unsigned pipeline (it must not go through the SigningHandler, which would throw for a
        // request it cannot sign). With no LocalCredentials the client is built without a default handler
        // — only the explicit-credential overload works.
        var localAuth = options.LocalCredentials is { } localCreds
            ? new LocalAuthHandler(localCreds, httpHandler)
            : null;

        return new MediaClient(localAuth);
    }
}
