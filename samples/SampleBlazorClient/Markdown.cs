using System.Text;
using System.Text.RegularExpressions;

namespace Iris.Samples.SampleBlazorClient;

/// <summary>
/// A small, dependency-free Markdown-to-HTML renderer for the object view (Phase 20.4c). It renders
/// the subset of Markdown the object view needs — headings, inline/fenced code, links, unordered and
/// ordered lists, and paragraphs — so a note's <c>content</c> that is Markdown (rather than HTML)
/// displays properly. It is deliberately not a general-purpose Markdown engine.
/// </summary>
/// <remarks>
/// <strong>Security:</strong> the input is HTML-escaped first (so any raw HTML in the content is
/// neutralized and rendered as text), and only then are the Markdown transforms applied. Link targets
/// are sanitized to the <c>http</c>/<c>https</c>/<c>mailto</c> schemes — a <c>javascript:</c> (or any
/// other) scheme is rendered as plain text, never as a live link. This makes the output safe to emit
/// as a <c>MarkupString</c> in a Blazor component.
/// </remarks>
public static class Markdown
{
    /// <summary>
    /// Converts a Markdown string to an HTML string (safe to emit as a Blazor <c>MarkupString</c>).
    /// </summary>
    /// <param name="markdown">The Markdown source. May be null or whitespace.</param>
    /// <returns>
    /// The rendered HTML (a sequence of block elements), or an empty string when
    /// <paramref name="markdown"/> is null or blank.
    /// </returns>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        // Normalize line endings, then escape ALL HTML so raw markup in the content is inert. Every
        // transform below operates on the escaped text and emits its own (safe) tags.
        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var escaped = EscapeHtml(text);

        // Fenced code blocks are pulled out first (as placeholders) so their contents are not
        // subject to the other transforms (a `#` or `-` inside a code fence is literal).
        var codeBlocks = new List<string>();
        escaped = ExtractFencedCode(escaped, codeBlocks);

        // Split into blocks on blank lines, then render each block.
        var blocks = escaped
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(block => RenderBlock(block, codeBlocks))
            .Where(html => !string.IsNullOrEmpty(html))
            .ToList();

        var html = string.Join("\n", blocks);
        // Safety net: restore any fenced-code placeholder that was not emitted as a lone block (e.g.
        // one that ended up inline). A lone-block placeholder is already restored by RenderBlock.
        return CodePlaceholderPattern().Replace(html, m =>
            codeBlocks[int.Parse(m.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    /// <summary>
    /// Renders a single Markdown block (a fenced code block, a heading, a list, or a paragraph) to
    /// HTML.
    /// </summary>
    private static string RenderBlock(string block, List<string> codeBlocks)
    {
        // A block that is exactly one fenced-code placeholder is emitted as the raw <pre><code> (no
        // <p> wrapper — <pre> is a block element and must not be nested in a <p>).
        var loneCode = CodePlaceholderPattern().Match(block);
        if (loneCode.Success && loneCode.Value == block)
        {
            return codeBlocks[int.Parse(loneCode.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture)];
        }

        // A block is a list when every non-empty line is a list item.
        var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 0 && lines.All(IsListLine))
        {
            return RenderList(block);
        }

        // A heading: a line of #s (1–6) followed by text.
        var heading = HeadingMatch().Match(block);
        if (heading.Success)
        {
            var level = heading.Groups["level"].Value.Length;
            var headingText = RenderInline(heading.Groups["text"].Value.Trim());
            return $"<h{level}>{headingText}</h{level}>";
        }

        // Otherwise a paragraph: join hard line breaks, render inline markdown.
        var paragraph = lines
            .Select(l => RenderInline(l.Trim()))
            .Aggregate(new StringBuilder(), (sb, line) =>
            {
                if (sb.Length > 0)
                {
                    sb.Append("<br/>");
                }

                return sb.Append(line);
            });
        return $"<p>{paragraph}</p>";
    }

    /// <summary>
    /// Reports whether a line is a list item (unordered <c>-</c>/<c>*</c> or ordered <c>1.</c>).
    /// </summary>
    private static bool IsListLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        return trimmed.StartsWith("- ", StringComparison.Ordinal)
            || trimmed.StartsWith("* ", StringComparison.Ordinal)
            || OrderedItemPattern().IsMatch(trimmed);
    }

    /// <summary>
    /// Renders a list block (unordered when the first item is <c>-</c>/<c>*</c>, ordered when it is
    /// <c>1.</c>) to HTML.
    /// </summary>
    private static string RenderList(string block)
    {
        var items = block
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimStart())
            .Select(RenderListItem)
            .Where(html => !string.IsNullOrEmpty(html))
            .ToList();

        if (items.Count == 0)
        {
            return string.Empty;
        }

        var ordered = !block.TrimStart().StartsWith("- ", StringComparison.Ordinal)
            && !block.TrimStart().StartsWith("* ", StringComparison.Ordinal);
        var tag = ordered ? "ol" : "ul";
        return $"<{tag}>\n{string.Join('\n', items.Select(i => $"  <li>{i}</li>"))}\n</{tag}>";
    }

