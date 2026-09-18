using System.Text;

namespace Iris.Core.Rendering;

/// <summary>
/// A small, dependency-free HTML sanitizer for rendering untrusted HTML (an ActivityPub actor's
/// <c>summary</c>/bio) safely as Blazor markup. It is deliberately conservative: only a fixed
/// allow-list of formatting/structure tags survives, and
/// only a fixed allow-list of attributes (with URL-scheme restrictions) is kept. Everything else —
/// scripts, iframes, event handlers, style attributes, and dangerous link targets — is stripped, so the
/// output is safe to emit verbatim into the DOM from any consuming project (the WASM app, the sample
/// client, or a server-rendered view).
/// </summary>
/// <remarks>
/// <strong>Security model:</strong> the input is treated as hostile (it arrives over the network from
/// other ActivityPub instances). The sanitizer works by <em>allow-listing</em> rather than
/// block-listing: a tag or attribute that is not explicitly permitted is dropped. For a dropped
/// element the surrounding text is preserved (so a disallowed tag renders as its inner text, not as an
/// executable element), <em>except</em> for the script/style/textarea families whose <em>content</em> is
/// code (not text) and is dropped wholesale. Link targets are restricted to the
/// <c>http</c>/<c>https</c>/<c>mailto</c> schemes — a <c>javascript:</c> (or any other) scheme is
/// rendered as a plain link with no target, never as a live handler.
/// </remarks>
public static class HtmlSanitizer
{
    /// <summary>
    /// Tags that survive sanitization, mapped to whether they are self-closing in the output. A tag not
    /// in this set is dropped (its inner text kept, unless it is a content-is-code tag).
    /// </summary>
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // block structure
        "p", "div", "span", "br", "hr", "blockquote", "pre", "code",
        // emphasis / inline formatting
        "b", "strong", "i", "em", "u", "s", "strike", "small", "sub", "sup",
        // links
        "a",
        // lists
        "ul", "ol", "li",
        // headings
        "h1", "h2", "h3", "h4", "h5", "h6",
    };

    /// <summary>
    /// Tags whose <em>content</em> is code (not displayable text). When one of these is encountered the
    /// tag <em>and</em> everything up to its matching close tag is removed entirely.
    /// </summary>
    private static readonly HashSet<string> ContentIsCodeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title", "noscript", "template", "iframe", "object", "embed",
        "form", "input", "button", "select", "option", "meta", "link", "base", "svg", "math",
    };

    /// <summary>
    /// Attributes allowed on any permitted tag. Each is validated further by
    /// <see cref="SanitizeAttribute"/> (e.g. <c>href</c> is scheme-checked).
    /// </summary>
    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "title", "rel", "target", "class", "id",
    };

    /// <summary>
    /// Sanitizes an HTML fragment, returning a string safe to emit verbatim as Blazor markup.
    /// </summary>
    /// <param name="html">The untrusted HTML fragment. May be null or whitespace.</param>
    /// <returns>
    /// The sanitized HTML, or an empty string when <paramref name="html"/> is null or blank.
    /// </returns>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(html.Length);
        var i = 0;
        while (i < html.Length)
        {
            var c = html[i];
            if (c != '<')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // A '<' starts either a comment, a doctype, or a tag. Find the matching '>'.
            var gt = html.IndexOf('>', i);
            if (gt < 0)
            {
                // No closing '>' — treat the rest as literal text (escape it so it cannot form a tag).
                sb.Append(Escape(html[i..]));
                break;
            }

            var raw = html[i..(gt + 1)]; // includes the leading '<' and trailing '>'
            var inner = raw[1..^1];       // the tag body between '<' and '>'

            if (inner.StartsWith("!", StringComparison.Ordinal))
            {
                // Comment (<!-- … -->) or doctype — drop the whole thing.
                i = gt + 1;
                continue;
            }

            var isClosing = inner.Length > 0 && inner[0] == '/';
            var name = ExtractTagName(inner);
            if (name.Length == 0)
            {
                // Malformed (e.g. a lone '<' followed by junk) — escape and continue.
                sb.Append('<');
                i = gt + 1;
                continue;
            }

            // A content-is-code tag: drop the tag AND its content up to the matching close tag.
            if (!isClosing && ContentIsCodeTags.Contains(name))
            {
                i = SkipTagAndContent(html, name, gt);
                continue;
            }

            // A disallowed tag: drop the tag but keep its inner text. For a closing tag of a disallowed
            // (but non-code) element there is nothing to emit.
            if (!AllowedTags.Contains(name))
            {
                i = gt + 1;
                continue;
            }

            // An allowed tag: re-emit it, keeping only the safe attributes.
            if (isClosing)
            {
                sb.Append("</").Append(name).Append('>');
                i = gt + 1;
                continue;
            }

            var (attrs, selfClosing) = ExtractSafeAttributes(inner, name);
            var open = new StringBuilder();
            open.Append('<').Append(name);
            if (attrs.Length > 0)
            {
                open.Append(' ').Append(attrs);
            }

            if (selfClosing || IsVoidElement(name))
            {
                open.Append(" />");
            }
            else
            {
                open.Append('>');
            }

            sb.Append(open);
            i = gt + 1;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reports whether a tag is an HTML void element (no closing tag, always self-closing in the output).
    /// </summary>
    private static bool IsVoidElement(string name) => name is "br" or "hr" or "img" or "input";

    /// <summary>
    /// Reads the tag name (the first token) from a tag body. Returns an empty string when the body has no
    /// valid name (e.g. it begins with a space or a non-letter).
    /// </summary>
    private static string ExtractTagName(string tagBody)
    {
        var body = tagBody.TrimStart();
        if (body.Length > 0 && body[0] == '/')
        {
            body = body[1..].TrimStart();
        }

        var end = 0;
        while (end < body.Length && (char.IsLetterOrDigit(body[end]) || body[end] == ':'))
        {
            end++;
        }

        return body[..end];
    }

    /// <summary>
    /// Skips past a content-is-code tag and its body, returning the index just after the matching close
    /// tag (or the end of the string when no close tag is present).
    /// </summary>
    private static int SkipTagAndContent(string html, string name, int openGtIndex)
    {
        var closeTag = $"</{name}";
        var searchFrom = openGtIndex + 1;
        var lower = html.ToLowerInvariant();
        var idx = lower.IndexOf(closeTag, searchFrom, StringComparison.Ordinal);
        if (idx < 0)
        {
            return html.Length; // no close tag — drop to the end (the content is code, never safe)
        }

        var gt = html.IndexOf('>', idx);
        return gt < 0 ? html.Length : gt + 1;
    }

    /// <summary>
    /// Parses the attributes out of a tag body, keeping only the allow-listed ones (with value
    /// validation), and reports whether the source tag was self-closing.
    /// </summary>
    private static (string Attributes, bool SelfClosing) ExtractSafeAttributes(string tagBody, string name)
    {
        var selfClosing = tagBody.TrimEnd().EndsWith("/", StringComparison.Ordinal);
        var result = new StringBuilder();

        // A naive but safe attribute scan: split the body on whitespace, but reassemble quoted values.
        // Because attribute values may contain spaces (inside quotes), we walk token by token.
        var tokens = TokenizeAttributes(tagBody);
        foreach (var (attrName, attrValue) in tokens)
        {
            if (!AllowedAttributes.Contains(attrName))
            {
                continue;
            }

            var sanitized = SanitizeAttribute(name, attrName, attrValue);
            if (sanitized is null)
            {
                continue; // value failed validation (e.g. a bad URL scheme) — drop the attribute
            }

            if (result.Length > 0)
            {
                result.Append(' ');
            }

            result.Append(attrName);
            if (sanitized.Length > 0)
            {
                result.Append("=\"").Append(EscapeAttribute(sanitized)).Append('"');
            }
        }

        return (result.ToString(), selfClosing);
    }

    /// <summary>
    /// Validates an attribute value for the given tag/attribute, returning the value to emit, or null when
    /// the value must be dropped. Currently only <c>href</c> is value-checked (URL scheme restriction); all
    /// other allow-listed attributes pass through (their values are HTML-escaped on emit, so they are inert).
    /// </summary>
    private static string? SanitizeAttribute(string tagName, string attrName, string value)
    {
        if (attrName is "href" || attrName is "src")
        {
            return IsAllowedUrl(value) ? value : null;
        }

        if (attrName is "rel")
        {
            // Forbid anything that could alter link semantics dangerously; keep a fixed safe value.
            return tagName == "a" ? "nofollow noopener" : value;
        }

        if (attrName is "target")
        {
            // Only an external-new-tab target is meaningful; normalize it.
            return tagName == "a" ? "_blank" : null;
        }

        return value;
    }

    /// <summary>
    /// Reports whether a URL uses an allowed scheme (http, https, mailto). Relative URLs and any other
    /// scheme (javascript:, data:, vbscript:, file:, …) are rejected.
    /// </summary>
    private static bool IsAllowedUrl(string url)
    {
        var trimmed = url.Trim().ToLowerInvariant();
        // Strip control characters and whitespace that browsers ignore in scheme detection
        // (e.g. "java\tscript:").
        var compact = trimmed.Replace("\t", "").Replace("\n", "").Replace("\r", "").Replace(" ", "");
        return compact.StartsWith("http://", StringComparison.Ordinal)
            || compact.StartsWith("https://", StringComparison.Ordinal)
            || compact.StartsWith("mailto:", StringComparison.Ordinal);
    }

    /// <summary>
    /// Escapes the five significant characters in an attribute value so it is inert inside a quoted
    /// attribute (a '"' would otherwise terminate the attribute).
    /// </summary>
    private static string EscapeAttribute(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    /// <summary>
    /// HTML-escapes a run of literal text (the five significant characters).
    /// </summary>
    private static string Escape(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    /// <summary>
    /// Tokenizes a tag body into (name, value) attribute pairs, handling single- and double-quoted values
    /// (which may contain spaces) and unquoted values.
    /// </summary>
    private static List<(string Name, string Value)> TokenizeAttributes(string tagBody)
    {
        var tokens = new List<(string, string)>();
        var i = 0;
        var body = tagBody.TrimStart();
        if (body.Length > 0 && body[0] == '/')
        {
            body = body[1..].TrimStart();
        }

        while (i < body.Length)
        {
            while (i < body.Length && char.IsWhiteSpace(body[i]))
            {
                i++;
            }

            if (i >= body.Length)
            {
                break;
            }

            // Read the attribute name.
            var nameStart = i;
            while (i < body.Length && body[i] != '=' && !char.IsWhiteSpace(body[i]) && body[i] != '/' && body[i] != '>')
            {
                i++;
            }

            var name = body[nameStart..i].Trim();
            if (name.Length == 0)
            {
                i++; // skip a stray character
                continue;
            }

            // Skip whitespace between the name and an optional '='.
            while (i < body.Length && char.IsWhiteSpace(body[i]))
            {
                i++;
            }

            string value = string.Empty;
            if (i < body.Length && body[i] == '=')
            {
                i++; // consume '='
                while (i < body.Length && char.IsWhiteSpace(body[i]))
                {
                    i++;
                }

                if (i < body.Length && (body[i] == '"' || body[i] == '\''))
                {
                    var quote = body[i];
                    i++;
                    var valStart = i;
                    while (i < body.Length && body[i] != quote)
                    {
                        i++;
                    }

                    value = body[valStart..i];
                    if (i < body.Length)
                    {
                        i++; // consume the closing quote
                    }
                }
                else
                {
                    var valStart = i;
                    while (i < body.Length && !char.IsWhiteSpace(body[i]))
                    {
                        i++;
                    }

                    value = body[valStart..i];
                }
            }

            tokens.Add((name, value));
        }

        return tokens;
    }
}
