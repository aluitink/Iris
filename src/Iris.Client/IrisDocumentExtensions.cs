using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
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

    // --------------------------------------------------------------------------------------------
    // Server-capability detection + capability-aware feed/members IRI resolution (137.2).
    //
    // A remote community may be served by a server that does not expose the same collection
    // endpoints Iris does. Iris advertises its specialized, non-core-AP endpoints on the public
    // document (the <c>iris:feed</c> / bare <c>members</c> extensions above); a Mastodon- or
    // Pleroma-shaped server exposes <c>{actor}/feed</c> / <c>{community}/members</c> by convention; a
    // <strong>Lemmy</strong> server exposes neither — a Lemmy community's posts live in its
    // <c>outbox</c> (a collection of <c>Create</c>/<c>Announce</c> activities) and its members in its
    // <c>followers</c>. Rather than hardcoding a server's quirk at each call site, the client reads the
    // community's own document and resolves the feed/members IRIs by capability: prefer what the
    // document advertises, fall back to the server's convention, and (for Lemmy) remap to the routes it
    // actually serves. This keeps the client ActivityPub-native: it adapts to whatever server/service it
    // is talking to.
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>@context</c> IRI that identifies a document as authored by a <strong>Lemmy</strong> server
    /// (Lemmy stamps <c>https://join-lemmy.org/context.json</c> into the <c>@context</c> of every public
    /// document it serves).
    /// </summary>
    public const string LemmyContextIri = "https://join-lemmy.org/context.json";

    /// <summary>
    /// Reports whether a community document is a <strong>non-Iris Group</strong> whose content is served
    /// through its <c>outbox</c> and whose members are served through its <c>followers</c> — the standard
    /// ActivityStreams shape for a Group actor (the shape Lemmy, and any AP-compliant Group server,
    /// uses). A Group's posts are the <c>Create</c>/<c>Announce</c> activities in its outbox, and its
    /// members are its followers. This is the capability signal the client uses to resolve the feed
    /// (outbox) and members (followers) IRIs for a community that does not advertise Iris-specific
    /// endpoints (137.2).
    /// <para>
    /// An <strong>Iris</strong> community is a Group that advertises <c>iris:</c>-namespaced extension
    /// properties on its public document (e.g. <c>{namespace}feed</c>, <c>{namespace}capabilities</c>).
    /// The namespace base is deployment-configurable, so detection scans the <see cref="IObject.ExtensionData"/>
    /// for any key containing a <c>#</c> fragment separator (the JSON-LD namespace marker) rather than
    /// matching a fixed namespace. A Lemmy (or other non-Iris) Group document has no such keys — its
    /// <c>@context</c> is stripped by the JSON deserializer — so the absence of any <c>#</c>-containing
    /// extension key is the reliable "not Iris" signal.
    /// </para>
    /// </summary>
    /// <param name="document">The actor/community document (an <see cref="IObject"/>). Must not be null.</param>
    /// <returns><see langword="true"/> when the document is a <see cref="Group"/> with an <c>outbox</c> link and no <c>iris:</c>-namespaced extension properties.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool IsLemmy(this IObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Must be a Group with an outbox (the AP-standard shape for a community whose posts are
        // Create/Announce activities in its outbox and whose members are its followers).
        if (document is not Group { Outbox: not null })
        {
            return false;
        }

        // An Iris community advertises iris:-namespaced extension properties on its public document.
        // The namespace base is deployment-configurable (ActivityPubServerOptions.NamespaceIri), so we
        // cannot match a fixed namespace. Instead, scan the ExtensionData for any key containing a '#'
        // fragment separator — the JSON-LD namespace marker. A Lemmy (or other non-Iris) Group document
        // has no such keys (its @context is stripped by the JSON deserializer), so the absence of any
        // '#' -containing extension key is the reliable "not Iris" signal.
        if (document.ExtensionData is { } ext)
        {
            foreach (var key in ext.Keys)
            {
                if (key.AsSpan().Contains('#'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the community/actor's <strong>feed</strong> collection IRI by capability. The client reads
    /// the document's own advertised endpoints and the server it talks to, in priority order:
    /// <list type="number">
    /// <item>the <c>iris:feed</c> extension, when the server advertises it (an <em>Iris</em> instance —
    /// its feed endpoint, which also supports the <c>?q=</c> content-search the community feed search
    /// relies on);</item>
    /// <item>otherwise, for a <strong>Lemmy</strong> community, <c>{actor}/outbox</c> — Lemmy has no
    /// <c>/feed</c> endpoint; a community's posts are the <c>Create</c>/<c>Announce</c> activities in its
    /// outbox (rendered by the client's content-object view, which unwraps the activity's object);</item>
    /// <item>otherwise, <c>{actor}/feed</c> — the Mastodon/Pleroma convention for a followed feed.</item>
    /// </list>
    /// The single, capability-aware read a UI uses to point its feed collection at the right route for
    /// whatever server the community lives on (137.2).
    /// </summary>
    /// <param name="document">The community (or actor) document. Must not be null.</param>
    /// <param name="actorIri">The actor/community IRI (the base the fallback routes are appended to).</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's value, or
    /// <see cref="DefaultNamespaceIri"/> when it does not override it).</param>
    /// <returns>The feed collection IRI.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri ResolveFeedIri(this IObject document, Iri actorIri, string namespaceIri = DefaultNamespaceIri)
    {
        ArgumentNullException.ThrowIfNull(document);

        // 1. An Iris instance advertises its feed endpoint on the document (the only feed that also
        //    supports ?q= content search) — prefer it when present.
        if (GetCollectionIri(document, namespaceIri + CollectionExtensionNames.Feed) is { } irisFeed)
        {
            return irisFeed;
        }

        // 2. A Lemmy community has no /feed: its posts are the activities in its outbox.
        if (document.IsLemmy())
        {
            return actorIri.OutboxOf();
        }

        // 3. Mastodon/Pleroma convention for a followed feed.
        return actorIri.FeedOf();
    }

    /// <summary>
    /// Resolves the community's <strong>members</strong> collection IRI by capability. In priority order:
    /// the bare <c>members</c> extension, when the server advertises it (Iris / Mastodon-Pleroma, served at
    /// <c>{community}/members</c>); otherwise, for a <strong>Lemmy</strong> community, <c>{community}/followers</c>
    /// — Lemmy has no <c>/members</c> endpoint and exposes a community's members through its followers
    /// collection (137.2).
    /// </summary>
    /// <param name="document">The community document. Must not be null.</param>
    /// <param name="communityIri">The community IRI (the base the fallback route is appended to).</param>
    /// <returns>The members collection IRI.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri ResolveMembersIri(this IObject document, Iri communityIri)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (GetCollectionIri(document, CollectionExtensionNames.Members) is { } members)
        {
            return members;
        }

        if (document.IsLemmy())
        {
            return communityIri.FollowersOf();
        }

        return AppendPathSegment(communityIri, "members");
    }

    /// <summary>
    /// Reports whether the community's feed collection is a <strong>non-Iris, activity-shaped</strong> feed —
    /// i.e. one resolved to the server's outbox (Lemmy) rather than an Iris/Mastodon <c>/feed</c> endpoint.
    /// A UI uses this to decide whether to apply a content-item filter to the feed (an outbox is a mixed
    /// collection of <c>Create</c>/<c>Announce</c>/social activities and must be filtered to content items;
    /// an Iris/Mastodon feed is already content-only).
    /// </summary>
    /// <param name="document">The community document. Must not be null.</param>
    /// <param name="actorIri">The community IRI. Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI.</param>
    /// <returns><see langword="true"/> when the resolved feed is the Lemmy outbox (an activity feed).</returns>
    public static bool IsActivityFeed(this IObject document, Iri actorIri, string namespaceIri = DefaultNamespaceIri)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.GetFeedIri(namespaceIri) is null && document.IsLemmy();
    }

    /// <summary>
    /// Appends a single path segment to an absolute IRI (the client-side derivation of a fallback
    /// collection route, e.g. <c>{community}/members</c>). Mirrors the server's <c>IriExtensions</c>
    /// segment append for the one route that has no typed helper.
    /// </summary>
    private static Iri AppendPathSegment(Iri iri, string segment)
    {
        var builder = new UriBuilder(iri.Uri);
        var path = builder.Path;
        if (path.Length == 0 || !path.EndsWith('/'))
        {
            path += "/";
        }

        builder.Path = path + segment;
        return new Iri(builder.Uri);
    }

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
    /// Reads the <c>iris:dislikedCount</c> extension property from a content object (including a nested
    /// object in a collection item), returning the number of distinct actors that have disliked
    /// (downvoted) the object. This is a cacheable, per-object interaction counter (not per-requester).
    /// Returns <see langword="null"/> when the property is absent.
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The dislike count, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static int? GetDislikedCount(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetInt(document, namespaceIri + IrisExtensionTerms.DislikedCount);

    /// <summary>
    /// Reads the <c>iris:isDisliked</c> extension property from a content object, returning the
    /// <em>requesting</em> user's net dislike (downvote) state on the object (per-requester, read-time
    /// state). Returns <see langword="true"/> when the term is present and <c>true</c> (the requester has
    /// disliked the object), <see langword="false"/> when present but <c>false</c>, and
    /// <see langword="null"/> when the term is absent (the read was anonymous / unauthenticated, or the
    /// object is not a content object).
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The requester's dislike state, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetIsDisliked(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetBool(document, namespaceIri + IrisExtensionTerms.IsDisliked);

    /// <summary>
    /// Reads the <c>totalItems</c> from the <c>likes</c> collection on a content object, returning the
    /// number of likes as reported by the object's source instance. This is used for remote objects
    /// where the <c>iris:likedCount</c> extension is absent (the proxy returns the raw remote object,
    /// which includes the <c>likes</c> collection with its <c>totalItems</c>). Returns
    /// <see langword="null"/> when the property is absent or the collection is null.
    /// </summary>
    /// <param name="document">The content object. Must not be null.</param>
    /// <returns>The like count from the <c>likes.totalItems</c> property, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static int? GetLikesTotalItems(this IObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return GetCollectionTotalItems(document, "likes");
    }

    /// <summary>
    /// Reads the <c>totalItems</c> from the <c>shares</c> collection on a content object, returning the
    /// number of boosts/shares as reported by the object's source instance. This is used for remote
    /// objects where the <c>iris:sharedCount</c> extension is absent. Returns <see langword="null"/>
    /// when the property is absent or the collection is null.
    /// </summary>
    /// <param name="document">The content object. Must not be null.</param>
    /// <returns>The share count from the <c>shares.totalItems</c> property, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static int? GetSharesTotalItems(this IObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return GetCollectionTotalItems(document, "shares");
    }

    private static int? GetCollectionTotalItems(IObject document, string key)
    {
        // First check ExtensionData (for objects where the property is not modeled as a typed property)
        if (document.ExtensionData is { } ext && ext.TryGetValue(key, out var value))
        {
            if (value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("totalItems", out var totalItems)
                && totalItems.TryGetInt32(out var count))
            {
                return count;
            }
        }

        // Then check if the concrete Object type has typed Likes/Shares properties
        if (document is KristofferStrube.ActivityStreams.Object obj)
        {
            if (key == "likes" && obj.Likes?.TotalItems is { } likeCount)
            {
                return (int)likeCount;
            }
            if (key == "shares" && obj.Shares?.TotalItems is { } shareCount)
            {
                return (int)shareCount;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the <c>iris:postsCount</c> extension property from an actor/community document, returning the
    /// number of content posts (Note/Article objects) in the actor's outbox. This is a cacheable,
    /// per-actor counter (not per-requester): the server renders it on the public document and on
    /// directory (search) results so a client can display a "N posts" stat without first reading the
    /// outbox collection. Returns <see langword="null"/> when the property is absent (a non-Iris instance,
    /// or an actor whose outbox the server has not indexed).
    /// </summary>
    /// <param name="document">The actor or community document. Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The post count, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static int? GetPostsCount(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetInt(document, namespaceIri + IrisExtensionTerms.PostsCount);

    /// <summary>
    /// Reads the <c>iris:followersCount</c> extension property from an actor/community document, returning
    /// the number of actors following the actor (the followers-collection count). This is a cacheable,
    /// per-actor counter (not per-requester). Returns <see langword="null"/> when the property is absent.
    /// </summary>
    /// <param name="document">The actor or community document. Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The follower count, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static int? GetFollowersCount(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetInt(document, namespaceIri + IrisExtensionTerms.FollowersCount);

    /// <summary>
    /// Reads the <c>iris:followingCount</c> extension property from an actor/community document, returning
    /// the number of actors the actor follows (the following-collection count). This is a cacheable,
    /// per-actor counter (not per-requester). Returns <see langword="null"/> when the property is absent.
    /// </summary>
    /// <param name="document">The actor or community document. Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The following count, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static int? GetFollowingCount(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetInt(document, namespaceIri + IrisExtensionTerms.FollowingCount);

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
    /// Reads the <c>iris:likeActivityIri</c> extension property from a content object (including a nested
    /// object in a collection item), returning the IRI of the <see cref="KristofferStrube.ActivityStreams.Like"/>
    /// activity the <em>requesting</em> user issued against the object (72.2). Present only when the
    /// requester currently has a (net) like on the object (the <c>isLiked</c> edge stands). This is the
    /// minted activity id an unlike (an <c>Undo</c>) references: with it, a client un-likes by referencing
    /// the IRI directly instead of walking the object's <c>/likes</c> collection to recover it. Returns
    /// <see langword="null"/> when the property is absent (the request was anonymous, or the requester has
    /// not liked the object).
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The requester's minted Like activity IRI, or <see langword="null"/> when absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetLikeActivityIri(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetCollectionIri(document, namespaceIri + IrisExtensionTerms.LikeActivityIri);

    /// <summary>
    /// Reads the <c>iris:announceActivityIri</c> extension property from a content object (including a
    /// nested object in a collection item), returning the IRI of the <see cref="KristofferStrube.ActivityStreams.Announce"/>
    /// activity the <em>requesting</em> user issued against the object (72.2). Present only when the
    /// requester currently has a (net) boost on the object (the <c>isShared</c> edge stands). This is the
    /// minted activity id an un-boost (an <c>Undo</c>) references: with it, a client un-boosts by
    /// referencing the IRI directly instead of walking the object's <c>/shares</c> collection to recover
    /// it. Returns <see langword="null"/> when the property is absent (the request was anonymous, or the
    /// requester has not boosted the object).
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI (the deployment's
    /// <c>ActivityPubServerOptions.NamespaceIri</c>, or <see cref="DefaultNamespaceIri"/> when the
    /// deployment does not override it).</param>
    /// <returns>The requester's minted Announce activity IRI, or <see langword="null"/> when absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetAnnounceActivityIri(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetCollectionIri(document, namespaceIri + IrisExtensionTerms.AnnounceActivityIri);

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
    /// Reads the <c>iris:removedBy</c> extension property from a <c>Tombstone</c> document, returning the
    /// IRI of the actor who removed the object (138.23). Present only when the deleter is not the
    /// object's <c>attributedTo</c> owner (a moderator removal, not an author delete). Returns
    /// <see langword="null"/> when the property is absent (an author delete, or a non-Iris tombstone).
    /// </summary>
    /// <param name="document">The tombstone document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI.</param>
    /// <returns>The removing actor's IRI, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static Iri? GetRemovedBy(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetCollectionIri(document, namespaceIri + IrisExtensionTerms.RemovedBy);

    /// <summary>
    /// Reads the <c>iris:communityNsfw</c> extension property from a community (Group) document, returning
    /// <see langword="true"/> when the source community is flagged as NSFW/sensitive (138.24). When
    /// present and <c>true</c>, clients should render a content warning or age gate on all content from
    /// that community. Returns <see langword="false"/> when the term is present but <c>false</c>, and
    /// <see langword="null"/> when the term is absent (a non-NSFW community, a locally-created community,
    /// or a non-Iris document).
    /// </summary>
    /// <param name="document">The community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI.</param>
    /// <returns><see langword="true"/> when NSFW, <see langword="false"/> when present but not NSFW, or
    /// <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetCommunityNsfw(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetBool(document, namespaceIri + IrisExtensionTerms.CommunityNsfw);

    /// <summary>
    /// Reads the <c>iris:locked</c> extension property from a content object (including a nested object in
    /// a collection item), returning <see langword="true"/> when the source post/comment is locked — no
    /// new replies are accepted (138.24). When present and <c>true</c>, clients should disable the reply
    /// composer for that object. Returns <see langword="false"/> when the term is present but <c>false</c>,
    /// and <see langword="null"/> when the term is absent (the object is not locked, or a non-Iris
    /// document).
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI.</param>
    /// <returns><see langword="true"/> when locked, <see langword="false"/> when present but not locked,
    /// or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetLocked(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetBool(document, namespaceIri + IrisExtensionTerms.Locked);

    /// <summary>
    /// Reads the <c>iris:featured</c> extension property from a content object (including a nested object
    /// in a collection item), returning <see langword="true"/> when the object is featured (pinned) in its
    /// source community (138.24). When present and <c>true</c>, clients should render a pinned/featured
    /// indicator on the object. Returns <see langword="false"/> when the term is present but <c>false</c>,
    /// and <see langword="null"/> when the term is absent (the object is not featured, or a non-Iris
    /// document).
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI.</param>
    /// <returns><see langword="true"/> when featured, <see langword="false"/> when present but not
    /// featured, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetFeatured(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetBool(document, namespaceIri + IrisExtensionTerms.Featured);

    /// <summary>
    /// Reads the <c>iris:language</c> extension property from a content object (including a nested object
    /// in a collection item), returning the ISO-639 language code (e.g. <c>"en"</c>, <c>"de"</c>) of the
    /// content (138.24). Returns <see langword="null"/> when the property is absent (no language set, or a
    /// non-Iris document).
    /// </summary>
    /// <param name="document">The content object (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI.</param>
    /// <returns>The language code, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static string? GetLanguage(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetString(document, namespaceIri + IrisExtensionTerms.Language);

    /// <summary>
    /// Reads the <c>iris:postingRestrictedToMods</c> extension property from a community (Group) document,
    /// returning <see langword="true"/> when the source community restricts posting to moderators only
    /// (138.24). When present and <c>true</c>, clients should disable the post composer for
    /// non-moderator users in that community. Returns <see langword="false"/> when the term is present but
    /// <c>false</c>, and <see langword="null"/> when the term is absent (posting is not restricted, a
    /// locally-created community, or a non-Iris document).
    /// </summary>
    /// <param name="document">The community document (an <see cref="IObject"/> with
    /// <see cref="IObject.ExtensionData"/>). Must not be null.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI.</param>
    /// <returns><see langword="true"/> when restricted, <see langword="false"/> when present but not
    /// restricted, or <see langword="null"/> when the property is absent.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="document"/> is null.</exception>
    public static bool? GetPostingRestrictedToMods(this IObject document, string namespaceIri = DefaultNamespaceIri)
        => GetBool(document, namespaceIri + IrisExtensionTerms.PostingRestrictedToMods);

    /// <summary>
    /// Shared implementation for the string-valued extension readers (<c>language</c>): reads the
    /// string-valued <paramref name="term"/> from <see cref="IObject.ExtensionData"/> and returns it, or
    /// <see langword="null"/> when the term is absent or not a JSON string.
    /// </summary>
    private static string? GetString(IObject document, string term)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.ExtensionData is not { } ext ||
            !ext.TryGetValue(term, out var value))
        {
            return null;
        }

        return value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
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
    /// Determines whether a content object should be rendered behind a content warning (CW) overlay,
    /// combining the object's own <c>sensitive</c> flag with its source community's
    /// <c>iris:communityNsfw</c> flag (138.26). A Lemmy community can be flagged NSFW at the
    /// community level, which implicitly marks all its posts as sensitive even when individual posts
    /// do not carry the per-post <c>sensitive</c> flag. This helper returns <see langword="true"/>
    /// when either flag is set, so the client can apply a CW overlay without the server mutating
    /// stored posts (the community's NSFW flag can change later; retro-applying it to already-stored
    /// posts would be incorrect).
    /// </summary>
    /// <remarks>
    /// When <paramref name="community"/> is null (the object is not associated with a known
    /// community, e.g. a person-to-person note), only the object's own <c>sensitive</c> flag is
    /// considered. When the community document is present but does not carry
    /// <c>iris:communityNsfw</c> (a locally-created community or a non-Lemmy source), the community
    /// flag is treated as <see langword="false"/>.
    /// </remarks>
    /// <param name="content">The content object (a <see cref="Page"/>, <see cref="Note"/>, etc.).
    /// Must not be null.</param>
    /// <param name="community">The source community document (a <see cref="Group"/>), or null when the
    /// object is not community-associated.</param>
    /// <param name="namespaceIri">The <c>iris:</c> namespace base IRI.</param>
    /// <returns>
    /// <see langword="true"/> when the object's own <c>sensitive</c> flag is <c>true</c> OR the
    /// community's <c>iris:communityNsfw</c> flag is <c>true</c>; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">When <paramref name="content"/> is null.</exception>
    public static bool RequiresCw(
        IObject content,
        IObject? community,
        string namespaceIri = DefaultNamespaceIri)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.IsSensitive())
        {
            return true;
        }

        if (community is not null &&
            community.GetCommunityNsfw(namespaceIri) == true)
        {
            return true;
        }

        return false;
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
    /// (<c>isLiked</c> / <c>isShared</c> / <c>communityNsfw</c> / <c>locked</c> / <c>featured</c> /
    /// <c>postingRestrictedToMods</c>): reads the bool-valued <paramref name="term"/> from
    /// <see cref="IObject.ExtensionData"/> and returns <see langword="true"/> when it is JSON <c>true</c>,
    /// <see langword="false"/> when present but JSON <c>false</c>, and
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

        return value.ValueKind == System.Text.Json.JsonValueKind.True;
    }
}