    /// <summary>
    /// Renders a single list item's text (stripping the leading marker) to HTML.
    /// </summary>
    private static string RenderListItem(string line)
    {
        var trimmed = line.TrimStart();
        var text = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal)
            ? trimmed[2..]
            : OrderedItemPattern().Replace(trimmed, string.Empty);
        return RenderInline(text.Trim());
    }

    /// <summary>
    /// Renders the inline Markdown in a line (code spans, links, emphasis) to HTML. The input is
    /// already HTML-escaped.
    /// </summary>
    private static string RenderInline(string text)
    {
        // Inline code first (so its contents are not further transformed).
        var codeSpans = new List<string>();
        text = CodeSpanPattern().Replace(text, m =>
        {
            codeSpans.Add($"<code>{m.Groups["code"].Value}</code>");
            return $"\u0000{codeSpans.Count - 1}\u0000";
        });

        // Links: [text](url) — the url is sanitized (only http/https/mailto survive).
        text = LinkPattern().Replace(text, m =>
        {
            var url = m.Groups["url"].Value;
            if (!IsAllowedUrl(url))
            {
                return m.Groups["text"].Value; // render the text, drop the (unsafe) link
            }

            return $"<a href=\"{url}\" rel=\"nofollow noopener\" target=\"_blank\">{m.Groups["text"].Value}</a>";
        });

        // Emphasis: **bold** and *italic* (bold before italic so ** is not eaten by *).
        text = BoldPattern().Replace(text, "<strong>$1</strong>");
        text = ItalicPattern().Replace(text, "<em>$1</em>");

        // Restore the code-span placeholders.
        text = CodePlaceholderPattern().Replace(text, m => codeSpans[int.Parse(m.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture)]);
        return text;
    }

    /// <summary>
    /// Reports whether a link target uses an allowed scheme (http, https, mailto). Relative and
    /// scheme-less URLs are rejected (the object view renders absolute links only).
    /// </summary>
    private static bool IsAllowedUrl(string url)
    {
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// HTML-escapes the five significant characters (&amp; first, then &lt;, &gt;, &quot;, &apos;).
    /// </summary>
    private static string EscapeHtml(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                case '\'':
                    sb.Append("&#39;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Pulls fenced code blocks (``` … ```) out of the text into <paramref name="blocks"/> and
    /// replaces each with a placeholder, so the block transforms skip their contents.
    /// </summary>
    private static string ExtractFencedCode(string text, List<string> blocks)
        => FencedCodePattern().Replace(text, m =>
        {
            var language = m.Groups["lang"].Value.Trim();
            var code = m.Groups["code"].Value.TrimEnd('\n');
            var cls = string.IsNullOrEmpty(language) ? string.Empty : $" class=\"language-{language}\"";
            blocks.Add($"<pre><code{cls}>{code}</code></pre>");
            return $"\u0000{blocks.Count - 1}\u0000";
        });

    // ---- compiled patterns (static, shared) ----

    private static Regex HeadingMatch() => new(@"^(?<level>#{1,6})\s+(?<text>.*)$", RegexOptions.Compiled);
    private static Regex OrderedItemPattern() => new(@"^\d+\.\s+", RegexOptions.Compiled);
    private static Regex CodeSpanPattern() => new("`(?<code>[^`]+)`", RegexOptions.Compiled);
    private static Regex LinkPattern() => new(@"\[(?<text>[^\]]+)\]\((?<url>[^)]+)\)", RegexOptions.Compiled);
    private static Regex BoldPattern() => new(@"\*\*(?<b>[^*]+)\*\*", RegexOptions.Compiled);
    private static Regex ItalicPattern() => new(@"\*(?<i>[^*]+)\*", RegexOptions.Compiled);
    private static Regex CodePlaceholderPattern() => new(@"\u0000(?<index>\d+)\u0000", RegexOptions.Compiled);
    private static Regex FencedCodePattern() => new(
        @"```(?<lang>[^\n]*)\n(?<code>.*?)\n?```",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Note: the patterns above are created fresh on each call (the renderer is low-frequency — one
    // render per object view). Caching them in static fields would shave a few allocations but adds
    // no correctness; keeping them as local factories keeps the class free of mutable state.
}
