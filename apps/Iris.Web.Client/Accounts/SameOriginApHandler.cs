using System.Net.Http;

namespace Iris.Web.Client.Accounts;

/// <summary>
/// A <see cref="DelegatingHandler"/> that rewrites requests addressed to the instance's advertised
/// (public FQDN) ActivityPub base into same-origin, path-relative requests.
/// </summary>
/// <remarks>
/// The WASM client dials the instance on the browser's origin (e.g. <c>http://localhost:8088</c>) but
/// the instance's canonical IRIs are advertised on the public FQDN (e.g.
/// <c>https://iris.luit.ink</c> — <c>IRIS_ADVERTISE_BASE</c>). When the client sends a request to an
/// absolute FQDN IRI, the browser treats it as a cross-origin request: it is blocked by CORS (the
/// server does not emit <c>Access-Control-Allow-Origin</c> for ActivityPub routes) and it cannot carry
/// the site's cookie. A signed ActivityPub write (a post to the actor's own outbox) or an owner-only
/// read (the actor document's <c>privateKey</c> extension) therefore fails.
///
/// This handler transparently rewrites an absolute request whose host matches the advertised base into
/// a same-origin request (path + query only). It MUST be the OUTERMOST handler in the signed pipeline
/// (wrapping the SigningHandler) so the URL is rewritten BEFORE the request is signed: the signature is
/// then computed over the same-origin host (the browser sends the site's dial host, e.g.
/// <c>localhost:8088</c>, because <c>Host</c> is a forbidden header it cannot override), which is exactly
/// the host the server's verifier reads from the wire (<c>request.Headers.Host</c>) — so the signature
/// validates. If the rewrite happened after signing, the host component of the signature would not match
/// the wire host and the server would reject the request (401).
///
/// The canonical FQDN IRIs are preserved in the activity payload (the actor's <c>attributedTo</c>, the
/// note's references) — only the transport URL is rewritten, so stored data keeps its advertised-base
/// IRIs. The site cookie is attached to the same-origin request (cookie auth for the owner-only read).
///
/// Because <see cref="System.Net.Http.HttpClient.SendAsync(HttpRequestMessage)"/> requires an absolute
/// <see cref="HttpRequestMessage.RequestUri"/>, the rewrite rebuilds the URI as
/// <c>{browser origin scheme}://{browser origin authority}{path}{query}</c> and sets the <c>Host</c>
/// header to the browser's authority — so the browser dials its own origin (same-origin: cookie is sent,
/// no CORS) and the server receives the browser's host (the value the <c>host</c> signature component
/// was signed over, since this handler runs outermost, before the <see cref="Iris.Client.Pipeline.SigningHandler"/>).
///
/// When no advertised base is configured and the request is not addressed to the advertised/known base,
/// the handler is a pass-through.
/// </remarks>
public sealed class SameOriginApHandler : DelegatingHandler
{
    private readonly Uri? _advertiseBase;
    private readonly Uri _browserBase;

    /// <summary>
    /// Initializes the handler.
    /// </summary>
    /// <param name="inner">The transport handler to send through (a browser <see cref="HttpClientHandler"/>).</param>
    /// <param name="advertiseBase">
    /// The instance's advertised (public FQDN) base. When set, requests addressed to that base's host are
    /// rewritten to same-origin. (The WASM client reads this from its <c>appsettings.json</c>, which the
    /// server populates from <c>IRIS_ADVERTISE_BASE</c>.)
    /// </param>
    /// <param name="browserBase">
    /// The browser's origin (the instance's dial base, e.g. <c>http://localhost:8088/</c>). Rewritten
    /// requests are rebuilt on this origin and carry this origin's authority in the <c>Host</c> header,
    /// so the browser dials its own origin (same-origin: cookie sent, no CORS).
    /// </param>
    public SameOriginApHandler(HttpClientHandler inner, Uri? advertiseBase, Uri browserBase)
    {
        InnerHandler = inner ?? throw new ArgumentNullException(nameof(inner));
        _advertiseBase = advertiseBase is null ? null : new Uri(TrimTrailingSlash(advertiseBase.ToString()));
        _browserBase = browserBase;
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is not null && uri.IsAbsoluteUri && ShouldRewrite(uri))
        {
            // Rebuild the request as an absolute URI on the BROWSER origin (same-origin: the browser dials
            // its own origin, sends the site cookie, and no CORS preflight). The Host header is set to the
            // browser's authority — the browser cannot override Host on the wire, so the server receives
            // exactly this value, which is what the (outermost-applied) signature's host component covers.
            var authority = _browserBase.Authority;
            request.RequestUri = new Uri($"{_browserBase.Scheme}://{authority}{uri.AbsolutePath}{uri.Query}");
            request.Headers.Host = authority;
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// True when the absolute request should be sent same-origin: it is addressed to the advertised
    /// base's host (the instance's canonical FQDN). The WASM client learns the advertised base from its
    /// <c>appsettings.json</c> (populated by the server from <c>IRIS_ADVERTISE_BASE</c>), so the precise
    /// FQDN host is known and matched. When no advertised base is configured (the client dials the same
    /// origin it advertises), nothing is rewritten (requests are already same-origin).
    /// </summary>
    private bool ShouldRewrite(Uri uri)
    {
        if (_advertiseBase is not null)
        {
            return string.Equals(uri.DnsSafeHost, _advertiseBase.DnsSafeHost, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string TrimTrailingSlash(string value)
        => value.EndsWith("/", StringComparison.Ordinal) ? value[..^1] : value;
}
