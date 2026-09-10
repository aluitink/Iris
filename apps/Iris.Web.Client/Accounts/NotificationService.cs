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

    /// <summary>
    /// Fetches a page of notifications from the server's filtered list endpoint.
    /// </summary>
    /// <param name="type">Optional activity type filter (e.g. "Like", "Follow").</param>
    /// <param name="limit">Page size (default 20, max 100).</param>
    /// <param name="offset">Offset into the result set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The notification page (items, total count, next-page URL or null).</returns>
    public async Task<NotificationPage> GetNotificationsAsync(
        string? type = null, int limit = 20, int offset = 0, CancellationToken ct = default)
    {
        var query = $"limit={limit}&offset={offset}";
        if (!string.IsNullOrWhiteSpace(type))
        {
            query += $"&type={Uri.EscapeDataString(type)}";
        }

        var json = await _http.GetStringAsync($"/local/v1/notifications?{query}", ct);
        var page = System.Text.Json.JsonSerializer.Deserialize<NotificationPage>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return page ?? new NotificationPage([], 0, null);
    }

    private sealed record UnreadCountDto(int Unread);
}

/// <summary>
/// A page of notifications from the server's <c>GET /local/v1/notifications</c> endpoint.
/// </summary>
/// <param name="Items">The notification items (ActivityStreams activities).</param>
/// <param name="TotalItems">The total number of matching notifications (before paging).</param>
/// <param name="NextPage">The URL for the next page, or null when there is no next page.</param>
public sealed record NotificationPage(
    IReadOnlyList<System.Text.Json.JsonElement> Items,
    int TotalItems,
    string? NextPage);
