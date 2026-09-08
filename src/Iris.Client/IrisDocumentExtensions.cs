using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client;

/// <summary>
/// Extension methods for reading <c>iris:</c>-namespaced extension properties from ActivityStreams
/// actor/community documents (22.6.1). These are the JSON-LD extensions that the server advertises on
/// the public actor/community document (the <c>ExtensionData</c> dictionary) to surface specialized,
/// non-core-AP capabilities (settings, capabilities list, collection endpoints) for client discovery.
/// </summary>
public static class IrisDocumentExtensions
{
    /// <summary>
    /// The default <c>iris:</c> namespace base IRI (the canonical out-of-the-box value, matching the
    /// server's default when the deployment does not override <c>ActivityPubServerOptions.NamespaceIri</c>).
    /// A deployment that overrides the namespace must pass the same base to these readers; when it does
    /// not, this default applies on both sides.
    /// </summary>
    public const string DefaultNamespaceIri = "https://iris.example/ns#";

    /// <summary>
    /// Reads the <c>iris:settings</c> extension property from an actor/community document, returning
    /// the settings IRI (the IRI where AP-native settings change activities are published — the
    /// actor's outbox). Returns <see langword="null"/> when the property is absent (the actor/community
    /// has no settings surface).
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment-specific
    /// <c>ActivityPubServerOptions.NamespaceIri</c> value, or <see cref="DefaultNamespaceIri"/> when
    /// the deployment does not override it).</param>
    /// <returns>The settings IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetSettingsIri(this IObject document, string namespaceIri = DefaultNamespaceIri)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.ExtensionData is not { } ext)
        {
            return null;
        }

        var term = namespaceIri + IrisExtensionTerms.Settings;
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
    /// <param name="document">The actor or community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment-specific
    /// <c>ActivityPubServerOptions.NamespaceIri</c> value, or <see cref="DefaultNamespaceIri"/> when
    /// the deployment does not override it).</param>
    /// <returns>The list of capability values (empty when the property is absent).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static IReadOnlyList<string> GetCapabilities(this IObject document, string namespaceIri = DefaultNamespaceIri)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.ExtensionData is not { } ext)
        {
            return [];
        }

        var term = namespaceIri + IrisExtensionTerms.Capabilities;
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
    /// Reads the <c>iris:feed</c> extension property from an actor/community document, returning the IRI
    /// of the followed-feed collection (the home timeline: the union of the actor's local and remote
    /// follows' outbox items, or the community feed for a Group). The server advertises it unconditionally
    /// on both person and community documents, namespaced under the <c>iris:</c> namespace (the full IRI
    /// key <c>{namespaceIri}feed</c>). Returns <see langword="null"/> when absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The feed IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetFeedIri(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetCollectionIri(document, namespaceIri + CollectionExtensionNames.Feed);

    /// <summary>
    /// Reads the <c>members</c> extension property from a community document, returning the IRI of the
    /// members collection (the community's member actors, served at <c>/c/{name}/members</c>). Present
    /// only on community (Group) documents; absent on person documents. Unlike the Iris-invented
    /// collection endpoints, <c>members</c> is a core ActivityStreams Group term and is therefore emitted
    /// <strong>bare</strong> (not namespaced). Returns <see langword="null"/> when the property is absent.
    /// </summary>
    /// <param name="document">The community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <returns>The members IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetMembersIri(this IObject document) => GetCollectionIri(document, CollectionExtensionNames.Members);

    /// <summary>
    /// Reads the <c>iris:blocks</c> extension property from an actor/community document, returning the IRI
    /// of the blocks collection (the actors the actor has blocked, served at <c>/u/{handle}/blocks</c>).
    /// Namespaced under the <c>iris:</c> namespace (the full IRI key <c>{namespaceIri}blocks</c>). Returns
    /// <see langword="null"/> when absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The blocks IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetBlocksIri(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetCollectionIri(document, namespaceIri + CollectionExtensionNames.Blocks);

    /// <summary>
    /// Reads the <c>iris:flags</c> extension property from an actor/community document, returning the IRI
    /// of the flags collection (the actors the actor has flagged, served at <c>/u/{handle}/flags</c>).
    /// Namespaced under the <c>iris:</c> namespace (the full IRI key <c>{namespaceIri}flags</c>). Returns
    /// <see langword="null"/> when absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The flags IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetFlagsIri(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetCollectionIri(document, namespaceIri + CollectionExtensionNames.Flags);

