using System.Linq;
using System.Text;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Rendering;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Web.Client.Components;

public partial class ObjectView
{
    [Parameter]
    public IObjectOrLink? Item { get; set; }

    /// <summary>
    /// Suppress the inline "In reply to" parent-context card (the fetched-parent context that would
    /// otherwise render below the post body). Set by the object detail page, which renders the full
    /// thread context (parent + grandparent) as a dedicated card above the main object card; without
    /// this the reply's own parent would render twice.
    /// </summary>
    [Parameter]
    public bool SuppressParentContext { get; set; }

    /// <summary>
    /// Whether to render the per-card moderation buttons (Block / Mute / Report) for the post's author.
    /// Defaults to <c>false</c> so feed cards stay clean — moderation is reserved for the actor detail
    /// page (the <see cref="ActorCard"/> in the page header carries the Block / Mute / Report actions
    /// at the actor level). Set to <c>true</c> where the card is the primary moderation surface (the
    /// object detail page, the profile's own-posts tab, search results, and the reply thread).
    /// </summary>
    [Parameter]
    public bool ShowModeration { get; set; } = false;

    /// <summary>
    /// Suppress the card's <c>.object-time</c> timestamp. Set by <c>NotificationRow</c> for a
    /// Create/reply whose embedded note's <c>published</c> equals the notification activity's
    /// <c>published</c> (Mastodon sets both to the status creation time), so the notification header
    /// time and the embedded post time don't render as an identical duplicate.
    /// </summary>
    [Parameter]
    public bool SuppressTime { get; set; }

    /// <summary>
    /// Suppress the <em>activity-level</em> timestamp of a boost card — the time on the "Boosted by"
    /// line (when the boost was made) — while keeping the boosted post's own timestamp. Set by
    /// <c>NotificationRow</c> for an Announce, where the notification header already carries the boost
    /// time; the rendered post below then shows only the post's own time (matching how posts render
    /// under reply/post notifications).
    /// </summary>
    [Parameter]
    public bool SuppressBoostTime { get; set; }

    [Microsoft.AspNetCore.Components.Inject]
    private Iris.Web.Client.Accounts.IActorSessionAccessor Session { get; set; } = default!;

    [Microsoft.AspNetCore.Components.Inject]
    private Iris.Web.Client.Ui.UiContext Ui { get; set; } = default!;

    private IObject? Obj => Item as IObject;

    private Iri? AuthorIri => (Obj as ActivityObject)?.AttributedTo?.FirstOrDefault()?.ResolveObjectIri();
    private Iri? ParentIri => Obj?.GetParentIri();
    private IReadOnlyList<Iri> MentionIris => Obj?.GetMentionIris() ?? [];
    private IReadOnlyList<(string Name, Iri? Href)> HashtagTags => Obj?.GetHashtagTags() ?? [];
    private IReadOnlyList<(string Name, string ShortCode, Iri? Url)> EmojiTags => ResolveEmojiTags();
    private PollData? Poll => _pollOverride ?? ResolvePoll();
    private IReadOnlyList<Iri> AudienceIris => FilterDisplayAudience(Obj?.GetAudienceIris());
    private PollData? _pollOverride;
    private bool _pollVoted;
    private int _pollVotedOption = -1;
    private bool _pollBusy;
    private string? _parentPreview;

    // Phase 90.1 — the fetched parent ("in reply to") object, rendered inline as a muted context
    // card above the content instead of the previous one-line preview.
    private IObject? _parentObject;
    private Iri? _parentAuthorIri;

    // 119.1 — the fetched target of a Like (the liked object). A Like activity carries its target as a
    // bare link (no embedded content), so the profile Likes tab would otherwise render only an IRI.
    // When the target is a link-only reference this is resolved (the liked Note/Article) so the card
    // shows the actual liked content, not just a link.
    private IObject? _likedObject;

    // 121.7 — the fetched target of an Announce (the boosted object). An Announce activity carries its
    // target as a bare link (no embedded content), so the boost card would otherwise render only
    // "View boosted post →". When the target is link-only this is resolved so the card shows a
    // content preview (author, text, media) like Mastodon.
    private IObject? _announcedObject;

    // 152 — the original post a boosted reply was replying to. When the boosted object is itself a
    // reply (its inReplyTo is set), the boost card renders the ORIGINAL post it answers to as the card
    // body (just with the "Boosted by" indicator) rather than the reply. Fetched in OnInitializedAsync
    // when BoostedObject.GetParentIri() is non-null. Null when the boosted object is not a reply (or the
    // fetch has not completed / failed) — in that case the card renders the boosted object itself.
    private IObject? _boostedParentObject;

    // 152 — the original post a liked reply was replying to. When the liked object is itself a reply,
    // the like card renders the ORIGINAL post it answers to as the card body (just with the "Liked"
    // indicator) rather than the reply. Fetched in OnInitializedAsync when _likedObject.GetParentIri()
    // is non-null. Null otherwise — the card renders the liked object itself.
    private IObject? _likedParentObject;

    // Lemmy score data (fetched from the Lemmy REST API) for the current post, when the post
    // is a Lemmy post. Null when not a Lemmy post or the fetch failed.
    private LemmyPostScore? _lemmyScore;

    private DateTime? Published => Obj?.Published;
    private DateTime? Updated => Obj?.GetUpdated();
    private DateTime? ArticlePublishedTime => (ActivityEmbeddedObject ?? Obj)?.GetPublishedTime();
    private string? ArticleInLanguage => (ActivityEmbeddedObject ?? Obj)?.GetInLanguage();
    private TimeSpan? ArticleDuration => (Obj as ActivityObject) is ActivityObject { Duration: { } d } ? d : null;
    private bool IsSensitive => Obj?.IsSensitive() ?? false;
    private string? Summary => Obj?.GetSummary();
    private string? ActorName => (Obj as Actor)?.Name?.FirstOrDefault();

    /// <summary>
    /// Whether the content object is locked (<c>iris:locked: true</c>) — replies are disabled on the
    /// originating post. Read from the <c>iris:</c> extension terms the server renders (138.25).
    /// Null-safe: returns <c>false</c> when the term is absent or the namespace is unknown.
    /// </summary>
    private bool IsLocked
    {
        get
        {
            var ns = Session.IrisNamespaceBase?.Value;
            return ns is not null && Obj?.GetLocked(ns) == true;
        }
    }

