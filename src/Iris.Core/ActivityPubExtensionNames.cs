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
}
