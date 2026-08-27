using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iris.Core;

namespace Iris.Client;

/// <summary>
/// Performs WebFinger (RFC 8410 / ActivityPub §3) discovery: resolves an account handle
/// (e.g. <c>@user@example.com</c>) or URL to the actor's IRI via a <c>/.well-known/webfinger</c>
/// query.
/// </summary>
/// <remarks>
/// A WebFinger response is a <c>application/jrd+json</c> document whose <c>links</c> array
/// contains an entry with <c>rel = "self"</c> (and <c>type = "application/activity+json"</c>
/// for ActivityPub). The <c>href</c> of that entry is the actor IRI.
/// </remarks>
public sealed class WebFingerClient
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new <see cref="WebFingerClient"/>.
    /// </summary>
    /// <param name="http">The HTTP client to use for requests.</param>
    public WebFingerClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// The WebFinger media type.
    /// </summary>
    public const string WebFingerContentType = "application/jrd+json";

    /// <summary>
    /// Resolves the actor IRI for the given account via WebFinger.
    /// </summary>
    /// <param name="account">The account handle (e.g. <c>@user@example.com</c>) or a full
    /// <c>acct:</c> URI. The <c>@</c> prefix is optional; a bare <c>user@host</c> is also accepted.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The actor IRI, or null if discovery failed or no <c>self</c> link was found.</returns>
    public async Task<Iri?> ResolveActorAsync(string account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var subject = NormalizeSubject(account);

        // The WebFinger resource lives on the *account's own host*, so derive the base URL from
        // the host in the acct: URI rather than relying on the HttpClient's BaseAddress.
        var host = Uri.UnescapeDataString(subject["acct:".Length..]);
        var at = host.IndexOf('@');
        var hostPart = at >= 0 ? host[(at + 1)..] : host;
        var wellKnown = $"https://{hostPart}/.well-known/webfinger?resource={Uri.EscapeDataString(subject)}";

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _http.GetAsync(wellKnown, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Network failure / unreachable host: discovery simply fails.
            return null;
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            return null;
        }

        WebFingerResponse? response;
        try
        {
            response = await httpResponse
                .Content.ReadFromJsonAsync<WebFingerResponse>(_options, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }

        if (response is null || response.Links is null)
        {
            return null;
        }

        var selfLink = response.Links
            .FirstOrDefault(l => string.Equals(l.Rel, "self", StringComparison.OrdinalIgnoreCase)
                                 && l.Href is not null
                                 && (l.Type is null || l.Type.Contains("activity", StringComparison.OrdinalIgnoreCase)));

        return selfLink?.Href is { } href ? new Iri(href) : null;
    }

    /// <summary>
    /// Normalizes an account string into a WebFinger <c>resource</c> URI.
    /// </summary>
    /// <param name="account">The account handle or <c>acct:</c> URI.</param>
    /// <returns>A <c>acct:user@host</c> URI.</returns>
    /// <exception cref="ArgumentException">When the account cannot be normalized.</exception>
    public static string NormalizeSubject(string account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            throw new ArgumentException("Account must not be empty.", nameof(account));
        }

        var value = account.Trim();
        if (value.StartsWith("acct:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (value.StartsWith('@'))
        {
            value = value[1..];
        }

        if (!value.Contains('@'))
        {
            throw new ArgumentException($"Cannot derive an acct: URI from '{account}'.", nameof(account));
        }

        return "acct:" + value;
    }

    /// <summary>
    /// A WebFinger (JRD) response document.
    /// </summary>
    public sealed class WebFingerResponse
    {
        /// <summary>
        /// Gets or sets the <c>subject</c> URI that was looked up.
        /// </summary>
        public string? Subject { get; set; }

        /// <summary>
        /// Gets or sets the <c>properties</c> object.
        /// </summary>
        public Dictionary<string, JsonElement>? Properties { get; set; }

        /// <summary>
        /// Gets or sets the <c>links</c> array.
        /// </summary>
        public IReadOnlyList<WebFingerLink>? Links { get; set; }
    }

    /// <summary>
    /// A single link entry in a WebFinger response.
    /// </summary>
    public sealed class WebFingerLink
    {
        /// <summary>
        /// Gets or sets the relation (e.g. <c>self</c>, <c>http://webfinger.net/rel/profile</c>).
        /// </summary>
        public string? Rel { get; set; }

        /// <summary>
        /// Gets or sets the media type of the linked resource.
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets the IRI of the linked resource.
        /// </summary>
        public string? Href { get; set; }
    }
}
