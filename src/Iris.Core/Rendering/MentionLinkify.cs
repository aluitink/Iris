using System.Text;
using System.Text.RegularExpressions;
using Iris.Core.Identity;

namespace Iris.Core.Rendering;

/// <summary>
/// Wraps <c>@mention</c> and <c>#hashtag</c> tokens in an authored note's HTML body with links, so the
/// mention/hashtag renders as a tappable link (the Mastodon/Pleroma convention: the note's
/// <c>content</c> HTML carries <c>&lt;a class="mention"&gt;</c> / <c>&lt;a class="hashtag"&gt;</c> elements
/// alongside the structured <c>tag</c> array). This is the client-side composition of the "html within
/// the content body" half of the @mention convention — the structured <c>tag</c> entries (the
/// <c>Mention</c> / <c>Hashtag</c> objects) are built separately by the composing surface.
/// </summary>
/// <remarks>
/// <strong>Security:</strong> the input is expected to already be safe HTML (the output of
/// <see cref="Markdown.ToHtml"/>, which HTML-escapes its input first). <see cref="Linkify"/> does not
/// re-escape; it only inserts <c>&lt;a&gt;</c> elements at the positions of the supplied mention/hashtag
/// tokens. Existing <c>&lt;a&gt;</c> elements in the input are protected (pulled out as placeholders) so
/// their inner text and attributes are never altered. A mention/hashtag token is only linked when it is a
/// standalone word (not part of a longer run of word characters), so <c>100%#off</c> or an email address
/// are not mistaken for a hashtag.
/// </remarks>
public static partial class MentionLinkify
{
    /// <summary>
    /// A resolved mention to link in the body: its display text (e.g. <c>@bob</c> or
    /// <c>@user@domain</c>, exactly as it appears in the content) and the actor IRI the link points to.
    /// </summary>
    /// <param name="Display">The mention text as it appears in the content (including the leading
    /// <c>@</c>), e.g. <c>@bob</c>.</param>
    /// <param name="Iri">The resolved actor IRI — set as the link's <c>href</c>.</param>
    public sealed record Mention(string Display, Iri Iri);

    /// <summary>
    /// A hashtag to link in the body: its display text (e.g. <c>#hello</c>, as it appears in the content)
    /// and the browse/search URL the link points to.
    /// </summary>
    /// <param name="Display">The hashtag text as it appears in the content (including the leading
    /// <c>#</c>), e.g. <c>#hello</c>.</param>
    /// <param name="Href">The hashtag's browse/search URL — set as the link's <c>href</c>.</param>
    public sealed record Hashtag(string Display, string Href);

