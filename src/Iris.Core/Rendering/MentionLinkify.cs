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
            return "" + (protectedLinks.Count - 1) + "";
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
        var pattern =
            "(?<![\\w/\"'])" +
            Regex.Escape(display) +
            "(?![\\w])";
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
