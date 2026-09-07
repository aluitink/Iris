using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Components;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Web.Components;

public static class ObjectViewActivityRenderer
{
    public static string? CreateActorIri(IObject activity)
        => (activity as Activity)?.Actor?.FirstOrDefault()?.ResolveObjectIri()?.Value;

    public static string? ActivityContent(IObject obj)
    {
        var content = obj is ActivityObject ao ? string.Join(" ", ao.Content ?? []) : null;
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    public static bool IsPreRendered(IObject obj) => obj.IsPreRenderedHtmlContent();

    public static MarkupString SafeContent(IObject obj)
    {
        var content = ActivityContent(obj);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new MarkupString(string.Empty);
        }

        return new MarkupString(
            IsPreRendered(obj) ? content! : System.Net.WebUtility.HtmlEncode(content!));
    }
}
