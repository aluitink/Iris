using Iris.Core;
using KristofferStrube.ActivityStreams;
using Object = KristofferStrube.ActivityStreams.Object;

namespace Iris.Client;

/// <summary>
/// Extension methods for reading <c>iris:</c>-namespaced extension properties from ActivityStreams
/// actor/community documents (22.6.1). These are the JSON-LD extensions that the server advertises on
/// the public actor/community document (the <c>ExtensionData</c> dictionary) to surface specialized,
/// non-core-AP capabilities (settings, capabilities list) for client discovery.
/// </summary>
public static class IrisDocumentExtensions
{
    /// <summary>
    /// The default <c>iris:</c> namespace base IRI (matching the server's default when the deployment
    /// does not override <c>ActivityPubServerOptions.NamespaceIri</c>).
    /// </summary>
    public const string DefaultNamespaceIri = "https://iris.example/ns#";

    /// <summary>
    /// Reads the <c>iris:settings</c> extension property from an actor/community document, returning
    /// the settings IRI (the IRI where AP-native settings change activities are published — the
    /// actor's outbox). Returns <see langword="null"/> when the property is absent (the actor/community
    /// has no settings surface).
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="Object"/> with
    /// <see cref="Object.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment-specific
    /// <c>ActivityPubServerOptions.NamespaceIri</c> value, or <see cref="DefaultNamespaceIri"/> when
    /// the deployment does not override it).</param>
    /// <returns>The settings IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetSettingsIri(this Object document, string namespaceIri = DefaultNamespaceIri)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.ExtensionData is not { } ext)
        {
            return null;
        }

        var term = namespaceIri + "settings";
        if (!ext.TryGetValue(term, out var value) ||
            value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        var str = value.GetString();
        return string.IsNullOrWhiteSpace(str) ? null : (Iri.TryParse(str, out var iri) ? iri : null);
    }

    /// <summary>
    /// Reads the <c>iris:capabilities</c> extension property from an actor/community document, returning
    /// the list of capability values (e.g. <c>["feed", "members", "search", "mute", "settings"]</c>).
    /// Returns an empty list when the property is absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="Object"/> with
    /// <see cref="Object.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment-specific
    /// <c>ActivityPubServerOptions.NamespaceIri</c> value, or <see cref="DefaultNamespaceIri"/> when
    /// the deployment does not override it).</param>
    /// <returns>The list of capability values (empty when the property is absent).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static IReadOnlyList<string> GetCapabilities(this Object document, string namespaceIri = DefaultNamespaceIri)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.ExtensionData is not { } ext)
        {
            return [];
        }

        var term = namespaceIri + "capabilities";
        if (!ext.TryGetValue(term, out var value) ||
            value.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.String &&
                element.GetString() is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }
}
