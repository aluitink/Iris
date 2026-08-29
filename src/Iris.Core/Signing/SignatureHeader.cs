using System.Text;

namespace Iris.Core.Signing;

/// <summary>
/// The parsed form of an HTTP <c>Signature</c> header (draft-cavage-http-signatures-03 as used
/// by ActivityPub). Provides round-trip <see cref="Format"/>/static <see cref="TryParse"/> so
/// the signer and verifier share one parser and one serializer.
/// </summary>
/// <remarks>
/// Example: <c>keyId="https://example.com/actors/alice#key-1", algorithm="rsa-sha256",
/// headers="(request-target) host date", signature="base64..."</c>.
/// </remarks>
public sealed record SignatureHeader(
    string KeyId,
    string Algorithm,
    string Headers,
    string Signature)
{
    /// <summary>
    /// Parses a <c>Signature</c> header value into a <see cref="SignatureHeader"/>.
    /// </summary>
    /// <param name="header">The raw header value.</param>
    /// <param name="parsed">The parsed header, when successful.</param>
    /// <returns><see langword="true"/> when the header is well-formed and contains all required fields; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? header, out SignatureHeader? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["keyId"] = null!,
            ["algorithm"] = null!,
            ["headers"] = null!,
            ["signature"] = null!,
        };

        foreach (var part in SplitTopLevel(header, ','))
        {
            var trimmed = part.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }

            var name = trimmed[..eq].Trim();
            var value = Unquote(trimmed[(eq + 1)..].Trim());
            if (value is null)
            {
                return false;
            }

            fields[name] = value;
        }

        foreach (var required in fields.Keys)
        {
            if (string.IsNullOrWhiteSpace(fields[required]))
            {
                return false;
            }
        }

        parsed = new SignatureHeader(fields["keyId"], fields["algorithm"], fields["headers"], fields["signature"]);
        return true;
    }

    /// <summary>
    /// Formats this header into the wire value (quoted, comma-separated).
    /// </summary>
    /// <returns>The <c>Signature</c> header value.</returns>
    public string Format()
    {
        var builder = new StringBuilder();
        builder.Append("keyId=\"").Append(KeyId).Append('"');
        builder.Append(", algorithm=\"").Append(Algorithm).Append('"');
        builder.Append(", headers=\"").Append(Headers).Append('"');
        builder.Append(", signature=\"").Append(Signature).Append('"');
        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => Format();

    /// <summary>
    /// Splits a string on a delimiter at the top level, ignoring delimiters inside double quotes.
    /// </summary>
    private static IEnumerable<string> SplitTopLevel(string input, char delimiter)
    {
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == delimiter && !inQuotes)
            {
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        yield return current.ToString();
    }

    /// <summary>
    /// Removes surrounding double quotes from a header parameter value, or returns null when the
    /// value is not a properly quoted string.
    /// </summary>
    private static string? Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        return null;
    }
}