    /// <summary>
    /// Wraps the supplied mention and hashtag tokens in <paramref name="html"/> with links.
    /// </summary>
    /// <param name="html">The note's body HTML (already safe — typically the output of
    /// <see cref="Markdown.ToHtml"/>). May be null or whitespace.</param>
    /// <param name="mentions">The resolved mentions to link (display text + actor IRI), when any.</param>
    /// <param name="hashtags">The hashtags to link (display text + browse URL), when any.</param>
    /// <returns>
    /// The HTML with each matching mention/hashtag token wrapped in
    /// <c>&lt;a class="mention" href="…"&gt;</c> / <c>&lt;a class="hashtag" href="…"&gt;</c>, or the input
    /// unchanged when <paramref name="html"/> is blank or there are no mention/hashtag tokens to link.
    /// </returns>
    public static string Linkify(
        string? html,
        IReadOnlyList<Mention>? mentions,
        IReadOnlyList<Hashtag>? hashtags)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return html ?? string.Empty;
        }

        var hasMentions = mentions is { Count: > 0 };
        var hasHashtags = hashtags is { Count: > 0 };
        if (!hasMentions && !hasHashtags)
        {
            return html;
        }

        // Protect any existing <a>…</a> elements (e.g. an explicit Markdown link) so their inner text and
        // attributes are never altered by the mention/hashtag passes below. They are restored verbatim at
        // the end. An <a> whose content spans a protected region is not expected in Markdown output, so a
        // non-greedy match to the first </a> is sufficient.
        var protectedLinks = new List<string>();
        var working = AnchorElement().Replace(html, m =>
        {
            protectedLinks.Add(m.Value);
            return "\u0001" + (protectedLinks.Count - 1) + "\u0001";
        });

        if (hasMentions)
        {
            foreach (var mention in mentions!)
            {
                working = LinkToken(working, mention.Display, mention.Iri.Value, "mention");
            }
        }

        if (hasHashtags)
        {
            foreach (var hashtag in hashtags!)
            {
                working = LinkToken(working, hashtag.Display, hashtag.Href, "hashtag");
            }
        }

        working = ProtectedPlaceholder().Replace(working, m =>
            protectedLinks[int.Parse(m.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture)]);

        return working;
    }

    /// <summary>
    /// Display-side linkify for a note whose <c>content</c> is plain text / Markdown (NOT pre-rendered
    /// HTML) but which carries a structured <c>tag</c> array declaring mentions and hashtags. This is the
    /// display counterpart to the compose-time <see cref="Linkify"/>: it scans the plain body for
    /// <c>@handle</c> / <c>@user@domain</c> and <c>#hashtag</c> tokens, links any token whose handle /
    /// name matches a declared tag (the tag array is authoritative for a remote note), renders the body
    /// through <see cref="Markdown.ToHtml"/>, and wraps the matched tokens in links.
    /// </summary>
    /// <remarks>
    /// Only tokens that match a declared <paramref name="mentionIris"/> (by the actor handle, the IRI
    /// path's last segment) or a declared <paramref name="hashtags"/> entry (by name, case-insensitive)
    /// are linked — a bare <c>@word</c> or <c>#word</c> with no matching tag is left as plain text, so an
    /// arbitrary note is not sprinkled with dead links. A hashtag without a declared <c>href</c> links to
    /// <c>{instanceOrigin}/search?q=%23{tag}</c> when <paramref name="instanceOrigin"/> is supplied;
    /// otherwise it is left as plain text. The input is HTML-escaped by <see cref="Markdown.ToHtml"/>
    /// before the links are inserted, so the body is safe.
    /// </remarks>
    /// <param name="plainText">The note's plain-text / Markdown body. May be null or whitespace.</param>
    /// <param name="instanceOrigin">
    /// The instance origin (scheme + host) used to build a hashtag search URL when a hashtag tag carries
    /// no <c>href</c> of its own (e.g. <c>https://iris.luit.ink</c>). May be null (then href-less
    /// hashtags are not linked).
    /// </param>
    /// <param name="mentionIris">The actor IRIs declared in the note's <c>tag</c> (its mentions).</param>
    /// <param name="hashtags">The hashtag tags declared in the note's <c>tag</c> (name + optional href).</param>
    /// <returns>
    /// The body rendered through Markdown with each matched mention/hashtag token wrapped in
    /// <c>&lt;a class="mention" href="…"&gt;</c> / <c>&lt;a class="hashtag" href="…"&gt;</c>, or the
    /// Markdown-rendered body unchanged when there are no matching tags (or the body is blank).
    /// </returns>
    public static string LinkifyPlain(
        string? plainText,
        string? instanceOrigin,
        IReadOnlyList<Iri>? mentionIris,
        IReadOnlyList<(string Name, Iri? Href)>? hashtags)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return plainText ?? string.Empty;
        }

        var hasMentions = mentionIris is { Count: > 0 };
        var hasHashtags = hashtags is { Count: > 0 };
        if (!hasMentions && !hasHashtags)
        {
            return Markdown.ToHtml(plainText);
        }

        var html = Markdown.ToHtml(plainText);

        var linkifyMentions = new List<Mention>();
        if (hasMentions)
        {
            foreach (System.Text.RegularExpressions.Match match in MentionToken().Matches(html))
            {
                var display = match.Value;
                var handle = HandleOfToken(display);
                // Match the declared mention by the actor handle (the IRI path's last segment),
                // case-insensitively. The first declared mention that matches wins (the composing
                // surface / remote author de-duplicates mentions, so at most one should match).
                Iri? resolved = null;
                foreach (var m in mentionIris!)
                {
                    if (string.Equals(HandleOfIri(m), handle, StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = m;
                        break;
                    }
                }
                if (resolved is { } iri)
                {
                    linkifyMentions.Add(new Mention(display, iri));
                }
            }
        }

        var linkifyHashtags = new List<Hashtag>();
        if (hasHashtags)
        {
            foreach (System.Text.RegularExpressions.Match match in HashtagToken().Matches(html))
            {
                var display = match.Value;
                var name = display[1..]; // strip the leading '#'
                var tag = hashtags!.FirstOrDefault(
                    h => string.Equals(NameOf(h.Name), name, StringComparison.OrdinalIgnoreCase));
                if (tag is { } t)
                {
                    var href = t.Href?.Value
                        ?? (instanceOrigin is { Length: > 0 } origin
                            ? $"{origin.TrimEnd('/')}/search?q={Uri.EscapeDataString(display)}"
                            : null);
                    if (!string.IsNullOrEmpty(href))
                    {
                        linkifyHashtags.Add(new Hashtag(display, href!));
                    }
                }
            }
        }

        return Linkify(html, linkifyMentions, linkifyHashtags);
    }

    /// <summary>
    /// The actor handle of a mention token: the text after the last <c>@</c> (so <c>@user@domain</c>
    /// yields <c>domain</c> is NOT wanted — we want the local part). For <c>@user@domain</c> the handle is
    /// <c>user</c> (between the leading <c>@</c> and the second <c>@</c>); for <c>@handle</c> it is
    /// <c>handle</c>.
    /// </summary>
    private static string HandleOfToken(string display)
    {
        // display is "@handle" or "@user@domain". The handle is the segment after the FIRST '@' up to the
        // next '@' (if any) or the end.
        var afterFirst = display.AsSpan(1);
        var at = afterFirst.IndexOf('@');
        return at >= 0 ? afterFirst[..at].ToString() : afterFirst.ToString();
    }

    /// <summary>
    /// The actor handle of a mention IRI: the path's last segment (e.g. <c>…/u/alice</c> → <c>alice</c>,
    /// <c>…/users/Gargron</c> → <c>Gargron</c>). Returns the full IRI value when it is not a parseable URI
    /// or has no path segment, so a match simply fails (the token is left unlinked).
    /// </summary>
    private static string HandleOfIri(Iri iri)
    {
        try
        {
            var path = Uri.UnescapeDataString(new Uri(iri.Value).AbsolutePath).TrimEnd('/');
            var last = path.Split('/').LastOrDefault();
            return string.IsNullOrWhiteSpace(last) ? iri.Value : last;
        }
        catch (UriFormatException)
        {
            return iri.Value;
        }
    }

    /// <summary>
    /// The hashtag name without its leading <c>#</c> (for case-insensitive matching against a declared
    /// tag's name, which may or may not carry the <c>#</c>).
    /// </summary>
    private static string NameOf(string name) => name.StartsWith('#') ? name[1..] : name;

    /// <summary>
    /// Matches an <c>@handle</c> or <c>@user@domain</c> mention token in the body (a leading <c>@</c>
    /// followed by the local handle, optionally followed by <c>@domain</c>), at the start of the text or
    /// after a non-word character, with a trailing word boundary.
    /// </summary>
    [GeneratedRegex(@"(?<![\w/""'])@([A-Za-z0-9_-]+)(?:@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+))?")]
    private static partial Regex MentionToken();

    /// <summary>
    /// Matches a <c>#hashtag</c> token in the body (a leading <c>#</c> followed by letters/digits, no
    /// internal spaces), at the start of the text or after a non-word character, with a trailing word
    /// boundary (so <c>100%#off</c> is not matched).
    /// </summary>
    [GeneratedRegex(@"(?<![\w/""'])#([A-Za-z0-9_]+)")]
    private static partial Regex HashtagToken();

    /// <summary>
    /// Wraps a single token occurrence (a mention or hashtag, as it appears in the content) in an
    /// <c>&lt;a&gt;</c> element. The token is matched only as a standalone word — a leading
    /// <c>@</c>/<c>#</c> at the start of the text or after a non-word character, and a trailing boundary
    /// (so a longer run like <c>@user@domain@x</c> or <c>100%#off</c> is not partially linked). The
    /// replacement is applied to the FIRST occurrence (each token is distinct per the composing surface's
    /// de-duplication), which is the position the user typed it.
    /// </summary>
    private static string LinkToken(string html, string display, string href, string className)
    {
        if (string.IsNullOrEmpty(display) || string.IsNullOrEmpty(href))
        {
            return html;
        }

        // Escape the display text for use in a regex (handles the '@'/'#' and any '.' in a domain).
        // The trailing boundary excludes a following word char, hyphen, or '#' so a shorter token is
        // not linked as the prefix of a longer hyphenated handle or a hashtag run (e.g. a declared
        // '@ii' must not be linked when the body actually contains '@ii-a2').
        var pattern =
            "(?<![\\w/\"'])" +
            Regex.Escape(display) +
            "(?![\\w-#])";
        var replacement =
            "<a class=\"" + className + "\" href=\"" + href + "\">" + display + "</a>";
        return new Regex(pattern).Replace(html, replacement, count: 1);
    }

    /// <summary>
    /// Matches an <c>&lt;a&gt;</c> element (opening tag through its closing tag, non-greedy) — the
    /// elements <see cref="Linkify"/> protects while it rewrites mention/hashtag tokens.
    /// </summary>
    [GeneratedRegex(@"<a\b[^>]*>.*?</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorElement();

    /// <summary>
    /// Matches a protected-<c>&lt;a&gt;</c> placeholder (the <c>\x01{index}\x01</c> token
    /// <see cref="Linkify"/> substitutes for a pulled-out <c>&lt;a&gt;</c> element) so it can be restored.
    /// </summary>
    [GeneratedRegex(@"\x01(?<index>\d+)\x01")]
    private static partial Regex ProtectedPlaceholder();
}
