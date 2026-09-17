using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Normalizes an inbound note's <c>tag</c> array before it is stored: resolves any <c>@mention</c> /
/// <c>#hashtag</c> tokens that appear in the note's <c>content</c> but are not already declared in its
/// <c>tag</c>, adding the corresponding <see cref="Mention"/> / <c>Hashtag</c> tags so the note renders
/// with tappable links and carries structured mention/hashtag references (Phase 156, Slice C).
/// </summary>
/// <remarks>
/// <strong>Why.</strong> Remote ActivityPub servers (Mastodon, Pleroma, Misskey, Pluralsite) almost always
/// pre-resolve mentions and ship a complete <c>tag</c> array, so this is rarely a no-op. But a
/// non-conforming (or minimal) server can deliver a note whose body carries <c>@mention</c> /
/// <c>#hashtag</c> tokens with no matching <c>tag</c> entry — in which case the note would render those
/// tokens as inert text. This normalizer closes that gap on the server (the object-id authority), so such
/// a note is stored with the structured tags a client (the display-side linkify, Phase 156 Slice B) needs
/// to render tappable links.
/// </remarks>
/// <para>
/// <strong>Safety (best-effort, bounded, non-federating).</strong>
/// <list type="bullet">
/// <item>
/// <description><em>Best-effort:</em> a mention that cannot be resolved (an unreachable peer, a 404, a
/// malformed handle) is left untagged — the note is still stored and the token renders as plain text. A
/// failure never fails the post.</description>
/// </item>
/// <item>
/// <description><em>Bounded:</em> at most <see cref="MaxMentionResolutions"/> mention resolutions (WebFinger
/// calls) are made per note, so a flood source cannot amplify a WebFinger DoS through the ingestion path.
/// Hashtags need no network call (the tag's href is the author's-instance search URL) and are uncapped.</description>
/// </item>
/// <item>
/// <description><em>Non-federating:</em> the note's <c>to</c>/<c>cc</c> audience is <em>not</em> mutated —
/// the note is not re-delivered to the mentioned actors. The <c>tag</c> is for display/structure only. This
/// deliberately avoids changing federation behavior (and its loop-safety implications) on ingestion.</description>
/// </item>
/// <item>
/// <description><em>Idempotent:</em> a token already covered by an existing <c>tag</c> (a mention whose IRI
/// is present, a hashtag whose name matches) is not duplicated.</description>
/// </item>
/// </list>
/// </para>
public interface IInboundTagNormalizer
{
    /// <summary>
    /// Adds <see cref="Mention"/> / <c>Hashtag</c> tags for any <c>@mention</c> / <c>#hashtag</c> tokens in
    /// <paramref name="obj"/>'s <c>content</c> that are not already declared in its <c>tag</c> array.
    /// </summary>
    /// <param name="obj">The inbound object (a <see cref="Note"/> or other content-bearing object). May be
    /// null (no-op).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <remarks>
    /// Never throws: a resolution failure (or a null/unresolvable object) leaves the object unchanged. The
    /// object's <c>tag</c> is reassigned only when at least one tag was added.
    /// </remarks>
    Task NormalizeAsync(IObject? obj, CancellationToken ct);

    /// <summary>
    /// The maximum number of <c>@mention</c> tokens resolved (WebFinger calls) per note — the bound that
    /// keeps a flood source from amplifying a WebFinger DoS through ingestion.
    /// </summary>
    int MaxMentionResolutions { get; }
}