    /// <summary>
    /// Whether the content object is featured/pinned (<c>iris:featured: true</c>). Read from the
    /// <c>iris:</c> extension terms the server renders (138.25).
    /// </summary>
    private bool IsFeatured
    {
        get
        {
            var ns = Session.IrisNamespaceBase?.Value;
            return ns is not null && Obj?.GetFeatured(ns) == true;
        }
    }

    /// <summary>
    /// The language tag (<c>iris:language</c>) the server rendered on the content object (138.25).
    /// Falls back to the standard ActivityStreams <c>inLanguage</c> extension when the <c>iris:</c>
    /// term is absent.
    /// </summary>
    private string? ObjectLanguage
    {
        get
        {
            var ns = Session.IrisNamespaceBase?.Value;
            if (ns is not null)
            {
                var irisLang = Obj?.GetLanguage(ns);
                if (!string.IsNullOrWhiteSpace(irisLang))
                {
                    return irisLang;
                }
            }

            return Obj?.GetInLanguage();
        }
    }

    // 153 — the object IRI the whole content card links to (the stretched-link overlay target). For a
    // Create this is the created object's IRI; for a bare content object it is its own IRI. Null when
    // the card has no navigable object (e.g. a link-only Create or an actor/tombstone card), in which
    // case the card is not made whole-card-clickable.
    private Iri? CardLinkIri =>
        Item is Create ? CreatedObjectIri
        : Obj is IObject { Id: { Length: > 0 } id } ? new Iri(id)
        : null;

    // 153 — a deterministic hue (0–359) derived from the card's primary author IRI, used to tint the
    // actor-header banner strip so each author's cards carry a consistent personal color. Stable across
    // renders for the same author (FNV-1a hash of the IRI string).
    private string CardHueStyle =>
        CardHueIri is { } iri ? $"--card-hue: {CardHue(iri.Value)};" : string.Empty;

    /// <summary>
    /// The actor IRI the banner-strip hue is derived from: the content author for a direct object, the
    /// activity author for a Create, else null (no strip tint).
    /// </summary>
    private Iri? CardHueIri => Item is Create ? ActivityActorIri : AuthorIri;

    /// <summary>
    /// Maps a stable string (an actor IRI) to a hue in [0, 359] via FNV-1a, so the same author always
    /// gets the same banner tint.
    /// </summary>
    private static int CardHue(string key)
    {
        uint hash = 2166136261;
        foreach (char c in key)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return (int)(hash % 360u);
    }

    /// <summary>
    /// The object's <c>name</c> property (the first string). For Lemmy Page objects this is the post
    /// title; for most other object types it is null or redundant with the content.
    /// </summary>
    private string? Name => Obj?.Name?.FirstOrDefault();

    /// <summary>
    /// The embedded object's <c>name</c> property (the first string). Used for the Create/Announce
    /// branches where the content object is nested inside the activity.
    /// </summary>
    private string? ActivityName => ActivityEmbeddedObject?.Name?.FirstOrDefault();

    private bool NameIsRedundant
    {
        get
        {
            var actor = Obj as Actor;
            if (actor is null)
            {
                return false;
            }

            var name = actor.Name?.FirstOrDefault();
            var username = actor.PreferredUsername;
            return !string.IsNullOrWhiteSpace(name)
                && !string.IsNullOrWhiteSpace(username)
                && name!.Equals(username, StringComparison.OrdinalIgnoreCase);
        }
    }

    private IReadOnlyList<string> MediaAttachments
        => (Obj?.GetMediaAttachments() ?? [])
            .Select(m => RewriteMediaToSameOrigin(m.Iri.Value))
            .ToList();

    private IReadOnlyList<RichAttachment> RichAttachments => Obj?.GetRichAttachments() ?? [];

    /// <summary>
    /// The self-contained media IRI of an object whose body IS the media (a PeerTube <c>Video</c>/
    /// <c>Audio</c>/<c>Image</c> object whose <c>url</c> points at the media file), rewritten
    /// same-origin. Null when the object is not a self-contained media object (its media, if any, is in
    /// <c>attachment</c> and is rendered by <see cref="MediaGallery"/>).
    /// </summary>
    private string? SelfMediaSrc
    {
        get
        {
            if (Obj?.GetSelfMediaIri() is not { } iri)
            {
                return null;
            }

            return RewriteMediaToSameOrigin(iri.Value);
        }
    }

    private bool IsVideoObject => Obj is KristofferStrube.ActivityStreams.Video;
    private bool IsAudioObject => Obj is KristofferStrube.ActivityStreams.Audio;

    /// <summary>
    /// The media attachments of an activity's embedded object (the Note/Article a <c>Create</c> or
    /// <c>Announce</c> wraps), each rewritten same-origin. The home timeline and profile outbox render
    /// feed items as the wrapping activity (a <c>Create</c>), not the embedded Note, so the direct-object
    /// <see cref="MediaAttachments"/> (which reads <c>Obj</c> — the activity itself, which carries no
    /// <c>attachment</c>) is empty there; this reads the embedded object instead.
    /// </summary>
    private IReadOnlyList<string> ActivityMediaAttachments
        => (ActivityEmbeddedObject?.GetMediaAttachments() ?? [])
            .Select(m => RewriteMediaToSameOrigin(m.Iri.Value))
            .ToList();

    private IReadOnlyList<RichAttachment> ActivityRichAttachments => ActivityEmbeddedObject?.GetRichAttachments() ?? [];

    /// <summary>
    /// Rewrites a media IRI to its browser-loadable same-origin form. Local media (the instance's own
    /// <c>/ap/v1/media/{id}</c>) is stripped to a relative path. Cross-origin external media is routed
    /// through the media proxy (<c>/ap/v1/media/proxy?url={encoded}</c>) so the browser loads it from
    /// the instance's own origin (no CORS, no mixed-content). A relative IRI or a non-HTTP(S) URL is
    /// returned unchanged.
    /// </summary>
    internal static string RewriteMediaToSameOrigin(string mediaIri)
    {
        if (!Uri.TryCreate(mediaIri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return mediaIri;
        }

        // Local media (the instance's own /ap/v1/media/{id}) → strip to a relative path.
        if (uri.AbsolutePath.StartsWith("/ap/v1/media/", StringComparison.Ordinal))
        {
            return uri.AbsolutePath + uri.Query;
        }

        // Cross-origin external media → route through the media proxy.
        return $"/ap/v1/media/proxy?url={Uri.EscapeDataString(mediaIri)}";
    }

    private MarkupString RenderedContent
    {
        get
        {
            var content = JoinStrings(Obj?.Content);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new MarkupString(string.Empty);
            }

            // Pre-rendered HTML is emitted verbatim; Markdown/plain text is run through the safe
            // dependency-free Markdown renderer (HTML-escaped first, so raw markup is inert) so
            // Markdown-sourced content displays as formatted HTML instead of literal source.
            // 156 (Slice B): the plain-text branch is also linkified (display-side) so a remote note that
            // ships plain-text @mentions / #hashtags but carries them in its `tag` array renders tappable
            // links (pre-rendered HTML is already self-linking, so it is emitted verbatim and untouched).
            return new MarkupString(RenderObjectContent(Obj, content));
        }
    }

