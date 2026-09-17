using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Client.Pipeline;

/// <summary>
/// A <see cref="DelegatingHandler"/> that logs non-success HTTP responses (status + body) at
/// Warning level. TEMP(157): added for the key-resolution bootstrap investigation; remove once
/// the root cause is fixed and the permanent diagnostics are in place.
/// </summary>
public sealed class ResponseLogHandler : DelegatingHandler
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new <see cref="ResponseLogHandler"/>.
    /// </summary>
    /// <param name="inner">The inner (transport) handler.</param>
    /// <param name="logger">The logger. Null falls back to a no-op logger.</param>
    public ResponseLogHandler(HttpMessageHandler inner, ILogger? logger = null)
        : base(inner)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                body = "<unreadable>";
            }

            _logger.LogWarning(
                "Outbound {Method} {Uri} -> {Status} (body: {Body})",
                request.Method,
                request.RequestUri,
                (int)response.StatusCode,
                body.Length > 300 ? body[..300] : body);
        }

        return response;
    }
}
