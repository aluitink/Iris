using System.Net.Http.Headers;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client;

/// <summary>
/// The primary ActivityPub client: performs signed HTTP requests against remote ActivityPub
/// servers and operates on <c>KristofferStrube.ActivityStreams</c> types.
/// </summary>
/// <remarks>
/// Requests are signed by the <see cref="SigningHandler"/> (wired into the
/// <see cref="HttpMessageHandler"/> pipeline) using the <see cref="SigningProfile.ClientToServer"/>
/// profile for bodyless GETs and the <see cref="SigningProfile.ServerToServer"/> profile for
/// body-carrying POSTs. Responses are deserialized into <see cref="IObjectOrLink"/> and then
/// pattern-matched — never into a concrete type (see the coding-style rules for 3rd-party
/// ActivityStreams types).
/// </remarks>
public sealed class ActivityPubClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SigningHandler? _signingHandler;
    private readonly bool _ownsHandler;

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClient"/>.
    /// </summary>
    /// <param name="http">The HTTP client (its handler pipeline should include a
    /// <see cref="SigningHandler"/> for signed requests).</param>
    /// <param name="signingHandler">The signing handler, used to set the actor per request.
    /// May be null when the client is used unsigned (e.g. for public reads).</param>
    public ActivityPubClient(HttpClient http, SigningHandler? signingHandler = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _signingHandler = signingHandler;
        _ownsHandler = false;
    }

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClient"/> that owns its <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="handler">The handler pipeline (typically a <see cref="SigningHandler"/> over a
    /// <see cref="HttpClientHandler"/>). The signing handler's <see cref="SigningHandler.ActorId"/>
    /// must be set before sending signed requests.</param>
    public ActivityPubClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _http = new HttpClient(handler, disposeHandler: true);
        _ownsHandler = true;
    }

    /// <summary>
    /// Disposes the owned <see cref="HttpClient"/> (and its handler pipeline).
    /// </summary>
    public void Dispose()
    {
        if (_ownsHandler)
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// Fetches an actor (or object) by IRI, signed with the <see cref="SigningProfile.ClientToServer"/>
    /// profile.
    /// </summary>
    /// <param name="actorId">The IRI of the actor/object to fetch.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The deserialized object, or null if the request failed or the body was empty.</returns>
    public async Task<IObject?> GetObjectAsync(Iri actorId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, actorId.Value);
        return await GetObjectAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an ActivityPub activity to the given inbox IRI, signed with the
    /// <see cref="SigningProfile.ServerToServer"/> profile (covers <c>digest</c> + <c>content-type</c>).
    /// </summary>
    /// <param name="inboxId">The inbox IRI to deliver to.</param>
    /// <param name="activity">The activity to send (must be an <see cref="Activity"/>; serialized
    /// with <see cref="ActivityJson"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <exception cref="ArgumentException">When <paramref name="activity"/> is not an <see cref="Activity"/>.</exception>
    public async Task<int> DeliverAsync(Iri inboxId, IObject activity, CancellationToken ct = default)
    {
        if (activity is not Activity)
        {
            throw new ArgumentException("The object must be an Activity to deliver.", nameof(activity));
        }

        var json = ActivityJson.Serialize(activity);
        var body = System.Text.Encoding.UTF8.GetBytes(json);

        using var request = new HttpRequestMessage(HttpMethod.Post, inboxId.Value)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    private async Task<IObject?> GetObjectAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var objectOrLink = ActivityJson.Deserialize<IObjectOrLink>(json);
        return objectOrLink is IObject obj ? obj : null;
    }
}
