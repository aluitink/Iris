namespace Iris.Core;

/// <summary>
/// The canonical wire names of the non-core-AP (extension) JSON-LD terms that Iris reads and writes on
/// ActivityStreams actor/community documents. These terms are not modeled by the
/// <see cref="KristofferStrube.ActivityStreams"/> library, so they ride in the document's
/// <see cref="KristofferStrube.ActivityStreams.Object.ExtensionData"/>; the constants here are the single
/// source of truth for their string values so the client (which builds/reads them) and the server
/// (which renders/updates them) can never drift apart.
/// </summary>
/// <remarks>
/// These live in <c>Iris.Core</c> (not <c>Iris.Server</c>) because both the
/// <c>Iris.Client</c> write path (<c>SetManuallyApprovesFollowersAsync</c> /
/// <c>SetManuallyApprovesMembersAsync</c>) and the client's document readers
/// (<c>IrisDocumentExtensions.GetManuallyApprovesFollowers</c> /
/// <c>GetManuallyApprovesMembers</c>) emit/parse these terms, and <c>Iris.Client</c> may not depend on
/// <c>Iris.Server</c>. The server's <c>ActivityPubServerConstants</c> aliases these for compatibility.
/// </remarks>
public static class ActivityPubExtensionNames
{
    /// <summary>
    /// The ActivityPub actor property <c>manuallyApprovesFollowers</c>: when <c>true</c> on a local actor,
    /// an inbound follow is not auto-accepted — the actor (operator) must respond with an explicit
    /// <c>Accept</c> or <c>Reject</c>. The library's <c>Actor</c> type does not model this property, so it
    /// is carried in the actor's <c>ExtensionData</c> (seeded by the host) and echoed onto the public
    /// actor document (Resolved Decision #46).
    /// </summary>
    public const string ManuallyApprovesFollowers = "manuallyApprovesFollowers";

    /// <summary>
    /// The ActivityPub group extension property <c>manuallyApprovesMembers</c>: when <c>true</c> on a local
    /// community (group), an inbound <c>Join</c> activity from a remote actor is not auto-granted — the
    /// operator must respond with an explicit <c>Accept</c> or <c>Reject</c>. Carried in the group's
    /// <c>ExtensionData</c> (seeded by the host) and echoed onto the public group document. Communities
    /// without the flag retain the legacy auto-grant behavior.
    /// </summary>
    public const string ManuallyApprovesMembers = "manuallyApprovesMembers";

    /// <summary>
    /// The ActivityStreams actor property <c>publicKey</c>: the public signing key object
    /// (<c>id</c>/<c>owner</c>/<c>publicKeyPem</c>, plus the JWK fields <c>kty</c>/<c>n</c>/<c>e</c> when the
    /// server enriches it). The ActivityPub/ActivityStreams <em>spec</em> defines no compact <c>publicKey</c>
    /// term (it is a de-facto ecosystem convention — the JSON-LD community-profile and ActivityPub
    /// implementations emit it bare, and remote instances such as Mastodon expect exactly that shape), so
    /// — like the <c>manuallyApproves*</c> settings gates — it is emitted <strong>bare</strong> (not under the
    /// <c>iris:</c> namespace) to preserve cross-ecosystem interop. The library's <c>Actor</c> type does not
    /// model it, so it rides in the document's <c>ExtensionData</c>; this constant is the single source of
    /// truth for the term so every emitter (seeder, server render) and reader (inbound key resolver,
    /// client authenticators, key-id resolution) shares one string.
    /// </summary>
    public const string PublicKey = "publicKey";

    /// <summary>
    /// The owner-only <c>privateKey</c> extension property: the actor's private key as PKCS#8 PEM. Emitted
    /// <strong>bare</strong> only on the owner-authenticated document (never on the public document); the
    /// client's Basic/OAuth2 authenticators read it to load the signing key. Ecosystem convention (no
    /// compact spec term), so — like <see cref="PublicKey"/> — it stays bare rather than namespaced.
    /// </summary>
    public const string PrivateKey = "privateKey";

    /// <summary>
    /// The owner-only <c>keyAlgorithm</c> extension property: the label of the algorithm of the key carried
    /// by the document (<c>rsa</c>/<c>ecdsa-p256</c>/<c>ed25519</c>). Emitted <strong>bare</strong> only on the
    /// owner-authenticated document alongside <see cref="PrivateKey"/>; the client's authenticators read it
    /// to pick the key loader (PEM headers cannot distinguish RSA from EC). Ecosystem convention (no compact
    /// spec term), so it stays bare.
    /// </summary>
    public const string KeyAlgorithm = "keyAlgorithm";
}
