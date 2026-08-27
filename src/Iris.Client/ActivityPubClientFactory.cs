using System.Net;
using System.Net.Http.Headers;
using Iris.Core;

namespace Iris.Client;

/// <summary>
/// The default <see cref="IActivityPubClientFactory"/>. Composes an <see cref="ActivityPubClient"/> with a
/// signing pipeline: <see cref="SigningHandler"/> over the caller-supplied transport handler.
/// </summary>
/// <remarks>
/// The <see cref="SigningHandler"/> signs as <see cref="ActivityPubClientOptions.ActorId"/>. The signer and
/// key store are owned by the factory; the key store must outlive the returned clients (keys are
/// borrowed, not cloned — see <see cref="IKeyStore"/>). The transport handler passed to
/// <see cref="Create"/> is not owned by the returned client.
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

        // The client owns this HttpClient (and disposes it on Dispose); the transport
        // httpHandler is NOT disposed by it.
        var httpClient = new HttpClient(signingHandler, disposeHandler: true)
        {
            Timeout = options.HttpClientTimeout ?? Timeout.InfiniteTimeSpan,
        };

        var caches = options.Caches;
        return new ActivityPubClient(
            httpClient,
            caches?.Actors,
            caches?.CollectionPages);
    }
}
