namespace Iris.Core;

/// <summary>
/// The canonical local terms of the <c>iris:</c>-namespaced JSON-LD extension properties that Iris
/// advertises on public actor/community documents and on collection-page documents. Unlike the
/// <see cref="ActivityPubExtensionNames"/> terms (which are emitted <strong>bare</strong> — ecosystem
/// conventions with no spec compact term), every term here is an <em>Iris-invented</em> extension and is
/// emitted under the deployment's <c>iris:</c> namespace base: the wire key is
/// <c>{NamespaceIri}{term}</c> (the namespace base is declared as <c>@vocab</c> in the document's
/// <c>@context</c>, and the property is written as the full IRI key so it can never collide with a core-AP
/// or vendor term).
/// </summary>
/// <remarks>
/// These live in <c>Iris.Core</c> so both the server (which renders the extensions) and the client
/// (which reads them via <c>IrisDocumentExtensions</c>) reference the same source of truth — a rename is
/// a compile error, not a silent wire drift. <c>Iris.Client</c> may not depend on <c>Iris.Server</c>, so
/// these terms cannot live in the server's <c>ActivityPubServerConstants</c>.
/// </remarks>
public static class IrisExtensionTerms
{
    /// <summary>
    /// The <c>capabilities</c> extension (Resolved Decision #11): the list of specialized, non-AP
    /// capabilities the actor/community supports (e.g. <c>["feed", "mute", "relay", "settings"]</c>),
    /// advertised for client discovery. The full wire key is <c>{NamespaceIri}capabilities</c>.
    /// </summary>
    public const string Capabilities = "capabilities";

    /// <summary>
    /// The <c>settings</c> extension (22.6.1): the IRI of the actor/community's settings surface (the
    /// AP-native settings change endpoint — an <c>Add</c>/<c>Remove</c> of the actor's own document
    /// carrying a settings flag, published to the outbox). Present when the actor/community has a
    /// settings gate (<c>manuallyApprovesFollowers</c>/<c>manuallyApprovesMembers</c>). The full wire key
    /// is <c>{NamespaceIri}settings</c>.
    /// </summary>
    public const string Settings = "settings";

    /// <summary>
    /// The <c>searchQuery</c> extension: on a search collection page, records the query the page was
    /// computed for (absent when no query was supplied). Carried by both the community search page and
    /// the instance-wide search page. The full wire key is <c>{NamespaceIri}searchQuery</c>.
    /// </summary>
    public const string SearchQuery = "searchQuery";

    /// <summary>
    /// The <c>isLiked</c> extension (per-object like state): a <c>bool</c> rendered on a content object's
    /// document that is <c>true</c> when the <em>requesting</em> user currently has a like on the object
    /// (the like edge is present — i.e. the object is in the requester's net <c>liked</c> state, after any
    /// intervening Like/Undo squashing to the present). Absent (omitted) when the request is unauthenticated
    /// or the requester has not liked the object. This is a per-requester, read-time convenience: the object
    /// document is served to each requester with their own <c>isLiked</c>, so the client can render a lit
    /// heart without first reading the requester's <c>/liked</c> collection. The full wire key is
    /// <c>{NamespaceIri}isLiked</c>.
    /// </summary>
    public const string IsLiked = "isLiked";

    /// <summary>
    /// The <c>isShared</c> extension (per-object boost state): a <c>bool</c> rendered on a content
    /// object's document that is <c>true</c> when the <em>requesting</em> user currently has a boost on
    /// the object (the announce edge is present — i.e. the object is in the requester's net
    /// <c>shared</c> state, after any intervening Announce/Undo squashing to the present). Absent
    /// (omitted) when the request is unauthenticated or the requester has not boosted the object. This is
    /// a per-requester, read-time convenience: the object document is served to each requester with their
    /// own <c>isShared</c>, so the client can render a lit boost marker without first reading the
    /// requester's <c>/announces</c> collection. The full wire key is <c>{NamespaceIri}isShared</c>.
    /// </summary>
    public const string IsShared = "isShared";

    /// <summary>
    /// The <c>refresh</c> collection capability extension: a <c>bool</c> advertised on a paged
    /// collection's page-1 <c>OrderedCollection</c> document that is <c>true</c> when the collection
    /// supports the <c>?refresh=true</c> query parameter (cache-bypass). Clients that read this flag can
    /// issue a <c>?refresh=true</c> request to force a re-render rather than relying on the
    /// <c>Cache-Control</c> TTL. The full wire key is <c>{NamespaceIri}refresh</c>.
    /// </summary>
    public const string Refresh = "refresh";

    /// <summary>
    /// The <c>query</c> collection capability extension: a <c>bool</c> advertised on a paged
    /// collection's page-1 <c>OrderedCollection</c> document that is <c>true</c> when the collection
    /// supports the <c>?q=...</c> content-filter query parameter. Clients that read this flag can issue
    /// a <c>?q=...</c> request to filter the collection's items by content/name. The full wire key is
    /// <c>{NamespaceIri}query</c>.
    /// </summary>
    public const string Query = "query";

    /// <summary>
    /// The <c>type</c> collection capability extension: a <c>bool</c> advertised on a paged
    /// collection's page-1 <c>OrderedCollection</c> document that is <c>true</c> when the collection
    /// supports the <c>?type=...</c> activity-type-filter query parameter. Clients that read this flag
    /// can issue a <c>?type=Create</c> request to filter the collection to only activities of that type.
    /// The full wire key is <c>{NamespaceIri}type</c>.
    /// </summary>
    public const string Type = "type";

    /// <summary>
    /// The <c>likedCount</c> extension: an <c>int</c> rendered on a content object's document (including
    /// nested objects in collection items) indicating the number of distinct actors that have liked the
    /// object (the like reverse-index count). This is a cacheable, per-object interaction counter: it is
    /// not per-requester, so it is safe to serve from the local collection-page response cache. The full
    /// wire key is <c>{NamespaceIri}likedCount</c>.
    /// </summary>
    public const string LikedCount = "likedCount";

    /// <summary>
    /// The <c>sharedCount</c> extension: an <c>int</c> rendered on a content object's document (including
    /// nested objects in collection items) indicating the number of distinct actors that have boosted
    /// (announced) the object (the announce reverse-index count). This is a cacheable, per-object
    /// interaction counter: it is not per-requester, so it is safe to serve from the local
    /// collection-page response cache. The full wire key is <c>{NamespaceIri}sharedCount</c>.
    /// </summary>
    public const string SharedCount = "sharedCount";

    /// <summary>
    /// The <c>repliedCount</c> extension: an <c>int</c> rendered on a content object's document (including
    /// nested objects in collection items) indicating the number of objects that reply to the object
    /// (the reply reverse-index count). This is a cacheable, per-object interaction counter: it is not
    /// per-requester, so it is safe to serve from the local collection-page response cache. The full wire
    /// key is <c>{NamespaceIri}repliedCount</c>.
    /// </summary>
    public const string RepliedCount = "repliedCount";
}
