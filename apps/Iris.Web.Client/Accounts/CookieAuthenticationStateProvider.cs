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
        return _cached ??= ResolveAsync();
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
        try
        {
            var response = await _http.GetAsync("/local/v1/session");
            if (!response.IsSuccessStatusCode)
            {
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            }

            // The server serializes the claims as camelCase (actorIri, etc.); ReadFromJsonAsync's
            // default options are case-sensitive, so deserialize with a case-insensitive property
            // name matcher so every field binds regardless of the server's casing.
            var json = System.Text.Json.JsonSerializer.Deserialize<SessionResponse>(
                await response.Content.ReadAsStringAsync(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (json is null)
            {
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            }

            var identity = new ClaimsIdentity(
                authenticationType: "cookie",
                nameType: ClaimTypes.Name,
                roleType: ClaimTypes.Role);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, json.Id));
            identity.AddClaim(new Claim(ClaimTypes.Name, json.Username));
            identity.AddClaim(new Claim(ActorClaims.ActorIri, json.ActorIri));
            identity.AddClaim(new Claim(ClaimTypes.Role, json.Role));

            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }
    }

    private sealed record SessionResponse(string Id, string Username, string ActorIri, string Role);
}
