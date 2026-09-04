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
}
