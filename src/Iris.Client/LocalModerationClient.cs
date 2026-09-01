using System.Net;

namespace Iris.Client;

/// <summary>
/// The default <see cref="ILocalModerationClient"/>: local, Basic-authenticated moderation requests
/// (a mute, F-07, and a relay subscription, F-06) to the acting actor's own home instance.
/// </summary>
/// <remarks>
/// Neither a mute nor a relay subscription is an ActivityStreams activity, so neither is a signed inbox
/// delivery: each is a body-less Basic-authenticated POST to the acting actor's own instance, which
/// identifies the actor from the credentials (the host's <c>IActorCredentialValidator</c>) and records
/// or removes the edge. The client holds an optional default <see cref="LocalAuthHandler"/> (built from
/// <see cref="ActivityPubClientOptions.LocalCredentials"/> by the factory) used by the no-credential
/// overloads; the explicit-<see cref="ProxyCredentials"/> overloads build a request-scoped handler. A
/// client with neither a default handler nor explicit credentials throws on a call (a programming
/// error — the caller must configure local credentials or pass them explicitly).
/// </remarks>
public sealed class LocalModerationClient : ILocalModerationClient
{
    private readonly LocalAuthHandler? _localAuth;

    /// <summary>
    /// Initializes a new <see cref="LocalModerationClient"/>.
    /// </summary>
    /// <param name="localAuth">
    /// Optional default <see cref="LocalAuthHandler"/> (the configured
    /// <see cref="ActivityPubClientOptions.LocalCredentials"/>, wrapping the instance's transport). When
    /// present the no-credential overloads use it; when <see langword="null"/> only the
    /// explicit-<see cref="ProxyCredentials"/> overloads work (each builds a request-scoped handler over
    /// a fresh transport). The client does not dispose a provided handler (it is shared with the
    /// factory's transport).
    /// </param>
    public LocalModerationClient(LocalAuthHandler? localAuth)
    {
        _localAuth = localAuth;
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> MuteAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
        => LocalDecisionAsync(actorId, targetId, path: "mutes", remove: false, removeQuery: "unmute", credentials: null, ct);

    /// <inheritdoc/>
    public Task<DeliveryResult> MuteAsync(Iri actorId, Iri targetId, ProxyCredentials credentials, CancellationToken ct = default)
        => LocalDecisionAsync(actorId, targetId, path: "mutes", remove: false, removeQuery: "unmute", credentials, ct);

    /// <inheritdoc/>
    public Task<DeliveryResult> UnmuteAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
        => LocalDecisionAsync(actorId, targetId, path: "mutes", remove: true, removeQuery: "unmute", credentials: null, ct);

    /// <inheritdoc/>
    public Task<DeliveryResult> UnmuteAsync(Iri actorId, Iri targetId, ProxyCredentials credentials, CancellationToken ct = default)
        => LocalDecisionAsync(actorId, targetId, path: "mutes", remove: true, removeQuery: "unmute", credentials, ct);

    /// <inheritdoc/>
    public Task<DeliveryResult> SubscribeRelayAsync(Iri actorId, Iri relayId, CancellationToken ct = default)
        => LocalDecisionAsync(actorId, relayId, path: "relays", remove: false, removeQuery: "unsubscribe", credentials: null, ct);

    /// <inheritdoc/>
    public Task<DeliveryResult> SubscribeRelayAsync(Iri actorId, Iri relayId, ProxyCredentials credentials, CancellationToken ct = default)
        => LocalDecisionAsync(actorId, relayId, path: "relays", remove: false, removeQuery: "unsubscribe", credentials, ct);

    /// <inheritdoc/>
    public Task<DeliveryResult> UnsubscribeRelayAsync(Iri actorId, Iri relayId, CancellationToken ct = default)
        => LocalDecisionAsync(actorId, relayId, path: "relays", remove: true, removeQuery: "unsubscribe", credentials: null, ct);

    /// <inheritdoc/>
    public Task<DeliveryResult> UnsubscribeRelayAsync(Iri actorId, Iri relayId, ProxyCredentials credentials, CancellationToken ct = default)
        => LocalDecisionAsync(actorId, relayId, path: "relays", remove: true, removeQuery: "unsubscribe", credentials, ct);

    /// <summary>
    /// Performs a local, Basic-authenticated moderation decision (a mute or a relay subscription).
    /// </summary>
    /// <param name="actorId">The IRI of the acting (local) actor.</param>
    /// <param name="targetId">The IRI of the decision's target (the muted actor, or the relay).</param>
    /// <param name="path">The route segment under the actor IRI (<c>mutes</c> or <c>relays</c>).</param>
    /// <param name="remove">Whether to remove the edge (un-mute / un-subscribe) rather than record it.</param>
    /// <param name="removeQuery">The query flag that signals a removal (<c>unmute</c> or <c>unsubscribe</c>).</param>
    /// <param name="credentials">Explicit Basic-auth credentials, or <see langword="null"/> to use the
    /// client's default local credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    private async Task<DeliveryResult> LocalDecisionAsync(
        Iri actorId,
        Iri targetId,
        string path,
        bool remove,
        string removeQuery,
        ProxyCredentials? credentials,
        CancellationToken ct)
    {
        // A local decision (a mute, F-07, or a relay subscription, F-06) is Iris-specific (no
        // ActivityStreams type for either) and is a local decision, so it is not a signed inbox
        // delivery: it is a Basic-authenticated POST to the acting actor's own instance. The local-auth
        // handler is either the client's default (the configured LocalCredentials) or a one built for the
        // request (explicit credentials). A missing handler/credentials is a programming error (the
        // caller must configure LocalCredentials or pass credentials explicitly).
        //
        // When the client's default local-auth handler is used it is SHARED across calls (the transport
        // is the factory's, and a test may route it through a deferred handler that is created once), so
        // the HttpClient must NOT dispose it. When a handler is built for the request (explicit
        // credentials over a fresh transport) it is request-scoped and IS disposed.
        var configured = _localAuth;
        LocalAuthHandler handler;
        bool ownsHandler;
        if (credentials is not null && configured is null)
        {
            // Explicit credentials with no configured default: build a request-scoped handler over a
            // fresh transport (owned and disposed with the request).
            handler = new LocalAuthHandler(credentials, new HttpClientHandler());
            ownsHandler = true;
        }
        else if (credentials is not null)
        {
            // Explicit credentials with a configured default: wrap the shared transport (not disposed —
            // it is the factory's / a deferred test handler, reused across calls).
            handler = new LocalAuthHandler(credentials, configured!);
            ownsHandler = false;
        }
        else if (configured is not null)
        {
            handler = configured;
            ownsHandler = false;
        }
        else
        {
            throw new InvalidOperationException(
                "Local moderation requires LocalCredentials (set ActivityPubClientOptions.LocalCredentials) or explicit credentials.");
        }

        // The target is an absolute IRI; the catch-all route on the server preserves it. A removal is
        // signalled by ?{removeQuery}=true (the same route records the edge otherwise). The request has
        // no body (the target is in the path), so it is sent unsigned through the local-auth handler (not
        // the signed pipeline, which would throw — a local decision is not a federated activity).
        var removeQueryString = remove ? $"?{removeQuery}=true" : string.Empty;
        var requestUri = new Uri($"{actorId.Value.TrimEnd('/')}/{path}/{targetId.Value.TrimStart('/')}{removeQueryString}");
        using var localHttp = new HttpClient(handler, disposeHandler: ownsHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        using var response = await localHttp.SendAsync(request, ct).ConfigureAwait(false);
        var bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new DeliveryResult((int)response.StatusCode, response.IsSuccessStatusCode, bodyText);
    }
}
