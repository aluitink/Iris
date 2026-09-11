using System.Linq;
using System.Text;
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

    [Microsoft.AspNetCore.Components.Inject]
    private Iris.Web.Client.Accounts.IActorSessionAccessor Session { get; set; } = default!;

    private IObject? Obj => Item as IObject;

    private Iri? AuthorIri => (Obj as ActivityObject)?.AttributedTo?.FirstOrDefault()?.ResolveObjectIri();
    private Iri? ParentIri => Obj?.GetParentIri();
    private IReadOnlyList<Iri> MentionIris => Obj?.GetMentionIris() ?? [];
    private IReadOnlyList<(string Name, Iri? Href)> HashtagTags => Obj?.GetHashtagTags() ?? [];
    private IReadOnlyList<(string Name, string ShortCode, Iri? Url)> EmojiTags => ResolveEmojiTags();
    private PollData? Poll => _pollOverride ?? ResolvePoll();
    private IReadOnlyList<Iri> AudienceIris => Obj?.GetAudienceIris() ?? [];
    private PollData? _pollOverride;
    private bool _pollVoted;
    private int _pollVotedOption = -1;
    private bool _pollBusy;
    private DateTime? Published => Obj?.Published;
    private DateTime? Updated => Obj?.GetUpdated();
    private DateTime? ArticlePublishedTime => (ActivityEmbeddedObject ?? Obj)?.GetPublishedTime();
    private string? ArticleInLanguage => (ActivityEmbeddedObject ?? Obj)?.GetInLanguage();
    private TimeSpan? ArticleDuration => (ActivityEmbeddedObject ?? Obj) is ActivityObject { Duration: { } d } ? d : null;
    private bool IsSensitive => Obj?.IsSensitive() ?? false;
    private string? Summary => Obj?.GetSummary();
    private string? ActorName => (Obj as Actor)?.Name?.FirstOrDefault();

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
    /// Rewrites a media IRI (an absolute HTTPS URL) into a same-origin path
    /// (e.g. <c>https://iris.luit.ink/ap/v1/media/{id}</c> → <c>/ap/v1/media/{id}</c>) so the browser's
    /// <c>&lt;img&gt;</c> loads it same-origin (no CORS, no mixed-content). A relative IRI or a non-HTTPS
    /// URL is returned unchanged.
    /// </summary>
    internal static string RewriteMediaToSameOrigin(string mediaIri)
    {
        if (!Uri.TryCreate(mediaIri, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            return mediaIri;
        }

        return uri.AbsolutePath + uri.Query;
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
            return new MarkupString(
                Obj is { } o && o.IsPreRenderedHtmlContent() ? content : Markdown.ToHtml(content));
        }
    }

    private bool Revealed;
    private bool ActivityRevealed;

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

            return new MarkupString(
                embedded.IsPreRenderedHtmlContent() ? content! : Markdown.ToHtml(content!));
        }
    }

    private DateTime? ActivityPublished => ActivityEmbeddedObject?.Published;

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

    private bool IsContentCreate => Item is Create && ActivityEmbeddedObject is Note or Article;

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

    private Iri? BareObjectIri
    {
        get
        {
            if (Obj is not (Note or Article))
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

    private IReadOnlyList<Iri> ActivityAudienceIris => ActivityEmbeddedObject?.GetAudienceIris() ?? [];

    private IReadOnlyList<Iri> ActivityMentionIris => ActivityEmbeddedObject?.GetMentionIris() ?? [];

    private IReadOnlyList<(string Name, Iri? Href)> ActivityHashtagTags => ActivityEmbeddedObject?.GetHashtagTags() ?? [];

    private DateTime? ActivityUpdated => ActivityEmbeddedObject?.GetUpdated();

    private bool ActivityIsSensitive => ActivityEmbeddedObject?.IsSensitive() ?? false;

    private string? ActivitySummary => ActivityEmbeddedObject?.GetSummary();

    /// <summary>
    /// Resolves the custom emojis for this object, preferring the embedded content object (for
    /// activity-wrapped items like <c>Create</c> where <c>Obj</c> is the activity, not the Note)
    /// and falling back to <c>Obj</c> itself (for direct-object rendering).
    /// </summary>
    private IReadOnlyList<(string Name, string ShortCode, Iri? Url)> ResolveEmojiTags()
        => (ActivityEmbeddedObject ?? Obj)?.GetCustomEmojis() ?? [];

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

    private static string ActorHref(Iri iri) => $"/actor?iri={Uri.EscapeDataString(iri.Value)}";

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
}
