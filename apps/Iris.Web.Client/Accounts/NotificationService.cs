using System.Net.Http.Json;

namespace Iris.Web.Client.Accounts;

/// <summary>
/// A scoped service that provides notification read-state operations for Blazor components.
/// Operates via HTTP against the server's <c>/local/v1/notifications/...</c> endpoints, so it
/// works in both InteractiveServer (same-origin, cookie-auth) and InteractiveWebAssembly
/// (same-origin, cookie-auth via the browser's HttpClient) render modes.
/// </summary>
public sealed class NotificationService
{
    private readonly HttpClient _http;

    /// <summary>
    /// Initializes the service.
    /// </summary>
    /// <param name="http">A cookie-authenticated <see cref="HttpClient"/> (same-origin).</param>
    public NotificationService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// The number of unread notifications for the signed-in user. Returns 0 on auth failure.
    /// </summary>
    public async Task<int> GetUnreadCountAsync(Guid accountId, CancellationToken ct = default)
    {
        // The server serializes the count as "unread" (camelCase); GetFromJsonAsync's default
        // options are case-sensitive, so deserialize explicitly with a case-insensitive property
        // name matcher so the field binds regardless of the server's casing.
        var json = await _http.GetStringAsync("/local/v1/notifications/unread-count", ct);
        var response = System.Text.Json.JsonSerializer.Deserialize<UnreadCountDto>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return response?.Unread ?? 0;
    }

    /// <summary>
    /// Marks all notifications as read for the signed-in user. Returns the (now-zero) unread count.
    /// </summary>
    public async Task<int> MarkAllReadAsync(Guid accountId, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync<object>(
            "/local/v1/notifications/read", new { }, ct);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        var body = System.Text.Json.JsonSerializer.Deserialize<UnreadCountDto>(
            await response.Content.ReadAsStringAsync(ct),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return body?.Unread ?? 0;
    }

    private sealed record UnreadCountDto(int Unread);
}
