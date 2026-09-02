namespace Iris.Client;

/// <summary>
/// The typed result of an ActivityPub delivery (an <c>ActivityPubClient.DeliverAsync</c> call or a
/// convenience method that delegates to it, such as <c>FollowAsync</c> or <c>LikeAsync</c>).
/// Carries the HTTP status code, a success flag, the response body (when present), and — when the
/// server minted the delivered activity's id (decision 055) and returned the created object in a 2xx
/// body — the minted <see cref="MintedId"/> so the caller can learn the id it should reference for any
/// follow-up activity (an <c>Undo</c>, a reply, a delete).
/// </summary>
/// <param name="StatusCode">The HTTP status code of the delivery response (e.g. <c>202</c>).</param>
/// <param name="IsSuccess">Whether the delivery was accepted (HTTP status 2xx).</param>
/// <param name="Body">The response body text (empty string when the response had no body).</param>
/// <param name="MintedId">The id the server minted for the delivered activity, when the server is the
/// id authority (decision 055) and returned the created object in the response body; null when the
/// response carried no parseable activity id (e.g. a 204 no-content, a 4xx, or a non-Activity body).</param>
public sealed record DeliveryResult(int StatusCode, bool IsSuccess, string Body, string? MintedId = null);
