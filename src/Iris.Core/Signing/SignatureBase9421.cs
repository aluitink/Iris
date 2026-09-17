using System.Text;

namespace Iris.Core.Signing;

/// <summary>
/// Builds the RFC 9421 (HTTP Message Signatures, draft-cavage-21+) signature base — the bytes that
/// are hashed and signed. This is the "new" format that modern fediverse peers (Mastodon 4.5+,
/// Pleroma 2024+, Misskey, GoToSocial) use, in which the covered components and signature
/// parameters live in a <c>Signature-Input</c> header rather than inline in the <c>Signature</c>
/// header (the legacy draft-cavage-03 form that <see cref="SignatureHeader"/> parses).
/// </summary>
/// <remarks>
/// The signature base is an ASCII string of one line per covered component, each line
/// <c>"&lt;name&gt;"&lt;params&gt;: &lt;value&gt;</c> (LF-terminated), followed by a final
/// <c>"@signature-params": &lt;inner-list&gt;</c> line whose value is the byte-identical
/// serialization of the <c>Signature-Input</c> header's member value. See RFC 9421 §2.5.
/// </remarks>
public static class SignatureBase9421
{
    /// <summary>
    /// The <c>Content-Digest</c> header name (RFC 9530), used as a covered component for body
    /// integrity. Distinct from the legacy <c>digest</c> pseudo-component in
    /// <see cref="Signatures.DigestHeaderName"/>.
    /// </summary>
    public const string ContentDigestHeaderName = "content-digest";

    /// <summary>
    /// Builds the signature base for the given request and signature parameters.
    /// </summary>
    /// <param name="metadata">The request fields (method, path, host, raw headers, body).</param>
    /// <param name="member">The parsed <c>Signature-Input</c> member (covered components + parameters).</param>
    /// <param name="memberValue">The raw <c>Signature-Input</c> member value — the text after
    /// <c>label=</c> and before the next member. Used verbatim as the <c>@signature-params</c>
    /// component value (the RFC requires it to be byte-identical to what the signer serialized).</param>
    /// <returns>The signature base bytes (ASCII/UTF-8).</returns>
    /// <exception cref="ArgumentException">
    /// A covered component cannot be derived (unknown derived component, missing header, or
    /// an unsupported component parameter). The verifier treats this as an invalid signature.
    /// </exception>
    public static byte[] Build(
        HttpRequestMetadata metadata,
        SignatureInputMember member,
        string memberValue)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(memberValue);

        var builder = new StringBuilder();
        foreach (var component in member.Components)
        {
            var value = DeriveComponentValue(metadata, component);
            builder
                .Append('"')
                .Append(component.Name)
                .Append('"')
                .Append(component.Parameters.TrimEnd())
                .Append(": ")
                .Append(value)
                .Append('\n');
        }

        builder
            .Append("\"@signature-params\": ")
            .Append(memberValue)
            .Append('\n');

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Derives the canonical component value for a covered component identifier, per RFC 9421 §2.2.
    /// </summary>
    /// <param name="metadata">The request fields.</param>
    /// <param name="component">The covered component (name + optional parameters).</param>
    /// <returns>The component value (already canonicalized for inclusion in the signature base).</returns>
    /// <exception cref="ArgumentException">The component cannot be derived.</exception>
    private static string DeriveComponentValue(HttpRequestMetadata metadata, SignatureInputComponent component)
    {
        var name = component.Name;
        if (name.StartsWith('@'))
        {
            return name switch
            {
                "@method" => metadata.Method,
                "@path" => NormalizePath(metadata.PathAndQuery),
                "@query" => NormalizeQuery(metadata.PathAndQuery),
                "@authority" => NormalizeAuthority(metadata.Host),
                "@target-uri" => BuildTargetUri(metadata),
                "@scheme" => "https",
                "@request-target" => metadata.PathAndQuery,
                "@signature-params" => throw new ArgumentException(
                    "The @signature-params component is not a covered component; it is the final line of the signature base.",
                    nameof(component)),
                _ => throw new ArgumentException($"Derived component '{name}' is not supported.", nameof(component)),
            };
        }

        // A plain header component: the value is the raw header value (the field-content form, which
        // is the canonical form for all field types a fediverse signer covers: date, content-type,
        // content-digest, user-agent, ...). Header lookup is case-insensitive.
        var value = metadata.GetHeader(name);
        if (value is null)
        {
            throw new ArgumentException($"Header '{name}' is not present on the request.", nameof(component));
        }

        return value;
    }

    /// <summary>
    /// Normalizes the <c>@path</c> component: the absolute path with no query, an empty path
    /// normalized to <c>/</c>, percent-encoding preserved (not decoded). RFC 9421 §2.2.6.
    /// </summary>
    private static string NormalizePath(string pathAndQuery)
    {
        var path = pathAndQuery;
        var q = path.IndexOf('?');
        if (q >= 0)
        {
            path = path[..q];
        }

        return path.Length == 0 ? "/" : path;
    }

    /// <summary>
    /// Normalizes the <c>@query</c> component: the entire query including the leading <c>?</c>;
    /// an empty query is the single <c>?</c> character. Percent-encoding is preserved. RFC 9421 §2.2.7.
    /// </summary>
    private static string NormalizeQuery(string pathAndQuery)
    {
        var q = pathAndQuery.IndexOf('?');
        if (q < 0)
        {
            return "?";
        }

        return pathAndQuery[q..];
    }

    /// <summary>
    /// Normalizes the <c>@authority</c> component: lowercase host, default port (80) omitted.
    /// RFC 9421 §2.2.3. A port of 443 is NOT omitted (it is not the default for the https scheme the
    /// request is served over, and omitting it would drift from the wire value a peer signed).
    /// </summary>
    private static string NormalizeAuthority(string host)
    {
        if (host.Length == 0)
        {
            return host;
        }

        var colon = host.LastIndexOf(':');
        if (colon > 0)
        {
            var hostPart = host[..colon];
            var portPart = host[(colon + 1)..];
            var normalizedHost = hostPart.ToLowerInvariant();
            if (portPart == "80")
            {
                return normalizedHost;
            }

            return normalizedHost + ":" + portPart;
        }

        return host.ToLowerInvariant();
    }

    /// <summary>
    /// Builds the <c>@target-uri</c> component: the absolute target URI (<c>https://host/path?query</c>).
    /// RFC 9421 §2.2.2. The scheme is inferred as <c>https</c> (fediverse peers always sign over the
    /// origin-form absolute URI with the https scheme).
    /// </summary>
    private static string BuildTargetUri(HttpRequestMetadata metadata)
        => $"https://{NormalizeAuthority(metadata.Host)}{metadata.PathAndQuery}";
}
