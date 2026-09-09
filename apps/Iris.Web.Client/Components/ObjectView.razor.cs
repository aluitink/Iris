using System.Linq;
using System.Net;
using System.Text;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Web.Client.Components;

public partial class ObjectView
{
    [Parameter]
    public IObjectOrLink? Item { get; set; }

    private IObject? Obj => Item as IObject;

    private Iri? AuthorIri => (Obj as ActivityObject)?.AttributedTo?.FirstOrDefault()?.ResolveObjectIri();
    private Iri? ParentIri => Obj?.GetParentIri();
    private IReadOnlyList<Iri> MentionIris => Obj?.GetMentionIris() ?? [];
    private IReadOnlyList<(string Name, Iri? Href)> HashtagTags => Obj?.GetHashtagTags() ?? [];
    private IReadOnlyList<Iri> AudienceIris => Obj?.GetAudienceIris() ?? [];
    private DateTime? Published => Obj?.Published;
    private DateTime? Updated => Obj?.GetUpdated();
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

            return new MarkupString(
                Obj is { } o && o.IsPreRenderedHtmlContent() ? content : WebUtility.HtmlEncode(content));
        }
    }

    private bool Revealed;

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
                embedded.IsPreRenderedHtmlContent() ? content! : WebUtility.HtmlEncode(content!));
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
}