    /// <summary>
    /// 156 (Slice B) — Renders an object's <c>content</c> for display: pre-rendered HTML is emitted
    /// verbatim (it already carries its own <c>&lt;a class="mention"&gt;</c>/<c>&lt;a
    /// class="hashtag"&gt;</c> links, e.g. from a remote Mastodon/Pluralsite author, or from Iris's own
    /// compose-time linkify); plain text / Markdown is run through the safe Markdown renderer AND
    /// display-linkified, so a note whose body is plain text but whose <c>tag</c> array declares mentions
    /// and hashtags renders those tokens as tappable links. The mention/hashtag targets come from the
    /// object's own <c>tag</c> (authoritative for a remote note); a hashtag without a declared
    /// <c>href</c> links to the author's instance search.
    /// </summary>
    /// <param name="obj">The object whose content is rendered (supplies the <c>tag</c> + author origin).</param>
    /// <param name="content">The joined content string (already non-blank by the caller).</param>
    /// <returns>The safe HTML for the content (verbatim for pre-rendered, Markdown+linkified otherwise).</returns>
    private static string RenderObjectContent(IObject? obj, string content)
    {
        if (obj is { } o && o.IsPreRenderedHtmlContent())
        {
            return content;
        }

        // Plain text / Markdown: render to HTML, then linkify any @mention / #hashtag token the object's
        // tag array declares (display-side counterpart of the compose-time linkify — the tags are the
        // authoritative link targets for a remote note). The origin used for a href-less hashtag search
        // is the author's instance (a remote hashtag's search lives on the author's instance).
        var authorIri = (obj as ActivityObject)?.AttributedTo?.FirstOrDefault()?.ResolveObjectIri();
        string? origin = authorIri is { } a
            ? SafeOrigin(a.Value)
            : null;
        return MentionLinkify.LinkifyPlain(
            content,
            origin,
            obj?.GetMentionIris() ?? [],
            obj?.GetHashtagTags() ?? []);
    }

    /// <summary>
    /// The scheme+host origin of an IRI (e.g. <c>https://iris.luit.ink</c>), or null when the IRI is not a
    /// parseable http(s) URI.
    /// </summary>
    private static string? SafeOrigin(string iri)
    {
        if (Uri.TryCreate(iri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        return null;
    }

    private bool Revealed;
    private bool ActivityRevealed;

    // Phase 102 — a single shared busy flag for the card-header moderation buttons (block / mute /
    // report). One action at a time is enough for these fire-and-forget header controls; the flag
    // keeps a double-click from firing two deliveries.
    private bool _cardModBusy;

    /// <summary>
    /// The author of the fetched parent ("in reply to") object, for the context card's "In reply to
    /// @author" label. Null until the parent is fetched or when the parent has no author.
    /// </summary>
    private Iri? ParentAuthorIri => _parentAuthorIri;

    /// <summary>
    /// The rendered content of the fetched parent ("in reply to") object, shown muted above the
    /// reply so the reader has context for what is being answered.
    /// </summary>
    private MarkupString ParentContent
    {
        get
        {
            if (_parentObject is not ActivityObject po)
            {
                return new MarkupString(string.Empty);
            }

            var content = JoinStrings(po.Content);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new MarkupString(string.Empty);
            }

            return new MarkupString(RenderObjectContent(po, content));
        }
    }

    private bool HasParentContext => _parentObject is not null;

    /// <summary>
    /// The rich media attachments of the fetched parent ("in reply to") object, for rendering in the
    /// context card so the reader sees what media the reply is answering. Empty when the parent has
    /// no attachments or has not yet been fetched.
    /// </summary>
    private IReadOnlyList<RichAttachment> ParentRichAttachments => _parentObject?.GetRichAttachments() ?? [];

    private string? ActivityVerb => Item switch
    {
        Announce => "boosted",
        Like => "liked",
        Follow => "followed",
        _ => null
    };

    private IObject? ActivityEmbeddedObject
    {
        get
        {
            if (Item is not Activity { Object: { } objects })
            {
                return null;
            }

            return objects.FirstOrDefault() as IObject;
        }
    }

    private Iri? ActivityActorIri
        => (Item as Activity)?.Actor?.FirstOrDefault()?.ResolveObjectIri();

    /// <summary>
    /// The IRI of the target of a <c>Like</c> activity (the liked object).
    /// </summary>
    private Iri? LikeTargetIri
    {
        get
        {
            if (Item is not Like like)
            {
                return null;
            }

            var target = like.Object?.FirstOrDefault();
            if (target is IObject { Id: { Length: > 0 } targetId })
            {
                return new Iri(targetId);
            }

            if (target is ILink { Href: { } linkUri })
            {
                return new Iri(linkUri.OriginalString);
            }

            return null;
        }
    }

    private Iri? FollowTargetIri
    {
        get
        {
            if (Item is not Follow follow)
            {
                return null;
            }

            var target = follow.Object?.FirstOrDefault();
            if (target is IObject { Id: { Length: > 0 } targetId })
            {
                return new Iri(targetId);
            }

            if (target is ILink { Href: { } linkUri })
            {
                return new Iri(linkUri.OriginalString);
            }

            return null;
        }
    }

    private string? CreateIri => (Item as Create)?.Id;

    /// <summary>
    /// The rendered content of the liked object (a <c>Like</c>'s target), when present.
    /// </summary>
    private MarkupString LikedContent
    {
        get
        {
            if (ActivityEmbeddedObject is not { } embedded)
            {
                return new MarkupString(string.Empty);
            }

            var content = embedded is ActivityObject ao ? JoinStrings(ao.Content) : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                return new MarkupString(string.Empty);
            }

            return new MarkupString(RenderObjectContent(embedded, content!));
        }
    }

