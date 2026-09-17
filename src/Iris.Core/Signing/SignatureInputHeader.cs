using System.Text;

namespace Iris.Core.Signing;

/// <summary>
/// A covered component of an RFC 9421 signature: a name plus its parameters (serialized form).
/// </summary>
/// <param name="Name">The component name, e.g. <c>date</c>, <c>content-type</c>, <c>@method</c>,
/// <c>@authority</c>, <c>@path</c>. For <c>@query-param</c> the <c>name</c> parameter carries the
/// query parameter name.</param>
/// <param name="Parameters">The serialized parameters exactly as they appear on the wire (including
/// any leading <c>;</c>, e.g. <c>;name="Pet"</c>), or empty when there are none. Kept verbatim so the
/// component identifier re-serializes byte-identically into the signature base (RFC 9421 §2.5).</param>
public readonly record struct SignatureInputComponent(string Name, string Parameters);

/// <summary>
/// A single member of an RFC 9421 <c>Signature-Input</c> header: the covered components and the
/// signature parameters for one signature (identified by its label).
/// </summary>
/// <param name="Label">The signature label (e.g. <c>sig1</c>, <c>sig-b26</c>).</param>
/// <param name="Components">The ordered covered components (the inner-list items).</param>
/// <param name="RawParameters">The serialized signature parameters, including the leading <c>;</c>
/// (e.g. <c>;created=1618884473;keyid="test-key-ed25519"</c>), or empty. Extracted from
/// <see cref="MemberValue"/>.</param>
/// <param name="MemberValue">The raw member value — the text after <c>label=</c> and before the next
/// member (e.g. <c>("date" "@method");created=...;keyid="..."</c>). Used verbatim as the
/// <c>@signature-params</c> component value in the signature base (RFC 9421 §2.3/§2.5).</param>
public sealed record SignatureInputMember(
    string Label,
    IReadOnlyList<SignatureInputComponent> Components,
    string RawParameters,
    string MemberValue)
{
    /// <summary>
    /// The <c>keyid</c> parameter value, or null when absent.
    /// </summary>
    public string? KeyId => SignatureInputHeader.ExtractQuotedParameter(RawParameters, "keyid");

    /// <summary>
    /// The <c>created</c> parameter value (Unix seconds), or null when absent.
    /// </summary>
    public long? Created => SignatureInputHeader.ExtractIntegerParameter(RawParameters, "created");
}

/// <summary>
/// Parsed form of an RFC 9421 <c>Signature-Input</c> header (a Dictionary Structured Field). Each
/// member maps a signature label to its covered components + signature parameters. This is the
/// counterpart to the legacy inline <see cref="SignatureHeader"/>: in the new format the
/// parameters (keyid, created, covered components) live here, and the <c>Signature</c> header
/// carries only the label + base64 signature.
/// </summary>
/// <param name="Members">The parsed members, in wire order.</param>
public sealed record SignatureInputHeader(IReadOnlyList<SignatureInputMember> Members)
{
    /// <summary>
    /// Parses a <c>Signature-Input</c> header value.
    /// </summary>
    /// <param name="header">The raw header value, e.g.
    /// <c>sig1=("date" "@method" "@path");created=123;keyid="https://x#key-1"</c>.</param>
    /// <param name="parsed">The parsed header, when successful.</param>
    /// <returns><see langword="true"/> when the header is well-formed; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? header, out SignatureInputHeader? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var members = new List<SignatureInputMember>();
        foreach (var memberChunk in SplitTopLevel(header, ','))
        {
            var trimmed = memberChunk.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }

            // The label is an sf-raw token (unquoted), e.g. sig1, sig-b26.
            var label = trimmed[..eq].Trim();
            var memberValue = trimmed[(eq + 1)..].TrimStart();
            if (string.IsNullOrEmpty(label) || memberValue.Length == 0)
            {
                return false;
            }

            var (components, rawParameters, ok) = ParseMemberValue(memberValue);
            if (!ok)
            {
                return false;
            }

