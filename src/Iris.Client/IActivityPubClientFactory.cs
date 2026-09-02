using Iris.Core;

namespace Iris.Client;

/// <summary>
/// Builds configured <see cref="IActivityPubClient"/> instances.
/// </summary>
/// <remarks>
/// The factory owns the composition of the client's HTTP pipeline: it wires a
/// <see cref="SigningHandler"/> (signing as <see cref="ActivityPubClientOptions.ActorId"/>, backed by the
/// <see cref="IKeyProvider"/> and <see cref="IKeyStore"/>) over the caller-supplied transport handler.
/// The returned client owns its <see cref="System.Net.Http.HttpClient"/> and disposes it on
/// <see cref="System.IDisposable.Dispose"/>.
/// </remarks>
public interface IActivityPubClientFactory
{
    /// <summary>
    /// Creates a new <see cref="IActivityPubClient"/>.
    /// </summary>
    /// <param name="options">Client options (must include <see cref="ActivityPubClientOptions.ActorId"/>).</param>
    /// <param name="httpHandler">The transport handler (e.g. <see cref="System.Net.Http.HttpClientHandler"/>).
    /// Not owned by the returned client.</param>
    /// <returns>A configured <see cref="IActivityPubClient"/> that owns its <see cref="System.Net.Http.HttpClient"/>.</returns>
    public IActivityPubClient Create(ActivityPubClientOptions options, HttpMessageHandler httpHandler);

    /// <summary>
    /// Creates a new <see cref="ILocalModerationClient"/> — the client for local, non-federated
    /// moderation decisions (a mute, F-07, and a relay subscription, F-06).
    /// </summary>
    /// <param name="options">Client options. When <see cref="ActivityPubClientOptions.LocalCredentials"/>
    /// is set, the returned client's no-credential overloads use it (a
    /// <see cref="Pipeline.LocalAuthHandler"/> wrapping the transport); otherwise only the
    /// explicit-<see cref="Pipeline.ProxyCredentials"/> overloads work.</param>
    /// <param name="httpHandler">The transport handler (e.g. <see cref="System.Net.Http.HttpClientHandler"/>).
    /// Not owned by the returned client (a provided default handler is shared across calls).</param>
    /// <returns>A configured <see cref="ILocalModerationClient"/>.</returns>
    public ILocalModerationClient CreateLocalModerationClient(ActivityPubClientOptions options, HttpMessageHandler httpHandler);

    /// <summary>
    /// Creates a new <see cref="IMediaClient"/> — the client for uploading a note's attachment (Phase
    /// 20.4 (a)): a local, non-federated, Basic-authenticated multipart POST to the acting actor's own
    /// instance.
    /// </summary>
    /// <param name="options">Client options. When <see cref="ActivityPubClientOptions.LocalCredentials"/>
    /// is set, the returned client's no-credential overloads use it (a
    /// <see cref="Pipeline.LocalAuthHandler"/> wrapping the transport); otherwise only the
    /// explicit-<see cref="Pipeline.ProxyCredentials"/> overloads work.</param>
    /// <param name="httpHandler">The transport handler (e.g. <see cref="System.Net.Http.HttpClientHandler"/>).
    /// Not owned by the returned client (a provided default handler is shared across calls).</param>
    /// <returns>A configured <see cref="IMediaClient"/>.</returns>
    public IMediaClient CreateMediaClient(ActivityPubClientOptions options, HttpMessageHandler httpHandler);
}
