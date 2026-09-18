using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.Security;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Server.Inbox;

/// <summary>
/// The default <see cref="IInboundTagNormalizer"/>: resolves <c>@mention</c> / <c>#hashtag</c> tokens in an
/// inbound note's <c>content</c> into <see cref="Mention"/> / <c>Hashtag</c> tags when they are not already
/// declared in the note's <c>tag</c> array (Phase 156, Slice C).
/// </summary>
/// <remarks>
/// See the <see cref="IInboundTagNormalizer"/> remarks for the safety model (best-effort, bounded,
/// non-federating, idempotent). Mentions resolve through <see cref="IAccountResolver"/> (WebFinger, cached)
/// for <c>@user@domain</c> tokens and as a local-actor short-circuit (no network) for a bare <c>@handle</c>;
/// hashtags need no network call (their href is the author's-instance search URL).
/// </remarks>
public sealed partial class InboundTagNormalizer(
    IAccountResolver accountResolver,
    IOptions<ActivityPubServerOptions> options,
    ILogger<InboundTagNormalizer>? logger = null) : IInboundTagNormalizer
{
    /// <inheritdoc/>
    public int MaxMentionResolutions { get; } = 5;

    /// <summary>
    /// A bare <c>@handle</c> token (no <c>@domain</c>) — a same-instance mention.
    /// </summary>
    [GeneratedRegex(@"(?<![\w/""'])@([A-Za-z0-9_]+)(?![\w@])")]
    private static partial Regex BareMentionToken();

    /// <summary>
    /// A federated <c>@user@domain</c> token.
    /// </summary>
    [GeneratedRegex(@"(?<![\w/""'])@([A-Za-z0-9_.-]+)@([A-Za-z0-9.-]+)(?::\d+)?(?![\w@])")]
    private static partial Regex FederatedMentionToken();

    /// <summary>
    /// A <c>#hashtag</c> token.
    /// </summary>
    [GeneratedRegex(@"(?<![\w""'])#([A-Za-z0-9_]+)")]
    private static partial Regex HashtagToken();

    /// <summary>
    /// An HTML tag (stripped before token scanning so a mention inside markup is still found).
    /// </summary>
    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex HtmlTag();

    /// <inheritdoc/>
    public async Task NormalizeAsync(IObject? obj, CancellationToken ct)
    {
        if (obj is null)
        {
            return;
        }

        try
        {
            var text = ContentText(obj);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var existingTags = obj.Tag?.ToList() ?? [];
            var existingMentionIris = new HashSet<string>(
                obj.GetMentionIris().Select(i => i.Value),
                StringComparer.OrdinalIgnoreCase);
            var existingHashtagNames = new HashSet<string>(
                obj.GetHashtagTags().Select(h => NormalizeHashtagName(h.Name)),
                StringComparer.OrdinalIgnoreCase);

            var newTags = new List<IObjectOrLink>();
            var resolvedMentions = 0;
            var authorOrigin = AuthorOrigin(obj);

            // Mentions: resolve tokens not already declared (bounded, best-effort).
            foreach (var (token, handle, domain) in MentionTokens(text))
            {
                if (resolvedMentions >= MaxMentionResolutions)
                {
                    break;
                }

                if (existingMentionIris.Contains(token))
                {
                    continue;
                }

                var iri = await ResolveMentionAsync(handle, domain, ct).ConfigureAwait(false);
                if (iri is not { } resolvedIri)
                {
                    continue;
                }

                if (existingMentionIris.Contains(resolvedIri.Value))
                {
                    continue;
                }

                newTags.Add(new Mention { Href = resolvedIri.Uri });
                existingMentionIris.Add(resolvedIri.Value);
                resolvedMentions++;
            }

            // Hashtags: add a tag for each token not already declared (no network call).
            foreach (Match match in HashtagToken().Matches(text))
            {
                var name = "#" + match.Groups[1].Value;
                if (existingHashtagNames.Contains(NormalizeHashtagName(name)))
                {
                    continue;
                }

                newTags.Add(BuildHashtagTag(name, authorOrigin));
                existingHashtagNames.Add(NormalizeHashtagName(name));
            }

            if (newTags.Count > 0)
            {
                var merged = new List<IObjectOrLink>(existingTags.Count + newTags.Count);
                merged.AddRange(existingTags);
                merged.AddRange(newTags);
                obj.Tag = merged;
                logger?.LogInformation(
                    "InboundTagNormalizer added {Count} tag(s) to {ObjectIri}",
                    newTags.Count,
                    obj.ResolveObjectIri()?.Value ?? "(no id)");
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a normalization failure must never fail the post. The object is stored as-is
            // (its tokens simply render as plain text, the pre-Slice-C behavior).
            logger?.LogWarning(ex, "InboundTagNormalizer failed for {ObjectIri}; storing the object unchanged",
                obj.ResolveObjectIri()?.Value ?? "(no id)");
        }
    }

    /// <summary>
    /// Yields the mention tokens found in <paramref name="text"/> as (full token, handle, domain). A bare
    /// <c>@handle</c> has a null domain; a federated <c>@user@domain</c> has both. Tokens are de-duplicated
    /// (first occurrence wins) so each is resolved at most once.
    /// </summary>
    private static IEnumerable<(string Token, string Handle, string? Domain)> MentionTokens(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<(string Token, string Handle, string? Domain)>();

        // Federated tokens first (a bare-handle regex would also match the handle part of @user@domain).
        foreach (Match match in FederatedMentionToken().Matches(text))
        {
            var token = "@" + match.Groups[1].Value + "@" + match.Groups[2].Value;
            if (seen.Add(token))
            {
                results.Add((token, match.Groups[1].Value, match.Groups[2].Value));
            }
        }

        foreach (Match match in BareMentionToken().Matches(text))
        {
            // Skip a bare handle that is actually the handle part of an already-matched federated token.
            var token = "@" + match.Groups[1].Value;
            if (results.Any(r => r.Token.StartsWith(token + "@", StringComparison.Ordinal)))
            {
                continue;
            }

            if (seen.Add(token))
            {
                results.Add((token, match.Groups[1].Value, null));
            }
        }

        return results;
    }

    /// <summary>
    /// Resolves a mention token to an actor IRI: a bare <c>@handle</c> resolves to a local actor IRI (no
    /// network — the handle names a same-instance actor); a federated <c>@user@domain</c> resolves through
    /// <see cref="IAccountResolver"/> (WebFinger, cached). Returns null when the actor cannot be resolved.
    /// </summary>
    /// <remarks>
    /// A bare handle is minted as a local actor IRI (<c>{base}/ap/v1/u/{handle}</c>) without an existence
    /// check: a mention to a handle that does not exist is a harmless dead link (the same as a typo'd
    /// mention), and the IRI shape is correct regardless. The meaningful guard is that a bare handle
    /// never mints a cross-origin IRI (it is always same-instance).
    /// </remarks>
    private async Task<Iri?> ResolveMentionAsync(string handle, string? domain, CancellationToken ct)
    {
        if (domain is null)
        {
            var baseUrl = _options.Value.BaseUri?.Value;
            if (baseUrl is null)
            {
                return null;
            }

            return BuildLocalActorIri(baseUrl, handle);
        }

        // Federated: resolve via WebFinger (cached). Best-effort — a failure returns null (no throw).
        try
        {
            return await _accountResolver.ResolveAsync(handle + "@" + domain, ct: ct).ConfigureAwait(false);
        }
        catch
        {
            // IAccountResolver documents null-on-failure, but a defensive guard: a resolution failure must
            // never propagate (a post must not fail because a mention could not be resolved).
            return null;
        }
    }

    private static Iri BuildLocalActorIri(string baseUrl, string handle)
        => new Iri(baseUrl.TrimEnd('/') + ActivityPubServerConstants.RoutePrefix + "/u/" + handle);

    /// <summary>
    /// The author's instance origin (scheme+host, e.g. <c>https://b.test</c>) for building a href-less
    /// hashtag's search URL — the remote hashtag's search lives on the author's instance. Null when the
    /// author IRI is not a parseable http(s) URI.
    /// </summary>
    private static string? AuthorOrigin(IObject obj)
    {
        var authorIri = obj.AttributedTo?.FirstOrDefault()?.ResolveObjectIri();
        if (authorIri is not { } iri)
        {
            return null;
        }

        if (Uri.TryCreate(iri.Value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        return null;
    }

    /// <summary>
    /// Builds a <c>Hashtag</c> tag (a generic <c>Object</c> of type <c>Hashtag</c> whose <c>name</c>
    /// is the <c>#tag</c> text and whose <c>href</c> is the author's-instance search URL, when an origin is
    /// known) — the exact shape <see cref="IriExtensions.GetHashtagTags"/> reads back.
    /// </summary>
    private static IObjectOrLink BuildHashtagTag(string name, string? origin)
    {
        var hashtag = new ActivityObject { Type = ["Hashtag"], Name = [name] };
        if (origin is { Length: > 0 } o
            && Iri.TryParse(o + "/search?q=" + Uri.EscapeDataString(name), out var href))
        {
            hashtag.ExtensionData ??= new Dictionary<string, JsonElement>();
            hashtag.ExtensionData["href"] = JsonSerializer.SerializeToElement(href.Value);
        }

        return hashtag;
    }

    /// <summary>
    /// Joins the object's <c>content</c> parts into a single string and strips HTML tags, so token
    /// scanning sees the visible text (a mention inside <c>&lt;p&gt;</c> markup is still found).
    /// </summary>
    private static string ContentText(IObject obj)
    {
        if (obj.Content is not { } contents)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var part in contents)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(part);
        }

        var joined = sb.ToString();
        return HtmlTag().Replace(joined, " ");
    }

    /// <summary>
    /// Normalizes a hashtag name for idempotency comparison (lower-case, leading <c>#</c> stripped).
    /// </summary>
    private static string NormalizeHashtagName(string name)
        => name.TrimStart('#').ToLowerInvariant();

    private readonly IAccountResolver _accountResolver = accountResolver!;
    private readonly IOptions<ActivityPubServerOptions> _options = options!;
}
