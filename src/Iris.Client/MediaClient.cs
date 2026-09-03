using System.Net.Http.Headers;
using System.Text.Json;
using Iris.Client.Pipeline;
using Iris.Core;

namespace Iris.Client;

/// <summary>
/// The default <see cref="IMediaClient"/>: uploads a note's media attachment (Phase 20.4 (a)) as a
/// local, Basic-authenticated multipart POST to the acting actor's own home instance.
/// </summary>
/// <remarks>
/// A media upload is not an ActivityStreams activity (the file is not an activity), so it is not a signed
/// inbox delivery: it is a Basic-authenticated multipart POST to
/// <c>{LocalRoutePrefix}/u/{handle}/media</c>, which stores the bytes and returns (201) the same-origin
/// media IRI. The client holds an optional default <see cref="LocalAuthHandler"/> (built from
/// <see cref="ActivityPubClientOptions.LocalCredentials"/> by the factory) used by the no-credential
/// overload; the explicit-<see cref="ProxyCredentials"/> overload builds a request-scoped handler. A
/// client with neither a default handler nor explicit credentials throws on a call (a programming error).
/// The handler selection (shared vs request-scoped, and whether to dispose it) mirrors
/// <see cref="LocalModerationClient"/>.
/// </remarks>
public sealed class MediaClient : IMediaClient
{
    private readonly LocalAuthHandler? _localAuth;

    /// <summary>
    /// Initializes a new <see cref="MediaClient"/>.
    /// </summary>
    /// <param name="localAuth">
    /// Optional default <see cref="LocalAuthHandler"/> (the configured
    /// <see cref="ActivityPubClientOptions.LocalCredentials"/>, wrapping the instance's transport). When
    /// present the no-credential overload uses it; when <see langword="null"/> only the
    /// explicit-<see cref="ProxyCredentials"/> overload works (it builds a request-scoped handler over a
    /// fresh transport). The client does not dispose a provided handler (it is shared with the factory's
    /// transport).
    /// </param>
    public MediaClient(LocalAuthHandler? localAuth)
    {
        _localAuth = localAuth;
    }

    /// <inheritdoc/>
    public Task<MediaUploadResult> UploadAsync(
        Iri actorId,
        byte[] content,
        string contentType,
        string fileName,
        CancellationToken ct = default)
        => UploadCoreAsync(actorId, content, contentType, fileName, credentials: null, ct);

    /// <inheritdoc/>
    public Task<MediaUploadResult> UploadAsync(
        Iri actorId,
        byte[] content,
        string contentType,
        string fileName,
        ProxyCredentials credentials,
        CancellationToken ct = default)
        => UploadCoreAsync(actorId, content, contentType, fileName, credentials, ct);

    /// <summary>
    /// Shared core for the two <c>UploadAsync</c> overloads: resolves the local-auth handler (the client's
    /// default or a request-scoped one) and performs the Basic-authenticated multipart upload.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor uploading the media.</param>
    /// <param name="content">The file's bytes.</param>
    /// <param name="contentType">The file's <c>Content-Type</c>.</param>
    /// <param name="fileName">The file's original name.</param>
    /// <param name="credentials">Explicit Basic-auth credentials, or <see langword="null"/> to use the
    /// client's default local credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    private async Task<MediaUploadResult> UploadCoreAsync(
        Iri actorId,
        byte[] content,
        string contentType,
        string fileName,
        ProxyCredentials? credentials,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        // A media upload (Phase 20.4 (a)) is Iris-specific (a file, not an ActivityStreams activity) and
        // is a local write, so it is not a signed inbox delivery: it is a Basic-authenticated multipart
        // POST to the acting actor's own instance. The local-auth handler is either the client's default
        // (the configured LocalCredentials) or one built for the request (explicit credentials). A missing
        // handler/credentials is a programming error (the caller must configure LocalCredentials or pass
        // credentials explicitly).
        //
        // When the client's default local-auth handler is used it is SHARED across calls (the transport is
        // the factory's, and a test may route it through a deferred handler that is created once), so the
        // HttpClient must NOT dispose it. When a handler is built for the request (explicit credentials
        // over a fresh transport) it is request-scoped and IS disposed.
        var configured = _localAuth;
        LocalAuthHandler handler;
        bool ownsHandler;
        if (credentials is not null && configured is null)
        {
            handler = new LocalAuthHandler(credentials, new HttpClientHandler());
            ownsHandler = true;
        }
        else if (credentials is not null)
        {
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
                "Media upload requires LocalCredentials (set ActivityPubClientOptions.LocalCredentials) or explicit credentials.");
        }

        // The request URI reuses the actor's identifying segment (/u/{handle}) under the local tree
        // (/local/v1), exactly as LocalModerationClient does (BuildLocalRequestUri's segment logic).
        var requestUri = BuildUploadUri(actorId);
        using var localHttp = new HttpClient(handler, disposeHandler: ownsHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // A multipart/form-data body carrying the file's bytes. The server reads form.Files[0] and uses
        // the part's content-type + file name. The request is sent unsigned through the local-auth handler
        // (not the signed pipeline, which would throw — a media upload is not a federated activity).
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        request.Content = form;

        using var response = await localHttp.SendAsync(request, ct).ConfigureAwait(false);
        var bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Media upload failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {bodyText}");
        }

        // The 201 body is { "id": "<mediaIri>", "type": "<contentType>", "name": "<fileName>" }.
        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        var mediaIriValue = root.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Media upload response missing the 'id' (media IRI).");
        var returnedType = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : contentType;
        var returnedName = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : fileName;
        return new MediaUploadResult(
            new Iri(mediaIriValue),
            returnedType ?? contentType,
            returnedName ?? fileName);
    }

    /// <summary>
    /// Builds the absolute upload URI: the acting actor's host, the
    /// <see cref="LocalModerationConstants.LocalRoutePrefix"/> tree, the actor's path segment
    /// (<c>u/{handle}</c>), and the <see cref="MediaConstants.UploadSegment"/> segment.
    /// </summary>
    /// <param name="actorId">The acting actor's IRI (e.g. <c>https://host/ap/v1/u/bob</c>).</param>
    /// <returns>The absolute upload URI (<c>{host}/local/v1/u/{handle}/media</c>).</returns>
    private static Uri BuildUploadUri(Iri actorId)
    {
        var actor = actorId.Value;
        // The actor's identifying segment is the path from the "u/" (person) segment onward (e.g.
        // "/u/bob"). Everything before it (scheme, host, AP route prefix) is dropped and replaced by the
        // local tree — the same logic LocalModerationClient uses for its write routes.
        var uStart = actor.IndexOf("/" + LocalModerationConstants.ActorSegment + "/", StringComparison.Ordinal);
        var cStart = actor.IndexOf("/" + LocalModerationConstants.CommunitySegment + "/", StringComparison.Ordinal);
        var actorSegmentStart = Math.Max(uStart, cStart);
        if (actorSegmentStart < 0)
        {
            throw new InvalidOperationException(
                $"Cannot derive a media-upload route for actor IRI '{actorId}' (expected a path containing /u/ or /c/).");
        }

        var actorSegment = actor[actorSegmentStart..];
        var host = new Uri(actorId.Value).GetLeftPart(UriPartial.Authority);
        return new Uri(
            $"{host}{LocalModerationConstants.LocalRoutePrefix}{actorSegment.TrimEnd('/')}/{MediaConstants.UploadSegment}");
    }
}
