namespace Iris.Client;

/// <summary>
/// The typed result of an ActivityPub delivery (an <c>ActivityPubClient.DeliverAsync</c> call or a
/// convenience method that delegates to it, such as <c>FollowAsync</c> or <c>LikeAsync</c>).
/// Carries the HTTP status code, a success flag, and the response body (when present) so a caller can
/// distinguish a 2xx acceptance from a 401/404/429 without pattern-matching on a bare integer.
/// </summary>
/// <param name="StatusCode">The HTTP status code of the delivery response (e.g. <c>202</c>).</param>
/// <param name="IsSuccess">Whether the delivery was accepted (HTTP status 2xx).</param>
/// <param name="Body">The response body text (empty string when the response had no body).</param>
public sealed record DeliveryResult(int StatusCode, bool IsSuccess, string Body);
