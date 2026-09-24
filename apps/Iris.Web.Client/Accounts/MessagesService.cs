using System.Net.Http.Json;
using System.Text.Json;

namespace Iris.Web.Client.Accounts;

/// <summary>
/// A scoped service for the DM inbox (S116): fetching the merged sent+received direct-message list
/// and marking it read. Operates via HTTP against the server's <c>/local/v1/messages</c> endpoints,
/// so it works in both InteractiveServer and InteractiveWebAssembly render modes.
/// </summary>
public sealed class MessagesService
{
    private readonly HttpClient _http;

    public MessagesService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Fetches a page of the user's direct messages (sent + received, merged, newest first).
    /// </summary>
    /// <param name="limit">Page size (default 20, max 100).</param>
    /// <param name="offset">Offset into the result set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The messages page (items, total count, next-page URL or null, read cursor).</returns>
    public async Task<MessagesPage> GetMessagesAsync(
        int limit = 20, int offset = 0, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(
            $"/local/v1/messages?limit={limit}&offset={offset}", ct);
        var page = JsonSerializer.Deserialize<MessagesPage>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return page ?? new MessagesPage([], 0, null, null);
    }

    /// <summary>
    /// Marks all DMs as read. Returns the (now-zero) unread count.
    /// </summary>
    public async Task<int> MarkAllReadAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync<object>("/local/v1/messages/read", new { }, ct);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        var body = JsonSerializer.Deserialize<UnreadCountDto>(
            await response.Content.ReadAsStringAsync(ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return body?.Unread ?? 0;
    }

    private sealed record UnreadCountDto(int Unread);
}

/// <summary>
/// A page of direct messages from the server's <c>GET /local/v1/messages</c> endpoint.
/// </summary>
/// <param name="Items">The DM activities (Create activities carrying the direct-message object).</param>
/// <param name="TotalItems">The total number of DMs (before paging).</param>
/// <param name="NextPage">The URL for the next page, or null when there is no next page.</param>
/// <param name="ReadAt">The account's messages-read cursor, or null when never marked read.</param>
public sealed record MessagesPage(
    IReadOnlyList<JsonElement> Items,
    int TotalItems,
    string? NextPage,
    DateTimeOffset? ReadAt);
