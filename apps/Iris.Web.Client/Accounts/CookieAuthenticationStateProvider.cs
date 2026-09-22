using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Iris.Web.Client.Accounts;

/// <summary>
/// WASM-compatible <see cref="AuthenticationStateProvider"/> that determines the signed-in user by
/// calling the server's <c>GET /local/v1/session</c> endpoint (cookie-auth, same-origin). If the
/// request succeeds, the response JSON carries the user's claims (id, username, actor IRI, role);
/// if it returns 401, the user is signed out. The state is cached after the first resolve.
/// </summary>
public class CookieAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly HttpClient _http;
    private Task<AuthenticationState>? _cached;

    public CookieAuthenticationStateProvider(HttpClient http)
    {
        _http = http;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // Re-resolve on every read (never cache across reads): a circuit that first rendered while
        // signed out (e.g. a page's initial render before the sign-in cookie round-trip completes)
        // must observe the signed-in state on the re-render that follows EnsureReadyAsync — a
        // one-shot cache would pin the stale signed-out identity for the circuit's lifetime. The
        // underlying session fetch is a cheap same-origin cookie-auth read.
        _cached = ResolveAsync();
        return _cached;
    }

    /// <summary>
    /// Re-resolves the auth state (call after sign-in/sign-out to refresh).
    /// </summary>
    public Task NotifySignInAsync()
    {
        _cached = ResolveAsync();
        NotifyAuthenticationStateChanged(_cached);
        return Task.CompletedTask;
    }

    private async Task<AuthenticationState> ResolveAsync()
    {
        // First, fetch the public session (no auth required) to get the public feed IRI and the
        // instance's effective advertised base (the FQDN the server serves its iris: namespace under).
        var publicSession = await FetchPublicSessionAsync();

        try
        {
            var response = await _http.GetAsync("/local/v1/session");
            if (!response.IsSuccessStatusCode)
            {
                // Signed out: return an unauthenticated state with the public feed IRI claim
                // so the client can render the public feed for a logged-out visitor.
                return new AuthenticationState(new ClaimsPrincipal(BuildPublicIdentity(publicSession)));
            }

            // The server serializes the claims as camelCase (actorIri, etc.); ReadFromJsonAsync's
            // default options are case-sensitive, so deserialize with a case-insensitive property
            // name matcher so every field binds regardless of the server's casing.
            var json = System.Text.Json.JsonSerializer.Deserialize<SessionResponse>(
                await response.Content.ReadAsStringAsync(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (json is null)
            {
                return new AuthenticationState(new ClaimsPrincipal(BuildPublicIdentity(publicSession)));
            }

            var identity = new ClaimsIdentity(
                authenticationType: "cookie",
                nameType: ClaimTypes.Name,
                roleType: ClaimTypes.Role);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, json.Id));
            identity.AddClaim(new Claim(ClaimTypes.Name, json.Username));
            identity.AddClaim(new Claim(ActorClaims.ActorIri, json.ActorIri));
            identity.AddClaim(new Claim(ClaimTypes.Role, json.Role));
            identity.AddClaim(new Claim(ActorClaims.PublicFeedIri, json.PublicFeedIri ?? publicSession?.PublicFeedIri ?? ""));
            AddServerBaseUriClaim(identity, publicSession?.ServerBaseUri);

            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            return new AuthenticationState(new ClaimsPrincipal(BuildPublicIdentity(publicSession)));
        }
    }

    /// <summary>
    /// Builds a signed-out (public) identity carrying the public feed IRI claim and the server base
    /// URI claim (so the client can derive the instance's iris: namespace even when logged out).
    /// </summary>
    private static ClaimsIdentity BuildPublicIdentity(PublicSessionResponse? publicSession)
    {
        var identity = new ClaimsIdentity();
        if (publicSession?.PublicFeedIri is not null)
        {
            identity.AddClaim(new Claim(ActorClaims.PublicFeedIri, publicSession.PublicFeedIri));
        }
        AddServerBaseUriClaim(identity, publicSession?.ServerBaseUri);
        return identity;
    }

    /// <summary>
    /// Adds the <see cref="ActorClaims.ServerBaseUri"/> claim when the server advertised its effective
    /// base (a non-empty absolute URI). Absent on older servers (no claim) so the client falls back to
    /// its baked-in AdvertiseBase.
    /// </summary>
    private static void AddServerBaseUriClaim(ClaimsIdentity identity, string? serverBaseUri)
    {
        if (string.IsNullOrWhiteSpace(serverBaseUri))
        {
            return;
        }

        identity.AddClaim(new Claim(ActorClaims.ServerBaseUri, serverBaseUri.TrimEnd('/')));
    }

    /// <summary>
    /// Fetches the no-auth public session (public feed IRI + the instance's effective advertised base).
    /// Returns null on failure.
    /// </summary>
    private async Task<PublicSessionResponse?> FetchPublicSessionAsync()
    {
        try
        {
            var response = await _http.GetAsync("/local/v1/session/public");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize<PublicSessionResponse>(
                await response.Content.ReadAsStringAsync(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private sealed record SessionResponse(string Id, string Username, string ActorIri, string Role, string? PublicFeedIri);
    private sealed record PublicSessionResponse(string? PublicFeedIri, string? ServerBaseUri);
}
