using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Iris.Samples.SampleBlazorClient.Explorer;

/// <summary>
/// The OAuth2 authorization-code browser flow (Phase 15.2): the client-side helper that builds the
/// <c>/ap/v1/oauth2/authorize</c> redirect URL (the browser is sent there), and, after the browser
/// lands back on the app's callback route, exchanges the authorization <c>code</c> for a Bearer token
/// at <c>/ap/v1/oauth2/token</c>. It is the browser half of the flow the
/// <see cref="Iris.Client.Auth.OAuth2ClientAuthenticator"/> (Phase 15.2b) consumes.
/// </summary>
/// <remarks>
/// <para>
/// The flow is three steps: (1) the app redirects the browser to
/// <c>{dialBase}/ap/v1/oauth2/authorize?client_id=…&amp;redirect_uri=…&amp;state=…</c> (built by
/// <see cref="BuildAuthorizeUrl"/>); (2) the server auto-approves and 302-redirects the browser to
/// <c>redirect_uri?code=…&amp;state=…</c> (the app's callback route, e.g.
/// <c>http://localhost:8090/callback</c>); (3) the app reads <c>code</c> + <c>state</c> from the
/// callback URL and calls <see cref="ExchangeCodeAsync"/> to redeem the code for a Bearer token. The
/// token is then handed to an <see cref="Iris.Client.Auth.OAuth2ClientAuthenticator"/> which fetches
/// the owner-only actor document and loads the private key.
/// </para>
/// <para>
/// The <c>state</c> parameter is required (RFC 6749 §10.12, CSRF protection). The app generates it
/// (<see cref="NewState"/>), remembers it (e.g. in a URL fragment or <c>localStorage</c>), and
/// verifies the callback's <c>state</c> matches before exchanging the code. The <c>redirect_uri</c>
/// must be an absolute URI the browser can reach (the WASM app's callback route, served by the static
/// host).
/// </para>
/// </remarks>
public static class OAuth2BrowserFlow
{
    /// <summary>
    /// The path of the OAuth2 authorization endpoint (under the versioned ActivityPub route prefix).
    /// </summary>
    public const string AuthorizePath = "/ap/v1/oauth2/authorize";

    /// <summary>
    /// The path of the OAuth2 token endpoint (under the versioned ActivityPub route prefix).
    /// </summary>
    public const string TokenPath = "/ap/v1/oauth2/token";

    /// <summary>
    /// Builds the <c>state</c> value for an authorization request (a random 32-byte Base64URL string,
    /// RFC 6749 §10.12). The caller must persist it and verify the callback's <c>state</c> matches.
    /// </summary>
    /// <returns>A cryptographically random, URL-safe state string.</returns>
    public static string NewState()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').Replace("=", "");

    /// <summary>
    /// Builds the <c>redirect_uri</c> for an authorization request: the app's callback route (the URL
    /// the server will 302 the browser to, carrying <c>code</c> + <c>state</c>). For the WASM app
    /// hosted by the static host this is the static host's base URI plus the callback path (e.g.
    /// <c>http://localhost:8090/callback</c>).
    /// </summary>
    /// <param name="callbackBaseUri">The base URI the browser reaches for the WASM app (the static
    /// host's base URI, e.g. <c>http://localhost:8090</c>).</param>
    /// <param name="callbackPath">
    /// The callback route path (e.g. <c>/callback</c>). Must start with <c>/</c>.
    /// </param>
    /// <returns>The absolute callback URI.</returns>
    public static Uri BuildRedirectUri(Uri callbackBaseUri, string callbackPath = "/callback")
    {
        ArgumentNullException.ThrowIfNull(callbackBaseUri);
        ArgumentException.ThrowIfNullOrEmpty(callbackPath);
        if (!callbackPath.StartsWith('/'))
        {
            callbackPath = "/" + callbackPath;
        }

        var basePart = callbackBaseUri.ToString().TrimEnd('/');
        return new Uri($"{basePart}{callbackPath}");
    }

    /// <summary>
    /// Builds the <c>/ap/v1/oauth2/authorize</c> URL the browser is redirected to.
    /// </summary>
    /// <param name="dialBaseUri">The base URI the browser dials for the ActivityPub instance (e.g.
    /// <c>http://localhost:5000</c>).</param>
    /// <param name="clientId">
    /// The actor handle to authenticate as (e.g. <c>alice</c>). The server's <c>client_id</c> is the
    /// actor handle (the v1 model has no separate OAuth2 client registration).
    /// </param>
    /// <param name="redirectUri">The callback URI (from <see cref="BuildRedirectUri"/>).</param>
    /// <param name="state">The opaque state value (from <see cref="NewState"/>).</param>
    /// <returns>The absolute authorize URL.</returns>
    public static Uri BuildAuthorizeUrl(
        Uri dialBaseUri, string clientId, Uri redirectUri, string state)
    {
        ArgumentNullException.ThrowIfNull(dialBaseUri);
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentException.ThrowIfNullOrEmpty(state);

        var basePart = dialBaseUri.ToString().TrimEnd('/');
        return new Uri(
            $"{basePart}{AuthorizePath}" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}" +
            $"&state={Uri.EscapeDataString(state)}");
    }

    /// <summary>
    /// Reads the <c>code</c> and <c>state</c> query parameters from a callback URL (the URL the server
    /// redirected the browser to: <c>redirect_uri?code=…&amp;state=…</c>).
    /// </summary>
    /// <param name="callbackUrl">The callback URL (the browser's current address after the redirect).</param>
    /// <returns>
    /// The <c>code</c> and <c>state</c> values, or <see langword="null"/> for a missing parameter.
    /// </returns>
    public static (string? Code, string? State) ParseCallback(Uri callbackUrl)
    {
        ArgumentNullException.ThrowIfNull(callbackUrl);
        var code = QueryParam(callbackUrl, "code");
        var state = QueryParam(callbackUrl, "state");
        return (code, state);
    }

    /// <summary>
    /// Exchanges an authorization <c>code</c> for a Bearer token at <c>/ap/v1/oauth2/token</c>
    /// (RFC 6749 §4.1.3). The code is one-time: a second exchange with the same code fails.
    /// </summary>
    /// <param name="http">
    /// The HTTP client that dials the instance (the same transport the session uses). Not disposed by
    /// this method.
    /// </param>
    /// <param name="dialBaseUri">The base URI the client dials for the instance.</param>
    /// <param name="code">The authorization code from the callback (from <see cref="ParseCallback"/>).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// The Bearer <c>access_token</c>, or <see langword="null"/> when the exchange fails (bad code,
    /// server error, or a non-JSON response).
    /// </returns>
    public static async Task<string?> ExchangeCodeAsync(
        HttpClient http, Uri dialBaseUri, string code, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(dialBaseUri);
        ArgumentException.ThrowIfNullOrEmpty(code);

        var basePart = dialBaseUri.ToString().TrimEnd('/');
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
        });

        using var response = await http.PostAsync($"{basePart}{TokenPath}", form, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("access_token", out var token)
            ? token.GetString()
            : null;
    }

    private static string? QueryParam(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return null;
        }

        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            if (pair[..eq] == name)
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }
}