    /// <summary>
    /// Reads the <c>iris:mutes</c> extension property from an actor/community document, returning the IRI
    /// of the mutes collection (the actors the actor has muted, served at <c>/u/{handle}/mutes</c>).
    /// Namespaced under the <c>iris:</c> namespace (the full IRI key <c>{namespaceIri}mutes</c>). Returns
    /// <see langword="null"/> when absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The mutes IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetMutesIri(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetCollectionIri(document, namespaceIri + CollectionExtensionNames.Mutes);

    /// <summary>
    /// Reads the <c>iris:star</c> (relays) extension property from an actor/community document, returning
    /// the IRI of the relays collection (the fan-out relays the actor subscribes to, served at
    /// <c>/u/{handle}/relays</c>). Iris reuses the AS <c>star</c> term to carry the relays set; it is
    /// namespaced under the <c>iris:</c> namespace (the full IRI key <c>{namespaceIri}star</c>) and
    /// advertised unconditionally (even when empty). Returns <see langword="null"/> when absent.
    /// </summary>
    /// <param name="document">The actor or community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The relays IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetRelaysIri(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetCollectionIri(document, namespaceIri + CollectionExtensionNames.Star);

    /// <summary>
    /// Reads the <c>iris:likedCount</c> extension property from a content object (including a nested
    /// object in a collection item), returning the number of distinct actors that have liked the object.
    /// This is a cacheable, per-object interaction counter (not per-requester): the server renders it on
    /// collection-page items (feed / outbox) and on the object document. Returns <see langword="null"/>
    /// when the property is absent (a non-Iris instance, or an object the server did not enrich).
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The like count, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static int? GetLikedCount(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetInt(document, namespaceIri + IrisExtensionTerms.LikedCount);

    /// <summary>
    /// Reads the <c>iris:sharedCount</c> extension property from a content object (including a nested
    /// object in a collection item), returning the number of distinct actors that have boosted
    /// (announced) the object. This is a cacheable, per-object interaction counter (not per-requester).
    /// Returns <see langword="null"/> when the property is absent.
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The boost count, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static int? GetSharedCount(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetInt(document, namespaceIri + IrisExtensionTerms.SharedCount);

    /// <summary>
    /// Reads the <c>iris:repliedCount</c> extension property from a content object (including a nested
    /// object in a collection item), returning the number of objects that reply to the object. This is a
    /// cacheable, per-object interaction counter (not per-requester). Returns <see langword="null"/> when
    /// the property is absent.
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The reply count, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static int? GetRepliedCount(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetInt(document, namespaceIri + IrisExtensionTerms.RepliedCount);

    /// <summary>
    /// Reads the <c>iris:isLiked</c> extension property from a content object, returning the
    /// <em>requesting</em> user's net like state on the object (per-requester, read-time state). Returns
    /// <see langword="true"/> when the term is present and <c>true</c> (the requester has liked the
    /// object), <see langword="false"/> when present but <c>false</c>, and <see langword="null"/> when the
    /// term is absent (the read was anonymous / unauthenticated, or the object is not a content object) —
    /// a caller that needs to distinguish "not liked" from "unknown" should treat <see langword="null"/>
    /// as "unknown" and fall back to reading the object's <c>/likes</c> collection.
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The requester's like state, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetIsLiked(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetBool(document, namespaceIri + IrisExtensionTerms.IsLiked);

    /// <summary>
    /// Reads the <c>iris:isShared</c> extension property from a content object, returning the
    /// <em>requesting</em> user's net boost (announce) state on the object (per-requester, read-time
    /// state). Returns <see langword="true"/> when the term is present and <c>true</c> (the requester has
    /// boosted the object), <see langword="false"/> when present but <c>false</c>, and
    /// <see langword="null"/> when the term is absent (the read was anonymous / unauthenticated) — a
    /// caller that needs to distinguish "not boosted" from "unknown" should treat <see langword="null"/>
    /// as "unknown" and fall back to reading the object's <c>/shares</c> collection.
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The requester's boost state, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetIsShared(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetBool(document, namespaceIri + IrisExtensionTerms.IsShared);

    /// <summary>
    /// Reads the <c>manuallyApprovesFollowers</c> gate state from a person (actor) document, returning
    /// <see langword="true"/> when the gate is set (inbound follows require manual approval),
    /// <see langword="false"/> when the term is present but not <c>true</c> (disabled), and
    /// <see langword="null"/> when the term is absent (the actor has no settings gate). This is the read
    /// half of <see cref="IActivityPubClient.SetManuallyApprovesFollowersAsync"/> (22.6.1): the server
    /// stores the gate on the actor's <see cref="IObject.ExtensionData"/> and advertises it verbatim on the
    /// public document when set, so a client can read the actor's follow-approval policy from the document
    /// alone (no persistence access).
    /// </summary>
    /// <param name="document">The actor document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <returns>
    /// <see langword="true"/> when the gate is set, <see langword="false"/> when present but disabled, and
    /// <see langword="null"/> when the term is absent.
    /// </returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetManuallyApprovesFollowers(this IObject document)
        => GetManuallyApprovesGate(document, ActivityPubExtensionNames.ManuallyApprovesFollowers);

    /// <summary>
    /// Reads the <c>manuallyApprovesMembers</c> gate state from a community (Group) document, returning
    /// <see langword="true"/> when the gate is set (join requests require manual approval),
    /// <see langword="false"/> when the term is present but not <c>true</c> (disabled), and
    /// <see langword="null"/> when the term is absent (the community has no settings gate). This is the read
    /// half of <see cref="IActivityPubClient.SetManuallyApprovesMembersAsync"/> (change 217): the server
    /// stores the gate on the community's <see cref="IObject.ExtensionData"/> and advertises it verbatim on
    /// the public document when set, so a client can read the community's membership-approval policy from
    /// the document alone (no persistence access).
    /// </summary>
    /// <param name="document">The community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <returns>
    /// <see langword="true"/> when the gate is set, <see langword="false"/> when present but disabled, and
    /// <see langword="null"/> when the term is absent.
    /// </returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetManuallyApprovesMembers(this IObject document)
        => GetManuallyApprovesGate(document, ActivityPubExtensionNames.ManuallyApprovesMembers);

    /// <summary>
    /// Shared implementation for the settings-gate readers: reads the boolean-valued
    /// <paramref name="term"/> from <see cref="IObject.ExtensionData"/>. Returns
    /// <see langword="true"/> when the term is JSON <c>true</c>, <see langword="false"/> when it is present
    /// but a different value (e.g. JSON <c>false</c>), and <see langword="null"/> when the term is absent.
    /// </summary>
    private static bool? GetManuallyApprovesGate(IObject document, string term)
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
    /// string-valued <paramref name="term"/> from <see cref="IObject.ExtensionData"/> and returns it as an
    /// <see cref="Iri"/>, or <see langword="null"/> when the term is absent or not a valid IRI.
    /// </summary>
    private static Iri? GetCollectionIri(IObject document, string term)
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

    /// <summary>
    /// Shared implementation for the integer-valued counter extension readers
    /// (<c>likedCount</c> / <c>sharedCount</c> / <c>repliedCount</c>): reads the int-valued
    /// <paramref name="term"/> from <see cref="IObject.ExtensionData"/> and returns it, or
    /// <see langword="null"/> when the term is absent or not a JSON integer.
    /// </summary>
    private static int? GetInt(IObject document, string term)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.ExtensionData is not { } ext ||
            !ext.TryGetValue(term, out var value))
        {
            return null;
        }

        if (value.ValueKind != System.Text.Json.JsonValueKind.Number)
        {
            return null;
        }

        return int.TryParse(value.GetRawText(), out var count) ? count : null;
    }

    /// <summary>
    /// Shared implementation for the boolean-valued extension readers
    /// (<c>isLiked</c> / <c>isShared</c>): reads the bool-valued <paramref name="term"/> from
    /// <see cref="IObject.ExtensionData"/> and returns <see langword="true"/> when it is JSON <c>true</c>,
    /// <see langword="false"/> when present but a different value (e.g. JSON <c>false</c>), and
    /// <see langword="null"/> when the term is absent.
    /// </summary>
    private static bool? GetBool(IObject document, string term)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.ExtensionData is not { } ext ||
            !ext.TryGetValue(term, out var value))
        {
            return null;
        }

        return value.ValueKind is System.Text.Json.JsonValueKind.True or
               System.Text.Json.JsonValueKind.False;
    }
}