    private MarkupString ActivityContent
    {
        get
        {
            if (ActivityEmbeddedObject is not { } embedded)
            {
                return new MarkupString(string.Empty);
            }

            var content = embedded is ActivityObject ao ? JoinStrings(ao.Content) : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                return new MarkupString(string.Empty);
            }

            return new MarkupString(RenderObjectContent(embedded, content!));
        }
    }

    private DateTime? ActivityPublished => ActivityEmbeddedObject?.Published;

    /// <summary>
    /// The boost activity's own <c>published</c> (when the boost was made) — distinct from
    /// <see cref="BoostedPublished"/>, which is the boosted post's time. Rendered on the "Boosted by"
    /// line when <c>SuppressBoostTime</c> is false.
    /// </summary>
    private DateTime? BoostActivityPublished => (Item as Activity)?.Published;

    private Iri? CreatedObjectIri
    {
        get
        {
            if (ActivityEmbeddedObject is { Id: { Length: > 0 } id })
            {
                return new Iri(id);
            }

            return null;
        }
    }

    private bool IsContentCreate => Item is Create && ActivityEmbeddedObject is ActivityObject;

    /// <summary>
    /// Whether the current post should show the vote bar (upvote/downvote/score) instead of the
    /// standard engagement bar. Generalized from the original Lemmy-IRI gate (138.27 S2): the vote
    /// bar is shown when the content object carries vote data — either the <c>iris:dislikedCount</c>
    /// extension (a non-zero dislike count, meaning the source instance supports downvotes) or a
    /// <c>LemmyPostScore</c> fetched from the Lemmy REST API (a Lemmy-IRI-shaped post). This allows
    /// any downvote-capable peer (not just Lemmy) to get the downvote affordance.
    /// </summary>
    private bool HasVoteData
    {
        get
        {
            IObject? contentObj = Item switch
            {
                Create c => ActivityEmbeddedObject,
                Announce a => UnwrapCreate(ActivityEmbeddedObject ?? _announcedObject),
                IObject o => o,
                _ => null
            };
            if (contentObj is null)
            {
                return false;
            }

            if (_lemmyScore is not null)
            {
                return true;
            }

            var ns = Session.IrisNamespaceBase?.Value;
            if (ns is { } n
                && (contentObj.GetDislikedCount(n) is > 0
                    || contentObj.GetIsDisliked(n) is true))
            {
                return true;
            }

            if (contentObj is { Id: { Length: > 0 } id })
            {
                return LemmyPostScore.TryParsePostIri(new Iri(id)) is not null;
            }

            return false;
        }
    }

    /// <summary>
    /// Recursively unwraps a <c>Create</c> activity to find the underlying content object.
    /// Lemmy outbox items are often nested as Announce → Create → Page; this method peels
    /// through any wrapping <c>Create</c> activities to return the actual content object
    /// (a <see cref="ActivityObject"/> such as a Page, Note, or Article).
    /// </summary>
    private static IObject? UnwrapCreate(IObject? obj)
    {
        var current = obj;
        while (current is Create { Object: { } inner } && inner.FirstOrDefault() is IObject innerObj)
        {
            current = innerObj;
        }

        return current is ActivityObject ? current : null;
    }

    private Iri? AnnounceTargetIri
    {
        get
        {
            if (Item is not Announce)
            {
                return null;
            }

            if (ActivityEmbeddedObject is { Id: { Length: > 0 } id })
            {
                return new Iri(id);
            }

            if ((Item as Activity)?.Object?.FirstOrDefault() is ILink { Href: { } linkUri })
            {
                return new Iri(linkUri.OriginalString);
            }

            return null;
        }
    }

    private bool HasAnnounceContent
    {
        get
        {
            if (Item is not Announce)
            {
                return false;
            }

            if (ActivityEmbeddedObject is not { } embedded)
            {
                return false;
            }

            var content = embedded is ActivityObject ao ? JoinStrings(ao.Content) : null;
            return !string.IsNullOrWhiteSpace(content);
        }
    }

    /// <summary>
    /// The author of the boosted (announced) post — its <c>attributedTo</c>, falling back to the
    /// embedded object's own IRI when no author is present. Used for the inline boosted-post header so
    /// a boost reads as the original post (with a small "Boosted by" line) rather than the booster.
    /// </summary>
    private Iri? AnnounceAuthorIri
    {
        get
        {
            if (ActivityEmbeddedObject is not { } embedded)
            {
                return null;
            }

            if (embedded is ActivityObject eo && eo.AttributedTo?.FirstOrDefault()?.ResolveObjectIri() is { } author)
            {
                return author;
            }

            if (embedded.Id is { Length: > 0 } id)
            {
                return new Iri(id);
            }

            return null;
        }
    }

    private Iri? BareObjectIri
    {
        get
        {
            if (Obj is not (Note or Article) && !HasVoteData)
            {
                return null;
            }

            if (Obj!.Id is { Length: > 0 } id)
            {
                return new Iri(id);
            }

            return null;
        }
    }

    // ---- Activity-scoped (Create/Announce branch) metadata ----
    //
    // The home timeline and profile outbox render feed items as the wrapping activity (a Create),
    // not the embedded Note, so the direct-object properties above (which read Obj — the activity,
    // which carries no tag/attachment/poll of its own) are empty there. These read the embedded
    // content object instead, so the feed card can be built from all of the note's available
    // content (71.1) rather than just its text + image attachments.

    private IObject? ActivityContentObject => ActivityEmbeddedObject ?? Obj;

    private Iri? ActivityParentIri => ActivityEmbeddedObject?.GetParentIri();

    /// <summary>
    /// The parent ("in reply to") IRI for whichever branch is rendering: the activity-wrapped
    /// embedded object for <c>Create</c>/<c>Announce</c> (the home timeline renders the wrapping
    /// activity) or the bare object itself (a <c>Note</c> rendered on its own detail page).
    /// </summary>
    private Iri? EffectiveParentIri => ActivityParentIri ?? ParentIri;

    private IReadOnlyList<Iri> ActivityAudienceIris => FilterDisplayAudience(ActivityEmbeddedObject?.GetAudienceIris());

    private IReadOnlyList<Iri> ActivityMentionIris => ActivityEmbeddedObject?.GetMentionIris() ?? [];

    private IReadOnlyList<(string Name, Iri? Href)> ActivityHashtagTags => ActivityEmbeddedObject?.GetHashtagTags() ?? [];

    private DateTime? ActivityUpdated => ActivityEmbeddedObject?.GetUpdated();

    private bool ActivityIsSensitive => ActivityEmbeddedObject?.IsSensitive() ?? false;

    private bool ActivityMediaBlurred => ActivityIsSensitive && !ActivityRevealed;

    private bool ObjectMediaBlurred => IsSensitive && !Revealed;

    private string? ActivitySummary => ActivityEmbeddedObject?.GetSummary();

    /// <summary>
    /// Resolves the custom emojis for this object, preferring the embedded content object (for
    /// activity-wrapped items like <c>Create</c> where <c>Obj</c> is the activity, not the Note)
    /// and falling back to <c>Obj</c> itself (for direct-object rendering).
    /// </summary>
    private IReadOnlyList<(string Name, string ShortCode, Iri? Url)> ResolveEmojiTags()
        => (ActivityEmbeddedObject ?? Obj)?.GetCustomEmojis() ?? [];

    /// <summary>
    /// Filters follower-collection IRIs (e.g. <c>…/followers</c>) out of the audience list so the
    /// "To" line only shows concrete recipients. A follower-collection IRI is noise for anonymous
    /// visitors on the public timeline (121.5).
    /// </summary>
    private static IReadOnlyList<Iri> FilterDisplayAudience(IReadOnlyList<Iri>? audiences)
    {
        if (audiences is null || audiences.Count == 0) return [];
        var filtered = audiences.Where(a => !IsFollowerCollection(a)).ToList();
        return filtered.Count == audiences.Count ? audiences : filtered;
    }

    private static bool IsFollowerCollection(Iri iri)
    {
        var path = new Uri(iri.Value).AbsolutePath.TrimEnd('/');
        return path.EndsWith("/followers", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/following", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 121.7 — The boosted object for an <c>Announce</c>: the embedded object (when the feed item
    /// carries the full object) or the fetched object (when the target is a bare link and has been
    /// resolved in <see cref="OnInitializedAsync"/>). Null when neither is available.
    /// </summary>
    /// <summary>
    /// The object IRI the boosted-post card links to (the stretched-link overlay target): the rendered
    /// target's IRI (the original post when the boost is of a reply, the boosted object otherwise),
    /// falling back to the Announce's target IRI. Null when neither is available — then the card is not
    /// whole-card-clickable.
    /// </summary>
    private Iri? BoostCardLinkIri
        => BoostedRenderTarget is { Id: { Length: > 0 } id } ? new Iri(id)
        : AnnounceTargetIri;

    /// <summary>
    /// The banner-strip tint for a boosted-post card, derived from the boosted post's author (the same
    /// deterministic hue as every other content card, so the boosted post reads like any other post by
    /// that author).
    /// </summary>
    private string BoostCardHueStyle =>
        BoostedAuthorIri is { } iri ? $"--card-hue: {CardHue(iri.Value)};" : string.Empty;

    private IObject? BoostedObject
    {
        get
        {
            var raw = ActivityEmbeddedObject ?? _announcedObject;
            // Lemmy outbox Announce items nest as Announce → Create → Page. Unwrap the Create
            // so the boosted card renders the actual post content, not the wrapper activity.
            return UnwrapCreate(raw) ?? raw;
        }
    }

    /// <summary>
    /// The IRI of the rendered target content object (the Page/Note/Article), distinct from
    /// the Announce target IRI (which may point to the Create activity). Used for the
    /// EngagementBar so like/boost/reply counts target the post the card renders. (152 — when the boost
    /// is of a reply the target is the original post it answers to, so the bar shows that post's counts.)
    /// </summary>
    private Iri? BoostedContentIri
    {
        get
        {
            var target = BoostedRenderTarget;
            if (target is { Id: { Length: > 0 } id })
            {
                return new Iri(id);
            }

            return null;
        }
    }

    /// <summary>
    /// 121.7 — The author IRI of the boosted object, preferring the activity-level author and
    /// falling back to the boosted object's own <c>attributedTo</c>.
    /// </summary>
    private Iri? BoostedAuthorIri
    {
        get
        {
            // 152 — prefer the rendered target's attributedTo (the original post author when the boost is of
            // a reply, the boosted object's author otherwise).
            if (BoostedRenderTarget is ActivityObject { AttributedTo: { } at } &&
                at.FirstOrDefault()?.ResolveObjectIri() is { } author)
            {
                return author;
            }

            return AnnounceAuthorIri;
        }
    }

    /// <summary>
    /// 121.7 — The published time of the rendered target, preferring the target's <c>published</c> and
    /// falling back to the activity-level published. (152 — the target is the original post when the boost
    /// is of a reply, the boosted object otherwise.)
    /// </summary>
    private DateTime? BoostedPublished
    {
        get
        {
            if (BoostedRenderTarget is ActivityObject { Published: not null } p)
            {
                return p.Published;
            }

            return ActivityPublished;
        }
    }

    /// <summary>
    /// The <c>name</c> of the rendered target (the post title for Lemmy Page objects). (152 — the target is
    /// the original post when the boost is of a reply, the boosted object otherwise.)
    /// </summary>
    private string? BoostedName => (BoostedRenderTarget as ActivityObject)?.Name?.FirstOrDefault();

    /// <summary>
    /// 152 — The original post a boosted reply was replying to, when the boosted object is itself a
    /// reply. When non-null the boost card renders THIS object as the card body (the original post) and
    /// shows the "Boosted by" indicator, rather than the reply the booster boosted. Null when the boosted
    /// object is not a reply (top-level post) — the card then renders the boosted object itself.
    /// </summary>
    private IObject? BoostedReplyParent =>
        BoostedObject?.GetParentIri() is { } && _boostedParentObject is { } ? _boostedParentObject : null;

    /// <summary>
    /// 152 — The object the boost card renders as its body: the original post the boosted reply answers to
    /// (when the boosted object is a reply and its parent was resolved), otherwise the boosted object
    /// itself.
    /// </summary>
    private IObject? BoostedRenderTarget => BoostedReplyParent ?? BoostedObject;

    /// <summary>
    /// 152 — Whether the boost card is rendering the original post a boosted reply answers to (so it can
    /// show a "replying to …" context hint under the "Boosted by" line).
    /// </summary>
    private bool IsBoostOfReply => BoostedReplyParent is not null;

    /// <summary>
    /// 152 — The original post a liked reply was replying to, when the liked object is itself a reply.
    /// When non-null the like card renders THIS object as the card body (the original post) and shows the
    /// "Liked" indicator, rather than the reply the liker liked. Null otherwise — the card renders the
    /// liked object itself.
    /// </summary>
    private IObject? LikedReplyParent =>
        _likedObject?.GetParentIri() is { } && _likedParentObject is { } ? _likedParentObject : null;

    /// <summary>
    /// 152 — The object the like card renders as its body: the original post the liked reply answers to
    /// (when the liked object is a reply and its parent was resolved), otherwise the liked object itself.
    /// </summary>
    private IObject? LikedRenderTarget => LikedReplyParent ?? _likedObject;

    /// <summary>
    /// 152 — Whether the like card is rendering the original post a liked reply answers to (so it can show
    /// a "replying to …" context hint under the "Liked" line).
    /// </summary>
    private bool IsLikeOfReply => LikedReplyParent is not null;

    /// <summary>
    /// 152 — A short label for the reply target the boosted/liked object answers to, used for the
    /// "replying to …" context hint. Prefers the original post's author handle, falling back to its IRI.
    /// </summary>
    private string? ReplyContextLabel(IObject? replyParent)
    {
        if (replyParent is null)
        {
            return null;
        }

        var author = (replyParent as ActivityObject)?.AttributedTo?.FirstOrDefault()?.ResolveObjectIri();
        return author is { } a ? HandleOf(a) : (replyParent as ActivityObject)?.Id;
    }

    /// <summary>
    /// 139.1 F-3 — Whether the rendered target's <c>name</c> duplicates its <c>content</c> (HTML-stripped,
    /// case-insensitive). When a cross-posted Note is delivered to a platform that derives a Page's
    /// <c>name</c> from its content (Lemmy), the title and body are identical and rendering both
    /// duplicates the visible text. The render suppresses the title in that case. (152 — the target is the
    /// original post when the boost is of a reply, the boosted object otherwise.)
    /// </summary>
    private bool BoostedTitleDuplicatesContent
        => BoostedRenderTarget is ActivityObject bao
            && NameDuplicatesContent(bao.Name?.FirstOrDefault(), JoinStrings(bao.Content));

    /// <summary>
    /// 139.1 F-3 — Whether the <c>Create</c> branch's embedded object's <c>name</c> duplicates its
    /// <c>content</c> (the same cross-posted-Note case as <see cref="BoostedTitleDuplicatesContent"/>,
    /// for a direct <c>Create</c> rather than an <c>Announce</c> wrapper).
    /// </summary>
    private bool ActivityTitleDuplicatesContent
        => ActivityEmbeddedObject is ActivityObject aeo
            && NameDuplicatesContent(aeo.Name?.FirstOrDefault(), JoinStrings(aeo.Content));

    /// <summary>
    /// 139.1 F-3 — Reports whether a content object's <c>name</c> duplicates its <c>content</c> as plain
    /// text (HTML stripped, case-insensitive). A title identical to (or contained within) the body would
    /// render as a visible duplicate, so the UI suppresses the title when this is true.
    /// </summary>
    private static bool NameDuplicatesContent(string? name, string? content)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var nameText = HtmlStrip(name).Trim();
        var contentText = HtmlStrip(content).Trim();
        if (nameText.Length == 0 || contentText.Length == 0)
        {
            return false;
        }

        return nameText.Equals(contentText, StringComparison.OrdinalIgnoreCase)
            || contentText.Contains(nameText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips HTML tags from a string (139.1 F-3) so a <c>name</c> can be compared against its
    /// <c>content</c> as plain text.
    /// </summary>
    private static string HtmlStrip(string html)
        => System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");

    /// <summary>
    /// 121.7 — Renders the content of a boosted object (from either the embedded or the fetched
    /// object) as a safe HTML markup string, converting markdown to HTML when needed.
    /// </summary>
    private MarkupString RenderBoostedContent(IObject boosted)
    {
        var content = boosted is ActivityObject ao ? JoinStrings(ao.Content) : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return new MarkupString(string.Empty);
        }

        return new MarkupString(RenderObjectContent(boosted, content!));
    }

    /// <summary>
    /// 121.7 — Resolves rich attachments for a boosted object, preferring the embedded object's
    /// attachments (which may include server-rendered same-origin media) and falling back to the
    /// fetched object's attachments.
    /// </summary>
    private IReadOnlyList<RichAttachment> ResolveBoostedAttachments(IObject boosted)
    {
        // 152 — the caller passes the rendered target (the original post when the boost is of a reply,
        // the boosted object otherwise). Prefer that target's attachments; fall back to the embedded
        // object's (unwrapped, in case the target is still a wrapper Create activity) when the target has
        // none of its own.
        var targetAttachments = boosted.GetRichAttachments();
        if (targetAttachments.Count > 0)
        {
            return targetAttachments;
        }

        var unwrapped = UnwrapCreate(ActivityEmbeddedObject ?? boosted);
        if (unwrapped is { } u)
        {
            return u.GetRichAttachments();
        }

        return targetAttachments;
    }

    private PollData? ResolvePoll()
        => (ActivityEmbeddedObject ?? Obj)?.GetPollData();

    /// <summary>
    /// Returns the same-origin-rewritten URL for a rich attachment.
    /// </summary>
    private string RichAttachmentUrl(RichAttachment att) => RewriteMediaToSameOrigin(att.Url.Value);

    /// <summary>
    /// Returns the same-origin-rewritten preview URL for a rich attachment, or null.
    /// </summary>
    private string? RichAttachmentPreviewUrl(RichAttachment att)
        => att.Preview is { } p ? RewriteMediaToSameOrigin(p.Value) : null;

    /// <summary>
    /// Returns the display icon for a rich attachment based on its type.
    /// </summary>
    private static string RichAttachmentIcon(string? type) => type?.ToLowerInvariant() switch
    {
        "document" => "📄",
        "audio" => "🎵",
        "video" => "🎬",
        _ => "📎",
    };

    /// <summary>
    /// Returns true when the attachment should be rendered as a plain <c>&lt;img&gt;</c>
    /// (an <c>Image</c> type, or a null-type attachment with no preview).
    /// </summary>
    private static bool IsPlainImage(RichAttachment att)
        => att.Type is null or "Image" && att.Preview is null;

    private static string? JoinStrings(IEnumerable<string>? values)
        => values is null ? null : string.Join(" ", values);

    private static string AvatarInitial(Iri iri)
    {
        var handle = HandleOf(iri);
        return handle.Length > 0 ? char.ToUpper(handle[0]).ToString() : "?";
    }

    private static string ObjectHref(Iri iri) => $"/object?iri={Uri.EscapeDataString(iri.Value)}";

    private static string ActorHref(Iri iri) => ActorIdentityHelper.ActorHref(iri);

    private static string ActorHref(Iri iri, IObject? actor) => ActorIdentityHelper.ActorHref(iri, actor);

    /// <summary>
    /// The rendered (sanitized) summary for the <c>Actor</c> branch of the object view. An actor's
    /// <c>summary</c> is untrusted HTML from a remote instance, so it is sanitized (see
    /// <c>HtmlSanitizer</c>) before being emitted as a <see cref="MarkupString"/>.
    /// </summary>
    private MarkupString RenderedActorSummary
        => ActorIdentityHelper.RenderedSummary((Item as Actor)?.Summary?.FirstOrDefault());

    /// <summary>
    /// The href for a rendered hashtag: this instance's own hashtag search
    /// (<c>/search?q={#tag}</c>). A hashtag's authoring-server href (when one was supplied in the
    /// inbound <c>tag</c>) is a *foreign* URL (e.g. another instance's hashtag page) and is not useful
    /// for a reader browsing this instance, so the link always points at the local hashtag search — the
    /// same destination a reader would expect when tapping a <c>#tag</c> on this instance.
    /// </summary>
    internal static string HashtagHref(string name, Iri? href)
        => $"/search?q={Uri.EscapeDataString(name)}";

    private static string HandleOf(Iri iri)
    {
        var path = Uri.UnescapeDataString(new Uri(iri.Value).AbsolutePath).TrimEnd('/');
        var last = path.Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(last) ? iri.Value : last;
    }

    private static bool IsActorLink(string href)
    {
        if (string.IsNullOrEmpty(href))
        {
            return false;
        }

        return href.Contains("/ap/v1/u/", StringComparison.OrdinalIgnoreCase)
            || href.Contains("/u/", StringComparison.OrdinalIgnoreCase);
    }

    private static string RelativeTime(DateTime utc) => TimeFormatting.Relative(utc);

    private async Task VotePollAsync(int optionIndex)
    {
        if (_pollBusy || Poll is not { } poll || poll.Expired)
        {
            return;
        }

        if (Session.ActorId is not { } me || Session.LocalModeration is not { } local)
        {
            return;
        }

        var pollIri = (ActivityEmbeddedObject ?? Obj)?.Id;
        if (pollIri is null || !Iri.TryParse(pollIri, out var iri))
        {
            return;
        }

        _pollBusy = true;
        try
        {
            var result = await local.VoteAsync(me, iri, optionIndex);
            if (result.IsSuccess)
            {
                _pollVoted = true;
                _pollVotedOption = optionIndex;
                var updated = Iris.Core.Identity.IriExtensions.GetPollDataFromJson(result.Body);
                if (updated is not null)
                {
                    _pollOverride = updated;
                }
                else
                {
                    var options = poll.Options.Select((o, i) =>
                        i == optionIndex ? new PollOption(o.Title, o.Votes + 1) : o).ToList();
                    _pollOverride = new PollData(options, poll.TotalVotes + 1, poll.EndsAt, poll.Expired, poll.Multiple);
                }
            }
        }
        finally
        {
            _pollBusy = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Whether the signed-in viewer may moderate the given author: the viewer is signed in, the author
    /// is known, and the author is a distinct actor (you cannot moderate yourself from a card header).
    /// Shared by the <c>Create</c> and direct-object card headers for the Phase 102 right-side
    /// moderation buttons.
    /// </summary>
    private bool CanModerateAuthor(Iri? author)
        => author is { } a && Session.ActorId is { } me && a != me;

    /// <summary>
    /// Blocks the author from the card header (Phase 102). A fire-and-forget moderation action: it
    /// posts a <c>Block</c> via the signed client and re-renders on completion. Non-fatal on failure
    /// (the button simply stays enabled for a retry).
    /// </summary>
    /// <param name="author">The author actor's IRI (the moderation target).</param>
    private async Task CardBlockAsync(Iri author)
    {
        if (_cardModBusy || Session.ActorId is not { } me || Session.Client is not { } client)
        {
            return;
        }

        _cardModBusy = true;
        try
        {
            await client.BlockAsync(me, author, CancellationToken.None);
        }
        catch
        {
            // Non-fatal: the moderation delivery failed; the button remains for a retry.
        }
        finally
        {
            _cardModBusy = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Mutes the author from the card header (Phase 102). A local moderation action (a <c>Mute</c>
    /// against this instance's moderation endpoint), not a federated delivery. Fire-and-forget with the
    /// same non-fatal-on-failure contract as <see cref="CardBlockAsync(Iri)"/>.
    /// </summary>
    /// <param name="author">The author actor's IRI (the moderation target).</param>
    private async Task CardMuteAsync(Iri author)
    {
        if (_cardModBusy || Session.ActorId is not { } me || Session.LocalModeration is not { } mod)
        {
            return;
        }

        _cardModBusy = true;
        try
        {
            await mod.MuteAsync(me, author, CancellationToken.None);
        }
        catch
        {
            // Non-fatal.
        }
        finally
        {
            _cardModBusy = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Reports (flags) the author from the card header (Phase 102). A federated moderation report
    /// (a <c>Flag</c>) via the signed client. Fire-and-forget with the same non-fatal-on-failure
    /// contract as <see cref="CardBlockAsync(Iri)"/>.
    /// </summary>
    /// <param name="author">The author actor's IRI (the moderation target).</param>
    private async Task CardReportAsync(Iri author)
    {
        if (_cardModBusy || Session.ActorId is not { } me || Session.Client is not { } client)
        {
            return;
        }

        _cardModBusy = true;
        try
        {
            await client.FlagAsync(me, author, CancellationToken.None);
        }
        catch
        {
            // Non-fatal.
        }
        finally
        {
            _cardModBusy = false;
            StateHasChanged();
        }
    }

    protected override async Task OnInitializedAsync()
    {
        // Fetch the parent ("in reply to") object so the reply card can render it inline as a muted
        // context card (Phase 90.1). This covers both the activity branch (a Create/Announce wrapping
        // a Note — `ActivityParentIri`) and the direct-object branch (a Note rendered on its own
        // detail page — `ParentIri`). A short text preview is kept as a fallback link label when the
        // parent can't be rendered inline.
        var parentIri = EffectiveParentIri;
        if (parentIri is { } iri)
        {
            try
            {
                var parent = await Ui.GetContentObjectAsync(iri);
                if (parent is { } parentObj)
                {
                    _parentObject = parentObj;
                    _parentAuthorIri = (parentObj as ActivityObject)?.AttributedTo?.FirstOrDefault()?.ResolveObjectIri();
                    if (_parentAuthorIri is null && parentObj is Actor { Id: { Length: > 0 } actorId })
                    {
                        _parentAuthorIri = new Iri(actorId);
                    }

                    var content = (parentObj as ActivityObject)?.Content?.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        var text = System.Text.RegularExpressions.Regex.Replace(content, "<[^>]+>", " ").Trim();
                        _parentPreview = text.Length > 120 ? text[..120] + "…" : text;
                    }
                }
            }
            catch
            {
                // Non-fatal: parent context simply won't show.
            }
            finally
            {
                StateHasChanged();
            }
        }

        // 119.1 — a Like whose target is a bare link (the common case: the outbox Like carries only an
        // IRI, no embedded object) is resolved so the profile Likes tab renders the actual liked
        // content (author, text, media) instead of a bare IRI. Skipped when the Like already carries an
        // embedded target (nothing to fetch) or when no target IRI is present.
        //
        // The session accessor is scoped, so this component's instance may not have its signing key
        // loaded yet even though the parent page has (the client returns null until
        // EnsureReadyAsync completes). Prime it here before reading Session.Client.
        if (Item is Like
            && ActivityEmbeddedObject is null
            && LikeTargetIri is { } likedIri)
        {
            try
            {
                var liked = await Ui.GetContentObjectAsync(likedIri);
                if (liked is { } likedObj)
                {
                    _likedObject = likedObj;
                }
            }
            finally
            {
                StateHasChanged();
            }
        }

        // 121.7 — an Announce whose target is a bare link (the common case: the feed Announce carries
        // only an IRI, no embedded object) is resolved so the boost card renders a content preview
        // (author, text, media) instead of just "View boosted post →". Skipped when the Announce
        // already carries an embedded target (nothing to fetch) or when no target IRI is present.
        if (Item is Announce
            && ActivityEmbeddedObject is null
            && AnnounceTargetIri is { } announceIri)
        {
            try
            {
                var announced = await Ui.GetContentObjectAsync(announceIri);
                if (announced is { } announcedObj)
                {
                    _announcedObject = announcedObj;
                }
            }
            finally
            {
                StateHasChanged();
            }
        }

        // 152 — when the boosted object is itself a reply (its inReplyTo is set), fetch the ORIGINAL post
        // it answers to so the boost card can render that original post as its body (just with the
        // "Boosted by" indicator) rather than the reply. Runs after the boosted object is known (embedded
        // or fetched above). Best-effort: a fetch failure simply means the card renders the boosted
        // object (the reply) as before.
        if (Item is Announce && BoostedObject?.GetParentIri() is { } boostedReplyIri)
        {
            try
            {
                var parent = await Ui.GetContentObjectAsync(boostedReplyIri);
                if (parent is { } parentObj)
                {
                    _boostedParentObject = parentObj;
                    StateHasChanged();
                }
            }
            catch
            {
                // Non-fatal: the boost card renders the boosted reply itself.
            }
        }

        // 152 — when the liked object is itself a reply, fetch the ORIGINAL post it answers to so the like
        // card can render that original post as its body (just with the "Liked" indicator) rather than the
        // reply. Runs after the liked object is known (embedded or fetched above). Best-effort.
        if (Item is Like && _likedObject?.GetParentIri() is { } likedReplyIri)
        {
            try
            {
                var parent = await Ui.GetContentObjectAsync(likedReplyIri);
                if (parent is { } parentObj)
                {
                    _likedParentObject = parentObj;
                    StateHasChanged();
                }
            }
            catch
            {
                // Non-fatal: the like card renders the liked reply itself.
            }
        }

        // Lemmy score fetch: when the rendered post is a Lemmy post (a Page object with a
        // /post/{id} IRI), fetch its score data from the Lemmy REST API so the card can show
        // the vote count in a Lemmy-style layout. Best-effort: a failure to fetch the score
        // simply means the score is not displayed.
        var scoreIri = ResolveLemmyScoreIri();
        if (scoreIri is { } si)
        {
            try
            {
                var score = await Ui.GetLemmyPostScoreAsync(si, CancellationToken.None);
                if (score is not null)
                {
                    _lemmyScore = score;
                    StateHasChanged();
                }
            }
            catch
            {
                // Non-fatal: the score simply won't show.
            }
        }
    }

    /// <summary>
    /// The Lemmy score data for the current post, or null when the post is not a Lemmy post
    /// or the score fetch has not completed. Used by the template to display the vote count.
    /// </summary>
    public LemmyPostScore? LemmyScore => _lemmyScore;

    /// <summary>
    /// Resolves the IRI of the Lemmy post to fetch the score for, or null when the current
    /// item is not a Lemmy post. Handles both the direct Create branch (a Create of a Page)
    /// and the Announce branch (an Announce wrapping a Create wrapping a Page).
    /// </summary>
    private Iri? ResolveLemmyScoreIri()
    {
        IObject? contentObj = null;

        if (Item is Create)
        {
            contentObj = ActivityEmbeddedObject;
        }
        else if (Item is Announce)
        {
            contentObj = UnwrapCreate(ActivityEmbeddedObject ?? _announcedObject);
        }
        else
        {
            contentObj = Obj;
        }

        if (contentObj is { Id: { Length: > 0 } id } &&
            LemmyPostScore.TryParsePostIri(new Iri(id)) is not null)
        {
            return new Iri(id);
        }

        return null;
    }

    /// <summary>
    /// The first <c>formerType</c> value on a <see cref="Tombstone"/> (the AS2.0 type of the deleted
    /// object, e.g. "Note" / "Article"), or <c>null</c> when the tombstone carries none. Used by the
    /// tombstone card to label the placeholder ("Note post deleted" vs the generic "Post deleted").
    /// </summary>
    /// <param name="tombstone">The tombstone to read the former type from.</param>
    /// <returns>The first former-type string, or <c>null</c>.</returns>
    internal static string? TombstoneFormerType(Tombstone tombstone)
        => tombstone.FormerType?.FirstOrDefault();
}
