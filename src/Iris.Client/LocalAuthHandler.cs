using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Iris.Client;

/// <summary>
/// A <see cref="DelegatingHandler"/> that authenticates local, non-federated requests with Basic auth.
/// </summary>
/// <remarks>
/// Some client operations are local to the home instance rather than federated: the F-07 mute
/// (<c>POST {actor}/mutes/{target}</c>) is an Iris-specific moderation decision (there is no
/// ActivityStreams <c>Mute</c> type), so it is not signed and delivered to an inbox — it is a
/// Basic-authenticated request to the acting actor's own instance, which identifies the actor from the
/// credentials (the host's <c>IActorCredentialValidator</c>) and records the mute edge. This handler
/// adds the <c>Authorization: Basic</c> header and forwards the request unsigned (it is not part of the
/// signed <see cref="SigningHandler"/> pipeline, which would throw for a request it cannot sign).
/// </remarks>
public sealed class LocalAuthHandler : DelegatingHandler
{
    private readonly string _authorization;

    /// <summary>
    /// Initializes a new <see cref="LocalAuthHandler"/> over an explicit inner handler.
    /// </summary>
    /// <param name="credentials">The Basic-auth credentials (the acting actor's username + password).</param>
    /// <param name="innerHandler">The inner handler (the transport) to forward the request to.</param>
    public LocalAuthHandler(ProxyCredentials credentials, HttpMessageHandler innerHandler)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
        ArgumentNullException.ThrowIfNull(credentials);
        InnerHandler = innerHandler;
        _authorization = "Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}"));
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            _authorization["Basic ".Length..]);
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
