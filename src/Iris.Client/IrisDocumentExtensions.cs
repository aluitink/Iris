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

    /// <summary>
    /// Reads the <c>feed</c> extension property from an actor/community document, returning the IRI of the
    /// followed-feed collection (the home timeline: the union of the actor's local and remote follows'
    /// outbox items, or the community feed for a Group). The server advertises it unconditionally on both
    /// person and community documents. Returns <see langword="null"/> when absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="Object"/> with
    /// <see cref="Object.ExtensionData"/>). Must not be null.</param>
    /// <returns>The feed IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetFeedIri(this Object document) => GetCollectionIri(document, "feed");

    /// <summary>
    /// Reads the <c>members</c> extension property from a community document, returning the IRI of the
    /// members collection (the community's member actors, served at <c>/c/{name}/members</c>). Present
    /// only on community (Group) documents; absent on person documents. Returns <see langword="null"/>
    /// when the property is absent.
    /// </summary>
    /// <param name="document">The community document (an <see cref="Object"/> with
    /// <see cref="Object.ExtensionData"/>). Must not be null.</param>
    /// <returns>The members IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetMembersIri(this Object document) => GetCollectionIri(document, "members");

    /// <summary>
    /// Reads the <c>blocks</c> extension property from an actor/community document, returning the IRI of
    /// the blocks collection (the actors the actor has blocked, served at <c>/u/{handle}/blocks</c>).
    /// Returns <see langword="null"/> when absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="Object"/> with
    /// <see cref="Object.ExtensionData"/>). Must not be null.</param>
    /// <returns>The blocks IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetBlocksIri(this Object document) => GetCollectionIri(document, "blocks");

    /// <summary>
    /// Reads the <c>flags</c> extension property from an actor/community document, returning the IRI of
    /// the flags collection (the actors the actor has flagged, served at <c>/u/{handle}/flags</c>).
    /// Returns <see langword="null"/> when absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="Object"/> with
    /// <see cref="Object.ExtensionData"/>). Must not be null.</param>
    /// <returns>The flags IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetFlagsIri(this Object document) => GetCollectionIri(document, "flags");

    /// <summary>
    /// Reads the <c>mutes</c> extension property from an actor/community document, returning the IRI of
    /// the mutes collection (the actors the actor has muted, served at <c>/u/{handle}/mutes</c>).
    /// Returns <see langword="null"/> when absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="Object"/> with
    /// <see cref="Object.ExtensionData"/>). Must not be null.</param>
    /// <returns>The mutes IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetMutesIri(this Object document) => GetCollectionIri(document, "mutes");

    /// <summary>
    /// Reads the <c>star</c> (relays) extension property from an actor/community document, returning the
    /// IRI of the relays collection (the fan-out relays the actor subscribes to, the ActivityPub
    /// <c>star</c> set, served at <c>/u/{handle}/relays</c>). Advertised unconditionally (even when empty).
    /// Returns <see langword="null"/> when absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="Object"/> with
    /// <see cref="Object.ExtensionData"/>). Must not be null.</param>
    /// <returns>The relays IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetRelaysIri(this Object document) => GetCollectionIri(document, "star");

    /// <summary>
    /// Reads the <c>manuallyApprovesFollowers</c> gate state from a person (actor) document, returning
    /// <see langword="true"/> when the gate is set (inbound follows require manual approval),
    /// <see langword="false"/> when the term is present but not <c>true</c> (disabled), and
    /// <see langword="null"/> when the term is absent (the actor has no settings gate). This is the read
    /// half of <see cref="IActivityPubClient.SetManuallyApprovesFollowersAsync"/> (22.6.1): the server
    /// stores the gate on the actor's <see cref="Object.ExtensionData"/> and advertises it verbatim on the
    /// public document when set, so a client can read the actor's follow-approval policy from the document
    /// alone (no persistence access).
    /// </summary>
    /// <param name="document">The actor document (an <see cref="Object"/> with
    /// <see cref="Object.ExtensionData"/>). Must not be null.</param>
    /// <returns>
    /// <see langword="true"/> when the gate is set, <see langword="false"/> when present but disabled, and
    /// <see langword="null"/> when the term is absent.
    /// </returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetManuallyApprovesFollowers(this Object document)
        => GetManuallyApprovesGate(document, ActivityPubExtensionNames.ManuallyApprovesFollowers);

    /// <summary>
    /// Reads the <c>manuallyApprovesMembers</c> gate state from a community (Group) document, returning
    /// <see langword="true"/> when the gate is set (join requests require manual approval),
    /// <see langword="false"/> when the term is present but not <c>true</c> (disabled), and
    /// <see langword="null"/> when the term is absent (the community has no settings gate). This is the read
    /// half of <see cref="IActivityPubClient.SetManuallyApprovesMembersAsync"/> (change 217): the server
    /// stores the gate on the community's <see cref="Object.ExtensionData"/> and advertises it verbatim on
    /// the public document when set, so a client can read the community's membership-approval policy from
    /// the document alone (no persistence access).
    /// </summary>
    /// <param name="document">The community document (an <see cref="Object"/> with
    /// <see cref="Object.ExtensionData"/>). Must not be null.</param>
    /// <returns>
    /// <see langword="true"/> when the gate is set, <see langword="false"/> when present but disabled, and
    /// <see langword="null"/> when the term is absent.
    /// </returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetManuallyApprovesMembers(this Object document)
        => GetManuallyApprovesGate(document, ActivityPubExtensionNames.ManuallyApprovesMembers);

    /// <summary>
    /// Shared implementation for the settings-gate readers: reads the boolean-valued
    /// <paramref name="term"/> from <see cref="Object.ExtensionData"/>. Returns
    /// <see langword="true"/> when the term is JSON <c>true</c>, <see langword="false"/> when it is present
    /// but a different value (e.g. JSON <c>false</c>), and <see langword="null"/> when the term is absent.
    /// </summary>
    private static bool? GetManuallyApprovesGate(Object document, string term)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.ExtensionData is not { } ext ||
            !ext.TryGetValue(term, out var value))
        {
            return null;
        }

        return value.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    /// <summary>
    /// Shared implementation for the un-prefixed collection-endpoint extension readers: reads the
    /// string-valued <paramref name="term"/> from <see cref="Object.ExtensionData"/> and returns it as an
    /// <see cref="Iri"/>, or <see langword="null"/> when the term is absent or not a valid IRI.
    /// </summary>
    private static Iri? GetCollectionIri(Object document, string term)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.ExtensionData is not { } ext)
        {
            return null;
        }

        if (!ext.TryGetValue(term, out var value) ||
            value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        var str = value.GetString();
        return string.IsNullOrWhiteSpace(str) ? null : (Iri.TryParse(str, out var iri) ? iri : null);
    }
}
