using System.Collections;

namespace Iris.Core.Signing;

/// <summary>
/// An immutable snapshot of the request/response fields needed to build (or verify) an
/// ActivityPub HTTP signature. This is the HTTP-agnostic input to
/// <see cref="ISignatureSigner"/> and <see cref="ISignatureVerifier"/> so the crypto stays
/// in <c>Iris.Core</c> (no HTTP dependency).
/// </summary>
/// <remarks>
/// The ASP.NET and <see cref="System.Net.Http"/> layers map their own types onto this type at
/// the boundary. Header lookups are case-insensitive (HTTP semantics). Equality compares the
/// fields by value (headers by contents), not by reference.
/// </remarks>
public sealed class HttpRequestMetadata
{
    /// <summary>
    /// Initializes a new <see cref="HttpRequestMetadata"/>.
    /// </summary>
    /// <param name="method">The HTTP method (e.g. <c>POST</c>). Used for the <c>(request-target)</c> component.</param>
    /// <param name="pathAndQuery">The request target path and query (e.g. <c>/u/alice/inbox</c>). Used for the <c>(request-target)</c> component.</param>
    /// <param name="host">The value of the <c>Host</c> header.</param>
    /// <param name="date">The value of the <c>Date</c> header (HTTP-date).</param>
    /// <param name="contentType">The value of the <c>Content-Type</c> header, when present.</param>
    /// <param name="body">The raw request body bytes. Only the <see cref="SigningProfile.ServerToServer"/> profile signs the <c>digest</c> of this.</param>
    /// <param name="headers">The raw header values, keyed by name (case-insensitive).</param>
    public HttpRequestMetadata(
        string method,
        string pathAndQuery,
        string host,
        string date,
        string? contentType,
        byte[] body,
        IReadOnlyDictionary<string, string> headers)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        PathAndQuery = pathAndQuery ?? throw new ArgumentNullException(nameof(pathAndQuery));
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Date = date ?? throw new ArgumentNullException(nameof(date));
        ContentType = contentType;
        Body = body ?? throw new ArgumentNullException(nameof(body));
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
    }

    /// <summary>
    /// Gets the HTTP method (e.g. <c>POST</c>).
    /// </summary>
    public string Method { get; }

    /// <summary>
    /// Gets the request target path and query (e.g. <c>/u/alice/inbox</c>).
    /// </summary>
    public string PathAndQuery { get; }

    /// <summary>
    /// Gets the value of the <c>Host</c> header.
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Gets the value of the <c>Date</c> header (HTTP-date).
    /// </summary>
    public string Date { get; }

    /// <summary>
    /// Gets the value of the <c>Content-Type</c> header, or null when absent.
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// Gets the raw request body bytes.
    /// </summary>
    public byte[] Body { get; }

    /// <summary>
    /// Gets the raw header values, keyed by name (case-insensitive).
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// Gets the value of a header, case-insensitively, or null when absent.
    /// </summary>
    /// <param name="name">The header name.</param>
    public string? GetHeader(string name)
    {
        foreach (var pair in Headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a copy of this metadata with the given fields replaced (null leaves a field unchanged).
    /// </summary>
    /// <param name="method">New method, or null to keep.</param>
    /// <param name="pathAndQuery">New path and query, or null to keep.</param>
    /// <param name="host">New host, or null to keep.</param>
    /// <param name="date">New date, or null to keep.</param>
    /// <param name="contentType">New content type (pass null to clear), or null to keep.</param>
    /// <param name="body">New body, or null to keep.</param>
    /// <param name="headers">New headers, or null to keep.</param>
    public HttpRequestMetadata With(
        string? method = null,
        string? pathAndQuery = null,
        string? host = null,
        string? date = null,
        string? contentType = null,
        byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
        => new(
            method ?? Method,
            pathAndQuery ?? PathAndQuery,
            host ?? Host,
            date ?? Date,
            contentType ?? ContentType,
            body ?? Body,
            headers ?? Headers);

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj is HttpRequestMetadata other
           && Method == other.Method
           && PathAndQuery == other.PathAndQuery
           && Host == other.Host
           && Date == other.Date
           && ContentType == other.ContentType
           && Body.AsSpan().SequenceEqual(other.Body.AsSpan())
           && HeadersEqual(Headers, other.Headers);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(Method, PathAndQuery, Host, Date, ContentType);

    private static bool HeadersEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out var value) || value != pair.Value)
            {
                return false;
            }
        }

        return true;
    }
}