            members.Add(new SignatureInputMember(label, components, rawParameters, memberValue));
        }

        if (members.Count == 0)
        {
            return false;
        }

        parsed = new SignatureInputHeader(members);
        return true;
    }

    /// <summary>
    /// Finds the member with the given label.
    /// </summary>
    /// <param name="label">The signature label.</param>
    /// <returns>The member, or null when no member has that label.</returns>
    public SignatureInputMember? GetMember(string label)
    {
        foreach (var member in Members)
        {
            if (member.Label == label)
            {
                return member;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the member value (the inner list of covered components plus the signature parameters).
    /// The form is <c>( &lt;item&gt; *(SP &lt;item&gt;) ) &lt;params&gt; </c> where each item is an
    /// sf-string optionally followed by parameters, and &lt;params&gt; is the signature-parameter
    /// serialization (leading <c>;</c>, e.g. <c>;created=...;keyid="..."</c>).
    /// </summary>
    private static (IReadOnlyList<SignatureInputComponent> Components, string RawParameters, bool Ok)
        ParseMemberValue(string memberValue)
    {
        var components = new List<SignatureInputComponent>();

        var i = 0;
        if (i >= memberValue.Length || memberValue[i] != '(')
        {
            return ([], "", false);
        }

        i++; // consume '('
        while (i < memberValue.Length)
        {
            // Skip whitespace between items.
            while (i < memberValue.Length && memberValue[i] is ' ' or '\t')
            {
                i++;
            }

            if (i >= memberValue.Length)
            {
                return ([], "", false);
            }

            if (memberValue[i] == ')')
            {
                i++; // consume ')'
                break;
            }

            if (memberValue[i] != '"')
            {
                return ([], "", false);
            }

            // An item: an sf-string (the component name) optionally followed by parameters.
            i++; // consume opening quote
            var nameStart = i;
            while (i < memberValue.Length && memberValue[i] != '"')
            {
                i++;
            }

            if (i >= memberValue.Length)
            {
                return ([], "", false);
            }

            var name = memberValue[nameStart..i];
            i++; // consume closing quote

            // Capture the parameters that follow (up to the closing ')' or the next item's quote).
            var paramStart = i;
            while (i < memberValue.Length && memberValue[i] != ')' && memberValue[i] != '"')
            {
                i++;
            }

            components.Add(new SignatureInputComponent(name, memberValue[paramStart..i]));
        }

        // The remainder after ')' is the signature-parameters portion (leading ';' included, or empty).
        var rawParameters = i < memberValue.Length ? memberValue[i..] : "";
        return (components, rawParameters, true);
    }

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
    /// Removes surrounding double quotes from an sf-string, or returns null when not quoted.
    /// </summary>
    private static string? Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        return null;
    }

    /// <summary>
    /// Extracts a quoted string parameter (e.g. <c>keyid</c>) from the serialized parameters.
    /// </summary>
    public static string? ExtractQuotedParameter(string parameters, string paramName)
    {
        var match = FindParameter(parameters, paramName);
        if (match is null)
        {
            return null;
        }

        var matchInt = match.Value;
        var rest = parameters[matchInt..];
        var eq = rest.IndexOf('=');
        if (eq < 0)
        {
            return null;
        }

        var value = rest[(eq + 1)..].TrimStart();
        return Unquote(value);
    }

    /// <summary>
    /// Extracts an integer parameter (e.g. <c>created</c>) from the serialized parameters.
    /// </summary>
    public static long? ExtractIntegerParameter(string parameters, string paramName)
    {
        var match = FindParameter(parameters, paramName);
        if (match is null)
        {
            return null;
        }

        var matchInt = match.Value;
        var rest = parameters[matchInt..];
        var eq = rest.IndexOf('=');
        if (eq < 0)
        {
            return null;
        }

        var value = rest[(eq + 1)..].TrimStart();
        // The integer runs until the next ';' or end of string.
        var end = value.IndexOf(';');
        if (end >= 0)
        {
            value = value[..end];
        }

        return long.TryParse(value.Trim(), out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Finds the start index of a named parameter in the serialized parameter string, matching the
    /// parameter name as a whole token (preceded by <c>;</c> or start, followed by <c>=</c>).
    /// </summary>
    private static int? FindParameter(string parameters, string paramName)
    {
        // The parameter string starts with a leading ';' (e.g. ";created=...;keyid=\"...\"").
        // Each parameter is delimited by ';'. Walk each segment and match the name.
        var searchStart = 0;
        while (searchStart < parameters.Length)
        {
            var segmentStart = searchStart;
            // Find the next ';' (the end of this parameter segment).
            var nextSemi = parameters.IndexOf(';', searchStart);
            var segmentEnd = nextSemi < 0 ? parameters.Length : nextSemi;

            // The segment may have a leading ';' (skip it for the name match).
            var nameStart = segmentStart;
            if (nameStart < parameters.Length && parameters[nameStart] == ';')
            {
                nameStart++;
            }

            if (nameStart + paramName.Length <= segmentEnd
                && string.Equals(parameters[nameStart..(nameStart + paramName.Length)], paramName, StringComparison.Ordinal)
                && nameStart + paramName.Length < segmentEnd
                && parameters[nameStart + paramName.Length] == '=')
            {
                return nameStart;
            }

            if (nextSemi < 0)
            {
                break;
            }

            searchStart = nextSemi; // continue from the ';'
        }

        return null;
    }
}
