namespace Iris.Core;

/// <summary>
/// The canonical <c>iris:</c>-namespaced collection-endpoint terms that Iris advertises on actor and
/// community documents to surface collections that have no core ActivityStreams/ActivityPub term. These
/// are the genuinely <em>Iris-invented</em> document properties (as opposed to the core-AP terms such as
/// <c>id</c>/<c>inbox</c>/<c>outbox</c>/<c>following</c>/<c>followers</c> and the ActivityStreams
/// <c>members</c> group term, which are emitted bare).
/// </summary>
/// <remarks>
/// The local (un-prefixed) name of each term is given here; the <strong>full wire key</strong> is the
/// deployment's <c>iris:</c> namespace base IRI (default <c>https://iris.example/ns#</c>) concatenated with
/// the local name, e.g. <c>https://iris.example/ns#feed</c>. On the wire the term appears as that full IRI
/// (the <c>iris:</c> prefix is not declared in <c>@context</c> as a compact term; the namespace is declared
/// via <c>@vocab</c>, so a JSON-LD processor resolves any bare local term to the same IRI). The terms are
/// written as full IRIs so they are unambiguous and cannot collide with a core-AP or vendor term (e.g.
/// Mastodon's <c>manuallyApprovesFollowers</c>, which stays bare by vendor convention).
/// <para>
/// These live in <c>Iris.Core</c> (not <c>Iris.Server</c>) so both the server (which advertises them) and
/// the client (which reads them via <c>IrisDocumentExtensions</c>) reference the same source of truth —
/// a rename is a compile error, not a silent wire drift. The <c>manuallyApproves*</c> settings gates are a
/// separate concern (Mastodon-compatible, bare) and live in <see cref="ActivityPubExtensionNames"/>.
/// </para>
/// </remarks>
public static class CollectionExtensionNames
{
    /// <summary>
    /// The <c>feed</c> collection — the actor's/community's home timeline (the union of local + remote
    /// follows' outbox items). Not a core AS Object property. Served at <c>{actor}/feed</c>.
    /// </summary>
    public const string Feed = "feed";

    /// <summary>
    /// The <c>blocks</c> collection — the actors the actor/community has blocked (F-07 moderation). Not a
    /// core AS Object property (a Mastodon-style extension). Served at <c>{actor}/blocks</c>.
    /// </summary>
    public const string Blocks = "blocks";

    /// <summary>
    /// The <c>flags</c> collection — the actors the actor/community has flagged (F-07 moderation). Not a
    /// core AS Object property (a Mastodon-style extension). Served at <c>{actor}/flags</c>.
    /// </summary>
    public const string Flags = "flags";

    /// <summary>
    /// The <c>mutes</c> collection — the actors the actor/community has muted (F-07 moderation; a mute is
    /// Iris-specific, no AS type exists). Served at <c>{actor}/mutes</c>.
    /// </summary>
    public const string Mutes = "mutes";

    /// <summary>
    /// The <c>search</c> collection — the community-scoped content/actor search (community documents
    /// only). Not a core AS Object property. Served at <c>{community}/search</c>.
    /// </summary>
    public const string Search = "search";

    /// <summary>
    /// The <c>star</c> collection — the fan-out <em>relays</em> the actor subscribes to (F-06). Iris reuses
    /// the AS <c>star</c> term to carry the relays set (a local actor's relay configuration); the endpoint
    /// is <c>{actor}/relays</c>. Advertised unconditionally (even when empty).
    /// </summary>
    public const string Star = "star";

    /// <summary>
    /// The <c>liked</c> collection — the objects the actor has liked (F-04). Served at <c>{actor}/liked</c>.
    /// The library models <c>liked</c> as a typed <c>Actor</c> property (emitted bare); it is listed here so
    /// the full Iris-advertised collection surface is documented in one place. Its wire form is bare
    /// <c>liked</c> (library-managed), unlike the other terms in this class, which are emitted under the
    /// <c>iris:</c> namespace.
    /// </summary>
    public const string Liked = "liked";

    /// <summary>
    /// The <c>members</c> collection — the community's member actors (served at <c>{community}/members</c>,
    /// community documents only). Unlike the Iris-invented collection endpoints, <c>members</c> is a core
    /// ActivityStreams <c>Group</c> term and is therefore emitted <strong>bare</strong> (not under the
    /// <c>iris:</c> namespace); it is listed here so the full advertised collection surface is documented in
    /// one place and the client's <c>GetMembersIri()</c> reader shares the canonical term.
    /// </summary>
    public const string Members = "members";
}
