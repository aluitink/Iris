using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Web.Client.Components;

/// <summary>
/// Shared static helpers for actor identity rendering (icon IRI resolution, same-origin media
/// rewriting, avatar fallback initials, and handle derivation). Consolidates the per-component
/// copy-pasted logic that previously lived in <c>ObjectView</c>, <c>NotificationRow</c>,
/// <c>DirectoryCard</c>, and <c>ActorProfile</c>.
/// </summary>
public static class ActorIdentityHelper
{
    /// <summary>
    /// Resolves the actor's first <c>icon</c> IRI. Checks, in order: an <see cref="IObject"/> icon's
    /// <c>id</c> (Iris-local), then its <c>url</c> (Mastodon/remote — the AS vocabulary's
    /// <c>url</c> property, mapped to the library's <c>Url</c> property as
    /// <see cref="IEnumerable{T}"/>{<see cref="ILink"/>}), then an <see cref="ILink"/> icon's
    /// <c>href</c>. Returns null when the actor has no icon or no resolvable IRI.
    /// </summary>
    public static string? IconIri(IObject? actorDoc)
    {
        if (actorDoc?.Icon is not { } icons)
        {
            return null;
        }

        foreach (var icon in icons)
        {
            // Iris-local icons carry an `id` (the /ap/v1/media/{id} IRI).
            if (icon is IObject { Id: { Length: > 0 } id })
            {
                return id;
            }

            // Remote icons (Mastodon, etc.) carry a `url` instead of an `id`. The library maps the
            // AS vocabulary's `url` property to the `Url` property (IEnumerable<ILink>?), so read
            // the first link's href.
            if (icon is IObject { Url: { } urls } && urls.FirstOrDefault() is ILink { Href: { } urlHref })
            {
                if (urlHref.ToString() is { Length: > 0 } urlIri)
                {
                    return urlIri;
                }
            }

            // A bare link icon (a server that emits the icon as a plain IRI string).
            if (icon is ILink { Href: { } href })
            {
                return href.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Rewrites a media IRI to its browser-loadable same-origin form. Local media (the instance's own
    /// <c>/ap/v1/media/{id}</c>) is stripped to a relative path. Cross-origin external media is routed
    /// through the media proxy. A relative IRI or a non-HTTP(S) URL is returned unchanged.
    /// </summary>
    public static string RewriteMediaToSameOrigin(string mediaIri)
    {
        if (!Uri.TryCreate(mediaIri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return mediaIri;
        }

        if (uri.AbsolutePath.StartsWith("/ap/v1/media/", StringComparison.Ordinal))
        {
            return uri.AbsolutePath + uri.Query;
        }

        return $"/ap/v1/media/proxy?url={Uri.EscapeDataString(mediaIri)}";
    }

    /// <summary>
    /// A short display initial for the avatar fallback: the first letter-or-digit character of the
    /// display name or handle, uppercased. Returns "?" when neither is available.
    /// </summary>
    public static string AvatarInitial(string? displayName, string? handle)
    {
        var source = !string.IsNullOrWhiteSpace(displayName) ? displayName : handle;
        if (source is null)
        {
            return "?";
        }

        var first = source.Trim().FirstOrDefault(c => char.IsLetterOrDigit(c));
        return first != default ? char.ToUpperInvariant(first).ToString() : "?";
    }

    /// <summary>
    /// Derives a short handle from an actor IRI: the path's last segment (the username) when it
    /// looks like a user IRI (<c>/ap/v1/u/{user}</c>), else the full IRI. Returns the raw IRI
    /// value when it is not a parseable URI.
    /// </summary>
    public static string HandleOf(Iri iri)
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
    /// The actor detail page URL for an actor IRI.
    /// </summary>
    public static string ActorHref(Iri iri) => $"/actor?iri={Uri.EscapeDataString(iri.Value)}";

    /// <summary>
    /// Whether an actor's <c>name</c> is redundant with its <c>preferredUsername</c> (case-insensitive
    /// equal) — when redundant the name is omitted to avoid duplication.
    /// </summary>
    public static bool NameIsRedundant(string? name, string? preferredUsername)
        => !string.IsNullOrWhiteSpace(name)
            && !string.IsNullOrWhiteSpace(preferredUsername)
            && name!.Equals(preferredUsername, StringComparison.OrdinalIgnoreCase);
}
