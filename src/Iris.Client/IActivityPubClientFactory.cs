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
}
