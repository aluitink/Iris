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
    /// Resolves the actor's first <c>icon</c> IRI (an <see cref="IObject"/> icon by its <c>id</c>,
    /// an <see cref="ILink"/> icon by its <c>href</c>), or null when the actor has no icon.
    /// </summary>
    public static string? IconIri(IObject? actorDoc)
    {
        if (actorDoc?.Icon is not { } icons)
        {
            return null;
        }

        foreach (var icon in icons)
        {
            var iri = icon is IObject { Id: { Length: > 0 } id } ? id
                : icon is ILink { Href: { } href } ? href.ToString()
                : null;
            if (iri is { Length: > 0 })
            {
                return iri;
            }
        }

        return null;
    }

    /// <summary>
    /// Rewrites a media IRI (an absolute HTTPS URL) into a same-origin path so the browser's
    /// <c>&lt;img&gt;</c> loads it same-origin (no CORS, no mixed-content). A relative IRI or a
    /// non-HTTPS URL is returned unchanged.
    /// </summary>
    public static string RewriteMediaToSameOrigin(string mediaIri)
    {
        if (!Uri.TryCreate(mediaIri, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            return mediaIri;
        }

        return uri.AbsolutePath + uri.Query;
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
