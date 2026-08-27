using System.Net.Http.Headers;
using Iris.Core;

namespace Iris.Client;

/// <summary>
/// A <see cref="DelegatingHandler"/> that performs ActivityPub content negotiation: it advertises
/// that Iris accepts <c>application/activity+json</c> and <c>application/ld+json</c> on reads, and
/// ensures outgoing body requests carry <c>application/activity+json</c>.
/// </summary>
/// <remarks>
/// Per Resolved Decision #4, Iris <em>produces</em> <c>application/activity+json</c> and
/// <em>accepts</em> both <c>application/activity+json</c> and <c>application/ld+json</c> on inbound.
/// This handler is the client-side expression of that policy:
/// <list type="bullet">
/// <item>Bodyless requests (GETs) get an <c>Accept</c> header listing both media types (in
/// preference order), so a server may respond with either.</item>
/// <item>Requests with a body (POSTs) get <c>application/activity+json</c> as the
/// <c>Content-Type</c> when none is set, so the <see cref="Iris.Client.SigningHandler"/> signs the
/// correct <c>content-type</c> component.</item>
/// </list>
/// It is a no-op for responses (the <see cref="ActivityPubClient"/> deserializes both media types
/// via <see cref="Iris.Core.ActivityJson"/> regardless of which one the server chose).
/// </remarks>
public sealed class JsonLdHandler : DelegatingHandler
{
    /// <summary>
    /// The <c>Accept</c> value advertised on bodyless requests: both media types, activity+json first.
    /// </summary>
    public const string AcceptHeaderValue =
        ActivityJson.ActivityJsonContentType + ", " + ActivityJson.JsonLdContentType;

    /// <summary>
    /// Initializes a new <see cref="JsonLdHandler"/>.
    /// </summary>
    public JsonLdHandler()
    {
    }

    /// <summary>
    /// Initializes a new <see cref="JsonLdHandler"/> with an explicit inner handler.
    /// </summary>
    /// <param name="innerHandler">The inner handler to forward to.</param>
    public JsonLdHandler(HttpMessageHandler innerHandler)
    {
        InnerHandler = innerHandler ?? throw new ArgumentNullException(nameof(innerHandler));
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Content is null)
        {
            // Bodyless request: advertise that we accept both media types.
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ActivityJson.ActivityJsonContentType));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ActivityJson.JsonLdContentType));
        }
        else
        {
            // Body request: ensure the content type is the Iris production default when unset.
            if (request.Content.Headers.ContentType?.MediaType is null)
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            }
        }

        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
